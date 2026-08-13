import { execFileSync } from "node:child_process";
import { existsSync, realpathSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

import { sanitizeHostEnvironment } from "./host-environment.mts";

export interface HostGitRepositoryIdentity {
  root: string;
  commonDir: string;
  fetchUrl: string;
  pushUrl: string;
}

export interface HostGit {
  validateRepository(): HostGitRepositoryIdentity;
  validateForDelivery(): HostGitRepositoryIdentity;
  assertNoPreservedWorktree(branch: string): void;
  currentBranch(): string;
  currentHeadSha(): string;
  readProjectConfigAtCommit(sha: string): string;
  assertCleanWorkingTree(): void;
  assertCommitExists(sha: string): void;
  branchSha(branch: string): string | undefined;
  ensureBranch(branch: string, startSha: string, expectedExistingSha?: string): void;
  restoreOwnedBranch(branch: string, startSha: string, recoveryRef: string): void;
  assertBranchSha(branch: string, expectedSha: string): void;
  isAncestor(ancestor: string, descendant: string): boolean;
  remoteBranchSha(branch: string): Promise<string | undefined>;
  pushExact(input: {
    branch: string;
    candidateSha: string;
    expectedRemoteSha: string | null;
  }): Promise<void>;
}

export type GitProcessRunner = (
  args: string[],
  options: {
    cwd: string;
    encoding: "utf8";
    env: Record<string, string>;
    stdio: ["ignore", "pipe", "pipe"];
  },
) => string;

export function buildExactPushArgs(
  remote: string,
  candidateSha: string,
  branch: string,
): string[] {
  assertSha(candidateSha);
  assertIssueBranch(branch);
  assertRemoteName(remote);
  return [
    "push",
    "--porcelain",
    "--no-follow-tags",
    "--recurse-submodules=no",
    remote,
    `${candidateSha}:refs/heads/${branch}`,
  ];
}

export function createHostGit(options: {
  root: string;
  gitPath: string;
  remote: string;
  expectedFetchUrl: string;
  expectedPushUrl: string;
  osHome: string;
  sourceEnvironment?: Record<string, string | undefined>;
  runGit?: GitProcessRunner;
}): HostGit {
  const root = realpathSync(options.root);
  const gitPath = validateExecutable(options.gitPath);
  const remote = options.remote;
  assertRemoteName(remote);
  assertRemoteUrlHasNoCredentials(options.expectedFetchUrl);
  assertRemoteUrlHasNoCredentials(options.expectedPushUrl);
  const environment = sanitizeHostEnvironment(
    options.sourceEnvironment ?? process.env,
    options.osHome,
  );
  environment.GIT_CONFIG_GLOBAL = "/dev/null";
  environment.GIT_CONFIG_NOSYSTEM = "1";
  const runGit: GitProcessRunner = options.runGit ?? ((args, commandOptions) =>
    execFileSync(gitPath, args, commandOptions));

  function git(...args: string[]): string {
    assertAllowedHostGitArgs(args);
    return runGit(withTrustedHostGitOptions(args), {
      cwd: root,
      encoding: "utf8",
      env: environment,
      stdio: ["ignore", "pipe", "pipe"],
    });
  }

  function tryGit(...args: string[]): { ok: true; output: string } | { ok: false } {
    try {
      return { ok: true, output: git(...args) };
    } catch (error) {
      const status = (error as { status?: number }).status;
      if (status === 1 || status === 128) return { ok: false };
      throw error;
    }
  }

  function branchSha(branch: string): string | undefined {
    assertIssueBranch(branch);
    const result = tryGit("rev-parse", "--verify", `refs/heads/${branch}^{commit}`);
    return result.ok ? normalizeSha(result.output) : undefined;
  }

  async function remoteBranchSha(branch: string): Promise<string | undefined> {
    validateRepositoryIdentity();
    assertIssueBranch(branch);
    const output = git(
      "ls-remote",
      "--refs",
      remote,
      `refs/heads/${branch}`,
    ).trim();
    if (!output) return undefined;
    const lines = output.split(/\r?\n/);
    if (lines.length !== 1) {
      throw new Error(`Remote returned ambiguous refs for ${branch}.`);
    }
    const [sha, ref] = lines[0]!.split(/\s+/);
    if (ref !== `refs/heads/${branch}` || !sha) {
      throw new Error(`Remote returned an invalid ref for ${branch}.`);
    }
    return normalizeSha(sha);
  }

  function isAncestor(ancestor: string, descendant: string): boolean {
    assertSha(ancestor);
    assertSha(descendant);
    return tryGit("merge-base", "--is-ancestor", ancestor, descendant).ok;
  }

  function assertBranchSha(branch: string, expectedSha: string): void {
    assertSha(expectedSha);
    const actual = branchSha(branch);
    if (actual !== expectedSha) {
      throw new Error(
        `Owned Issue branch ${branch} changed: expected ${expectedSha}, found ${String(actual)}.`,
      );
    }
  }

  function validateRepositoryIdentity(): HostGitRepositoryIdentity {
    const actualRoot = realpathSync(git("rev-parse", "--show-toplevel").trim());
    if (actualRoot !== root) {
      throw new Error(`Host Git repository root mismatch: ${actualRoot}.`);
    }
    const fetchUrls = exactUrlLines(git("remote", "get-url", "--all", remote));
    const pushUrls = exactUrlLines(git("remote", "get-url", "--all", "--push", remote));
    if (fetchUrls.length !== 1 || pushUrls.length !== 1 ||
        fetchUrls[0] !== options.expectedFetchUrl ||
        pushUrls[0] !== options.expectedPushUrl) {
      throw new Error("Host Git fetch/push remote identity mismatch.");
    }
    const fetchUrl = fetchUrls[0]!;
    const pushUrl = pushUrls[0]!;
    const localConfiguration = git(
      "config",
      "--local",
      "--includes",
      "--null",
      "--list",
    );
    const unsafeConfiguration = localConfiguration
      .split("\0")
      .filter(Boolean)
      .map((entry) => entry.split("\n", 1)[0]!.toLowerCase())
      .filter(isUnsafeLocalGitKey);
    if (unsafeConfiguration.length > 0) {
      throw new Error("Host Git repository contains forbidden routing, credential, hook, or transport configuration.");
    }
    const indexState = git("ls-files", "-v");
    if (indexState.split(/\r?\n/).some((line) =>
      line.length > 0 && (line[0] === "S" || /[a-z]/.test(line[0]!))
    )) {
      throw new Error(
        "Host Git index contains skip-worktree or assume-unchanged entries; owner-controlled files must remain observable.",
      );
    }
    const commonDirRaw = git("rev-parse", "--git-common-dir").trim();
    const commonDir = realpathSync(
      commonDirRaw.startsWith("/") ? commonDirRaw : resolve(root, commonDirRaw),
    );
    const replaceRefs = tryGit("for-each-ref", "--format=%(refname)", "refs/replace");
    if ((replaceRefs.ok && replaceRefs.output.trim()) ||
        existsSync(join(commonDir, "info", "grafts"))) {
      throw new Error("Host Git replacement refs or grafts are not permitted.");
    }
    return { root, commonDir, fetchUrl, pushUrl };
  }

  return {
    validateRepository() {
      return validateRepositoryIdentity();
    },
    validateForDelivery() {
      return validateRepositoryIdentity();
    },
    assertNoPreservedWorktree(branch) {
      assertIssueBranch(branch);
      const output = git("worktree", "list", "--porcelain");
      const marker = `branch refs/heads/${branch}`;
      if (output.split(/\n\n+/).some((record) => record.split(/\r?\n/).includes(marker))) {
        throw new Error(
          `A preserved Sandcastle worktree already owns ${branch}; inspect and clean it manually before retrying.`,
        );
      }
    },
    currentBranch() {
      const branch = git("branch", "--show-current").trim();
      if (!branch) {
        throw new Error("Trusted host checkout must be on a named branch.");
      }
      return branch;
    },
    currentHeadSha() {
      return normalizeSha(git("rev-parse", "HEAD"));
    },
    readProjectConfigAtCommit(sha) {
      assertSha(sha);
      return git("show", `${sha}:.sandcastle/project.json`);
    },
    assertCleanWorkingTree() {
      const status = git("status", "--porcelain=v1", "--untracked-files=all");
      if (status.length !== 0) {
        throw new Error(
          "The trusted host checkout is not clean; commit or move intended changes before starting an Issue round.",
        );
      }
    },
    assertCommitExists(sha) {
      assertSha(sha);
      const result = tryGit("cat-file", "-e", `${sha}^{commit}`);
      if (!result.ok) {
        throw new Error(
          `The verified GitHub commit ${sha} is not present locally; fetch it explicitly before retrying.`,
        );
      }
    },
    branchSha,
    ensureBranch(branch, startSha, expectedExistingSha) {
      assertSha(startSha);
      const current = branchSha(branch);
      if (current === undefined) {
        git("branch", branch, startSha);
        return;
      }
      const expected = expectedExistingSha ?? startSha;
      assertSha(expected);
      if (current !== expected) {
        throw new Error(
          `Owned Issue branch ${branch} drifted: expected ${expected}, found ${current}.`,
        );
      }
    },
    restoreOwnedBranch(branch, startSha, recoveryRef) {
      assertIssueBranch(branch);
      assertSha(startSha);
      if (!/^refs\/sandcastle\/recovery\/[A-Za-z0-9._/-]+$/.test(recoveryRef)) {
        throw new Error("Invalid Sandcastle recovery ref.");
      }
      const current = branchSha(branch);
      if (!current) throw new Error(`Owned Issue branch ${branch} does not exist.`);
      if (current === startSha) return;
      git("update-ref", recoveryRef, current);
      git("update-ref", `refs/heads/${branch}`, startSha, current);
    },
    assertBranchSha,
    isAncestor,
    remoteBranchSha,
    async pushExact(input) {
      const { branch, candidateSha, expectedRemoteSha } = input;
      assertSha(candidateSha);
      if (expectedRemoteSha !== null) assertSha(expectedRemoteSha);
      validateRepositoryIdentity();
      assertBranchSha(branch, candidateSha);
      const before = await remoteBranchSha(branch);
      if ((before ?? null) !== expectedRemoteSha) {
        throw new Error(
          `Remote drift for ${branch}: expected ${String(expectedRemoteSha)}, found ${String(before)}.`,
        );
      }
      if (before && !isAncestor(before, candidateSha)) {
        throw new Error(`Push for ${branch} would not be fast-forward.`);
      }
      git(...buildExactPushArgs(remote, candidateSha, branch));
      const after = await remoteBranchSha(branch);
      if (after !== candidateSha) {
        throw new Error(
          `Remote verification failed for ${branch}: expected ${candidateSha}, found ${String(after)}.`,
        );
      }
    },
  };
}

const TRUSTED_GITHUB_CREDENTIAL_HELPER = [
  "credential.https:/",
  "/github.com.helper=!/usr/bin/gh auth git-credential",
].join("");

function withTrustedHostGitOptions(args: readonly string[]): string[] {
  return [
    "-c",
    "core.hooksPath=/dev/null",
    "-c",
    "core.askPass=",
    "-c",
    "core.fsmonitor=false",
    "-c",
    "push.followTags=false",
    "-c",
    "remote.origin.tagOpt=--no-tags",
    "-c",
    "credential.helper=",
    "-c",
    TRUSTED_GITHUB_CREDENTIAL_HELPER,
    "-c",
    "credential.useHttpPath=true",
    "-c",
    "http.extraHeader=",
    ...args,
  ];
}

export function assertAllowedHostGitArgs(args: readonly string[]): void {
  if (args[0] === "-c") {
    if (args.length < 19 || args[1] !== "core.hooksPath=/dev/null" ||
        args[2] !== "-c" || args[3] !== "core.askPass=" ||
        args[4] !== "-c" || args[5] !== "core.fsmonitor=false" ||
        args[6] !== "-c" || args[7] !== "push.followTags=false" ||
        args[8] !== "-c" || args[9] !== "remote.origin.tagOpt=--no-tags" ||
        args[10] !== "-c" || args[11] !== "credential.helper=" ||
        args[12] !== "-c" ||
        args[13] !== TRUSTED_GITHUB_CREDENTIAL_HELPER ||
        args[14] !== "-c" || args[15] !== "credential.useHttpPath=true" ||
        args[16] !== "-c" || args[17] !== "http.extraHeader=") {
      throw new Error("Host Git command has forbidden configuration overrides.");
    }
    args = args.slice(18);
  }
  const command = args[0];
  if (!command) throw new Error("Host Git command is empty.");
  if (args.some((argument) =>
    argument === "-f" ||
    argument.startsWith("--force") ||
    argument === "--delete" ||
    argument === "--mirror" ||
    argument === "--prune" ||
    argument === "--tags" ||
    argument.startsWith("+") ||
    argument.startsWith(":refs/")
  )) {
    throw new Error("Forbidden destructive or force Git capability.");
  }

  if (command === "push") {
    if (args.length !== 6 || args[1] !== "--porcelain" ||
        args[2] !== "--no-follow-tags" ||
        args[3] !== "--recurse-submodules=no") {
      throw new Error("Host Git push must use the exact Sandcastle SHA form.");
    }
    assertRemoteName(args[4]!);
    const match = args[5]!.match(
      /^([0-9a-f]{40}|[0-9a-f]{64}):refs\/heads\/(sandcastle\/issue-[1-9][0-9]*)$/,
    );
    if (!match) throw new Error("Host Git push must use a literal SHA refspec.");
    return;
  }

  if (command === "branch") {
    if (args.length === 2 && args[1] === "--show-current") return;
    if (args.length !== 3) throw new Error("Host branch mutation is not allowed.");
    assertIssueBranch(args[1]!);
    assertSha(args[2]!);
    return;
  }

  if (command === "update-ref") {
    const ref = args[1] ?? "";
    if (args.length < 3 || args.length > 4 ||
        (!/^refs\/heads\/sandcastle\/issue-[1-9][0-9]*$/.test(ref) &&
          !/^refs\/sandcastle\/recovery\/[A-Za-z0-9._/-]+$/.test(ref))) {
      throw new Error("Host Git ref mutation is outside the Sandcastle namespace.");
    }
    assertSha(args[2]!);
    if (args[3]) assertSha(args[3]);
    return;
  }

  const readOnlyShape =
    command === "rev-parse" ||
    command === "merge-base" ||
    command === "for-each-ref" ||
    command === "worktree" && args[1] === "list" && args[2] === "--porcelain" ||
    command === "ls-remote" ||
    command === "status" ||
    command === "cat-file" ||
    (command === "show" && args.length === 2 &&
      /^(?:[0-9a-f]{40}|[0-9a-f]{64}):\.sandcastle\/project\.json$/.test(args[1]!)) ||
    (command === "ls-files" && args.length === 2 && args[1] === "-v") ||
    (command === "remote" && args[1] === "get-url") ||
    (command === "config" && args[1] === "--local" &&
      args[2] === "--includes" && args[3] === "--null" &&
      args[4] === "--list");
  if (!readOnlyShape) {
    throw new Error(`Host Git command is outside the allowlist: ${command}.`);
  }
}

function isUnsafeLocalGitKey(key: string): boolean {
  return key === "core.hookspath" || key === "core.fsmonitor" ||
    key === "core.sshcommand" || key === "extensions.worktreeconfig" ||
    key.startsWith("credential.") || key.startsWith("push.") ||
    key.startsWith("http.") || key.startsWith("include.") ||
    key.startsWith("includeif.") ||
    (/^url\..*\.(insteadof|pushinsteadof)$/.test(key)) ||
    (/^remote\..*\.(proxy|uploadpack|receivepack|pushurl)$/.test(key));
}

function exactUrlLines(value: string): string[] {
  return value.split(/\r?\n/).filter((line) => line.length > 0);
}

function validateExecutable(path: string): string {
  if (!path.startsWith("/")) throw new Error("Host Git executable must be absolute.");
  const resolved = realpathSync(path);
  const metadata = statSync(resolved);
  if (!metadata.isFile() || (metadata.mode & 0o111) === 0) {
    throw new Error(`Host Git executable is not a regular executable file: ${resolved}.`);
  }
  return resolved;
}

function assertIssueBranch(branch: string): void {
  if (!/^sandcastle\/issue-[1-9][0-9]*$/.test(branch)) {
    throw new Error(`Invalid Sandcastle Issue branch: ${branch}.`);
  }
}

function assertRemoteName(remote: string): void {
  if (!/^[A-Za-z0-9._-]+$/.test(remote)) {
    throw new Error(`Invalid Git remote name: ${remote}.`);
  }
}

function assertSha(sha: string): void {
  if (!/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(sha)) {
    throw new Error(`Invalid full Git object ID: ${sha}.`);
  }
}

function normalizeSha(raw: string): string {
  const sha = raw.trim().toLowerCase();
  assertSha(sha);
  return sha;
}

function assertRemoteUrlHasNoCredentials(value: string): void {
  if (!/^https?:\/\//.test(value)) return;
  const parsed = new URL(value);
  if (parsed.username || parsed.password) {
    throw new Error("Configured Git remote URL must not embed credentials.");
  }
}
