import { execFileSync } from "node:child_process";
import {
  chmod,
  copyFile,
  lstat,
  mkdir,
  mkdtemp,
  readlink,
  realpath,
  symlink,
  writeFile,
} from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { dirname, isAbsolute, join } from "node:path";

import { sanitizeHostEnvironment } from "./host-environment.mts";

const GIT_EXECUTABLE = "/usr/bin/git";
const GIT_READ_PREFIX = [
  "--no-optional-locks",
  "-c",
  "core.fsmonitor=false",
  "-c",
  "core.untrackedCache=false",
  "-c",
  "core.hooksPath=/dev/null",
] as const;

export type RepositoryState = Readonly<{
  branch: Buffer;
  head: Buffer;
  status: Buffer;
  refs: Buffer;
  worktrees: Buffer;
}>;

function gitEnvironment(extra: Record<string, string> = {}): Record<string, string> {
  return {
    ...sanitizeHostEnvironment(process.env, homedir()),
    GIT_CONFIG_GLOBAL: "/dev/null",
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_OPTIONAL_LOCKS: "0",
    GIT_PAGER: "cat",
    ...extra,
  };
}

function gitBuffer(repository: string, args: readonly string[]): Buffer {
  return execFileSync(
    GIT_EXECUTABLE,
    [...GIT_READ_PREFIX, "-C", repository, ...args],
    {
      env: gitEnvironment(),
      maxBuffer: 64 * 1024 * 1024,
    },
  );
}

function gitWrite(
  repository: string,
  args: readonly string[],
  extraEnvironment: Record<string, string> = {},
): void {
  execFileSync(GIT_EXECUTABLE, ["-C", repository, ...args], {
    env: gitEnvironment(extraEnvironment),
    stdio: "pipe",
  });
}

function decodeGitPath(raw: Buffer): string {
  const decoded = raw.toString("utf8");
  if (!Buffer.from(decoded, "utf8").equals(raw)) {
    throw new Error("The smoke snapshot cannot safely represent a non-UTF-8 Git path.");
  }
  if (
    decoded.length === 0 ||
    isAbsolute(decoded) ||
    decoded.split("/").some((part) => part === "" || part === "." || part === "..") ||
    decoded.split("/")[0] === ".git"
  ) {
    throw new Error(`Unsafe Git path in smoke snapshot: ${JSON.stringify(decoded)}`);
  }
  return decoded;
}

function listedSnapshotPaths(repository: string): string[] {
  const output = gitBuffer(repository, [
    "ls-files",
    "--cached",
    "--others",
    "--exclude-standard",
    "-z",
  ]);
  const result: string[] = [];
  let start = 0;
  for (let index = 0; index < output.length; index += 1) {
    if (output[index] !== 0) continue;
    if (index > start) result.push(decodeGitPath(output.subarray(start, index)));
    start = index + 1;
  }
  if (start !== output.length) {
    throw new Error("Git returned an unterminated path list for the smoke snapshot.");
  }
  return result;
}

async function copySnapshotPath(sourceRoot: string, targetRoot: string, relativePath: string) {
  const source = join(sourceRoot, relativePath);
  const target = join(targetRoot, relativePath);
  let metadata;
  try {
    metadata = await lstat(source);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
    throw error;
  }

  await mkdir(dirname(target), { recursive: true });
  if (metadata.isSymbolicLink()) {
    await symlink(await readlink(source), target);
    return;
  }
  if (!metadata.isFile()) {
    throw new Error(`Unsupported smoke snapshot entry type: ${relativePath}`);
  }
  await copyFile(source, target);
  await chmod(target, metadata.mode & 0o777);
}

/**
 * Capture only observable Git state. `--no-optional-locks` keeps these checks
 * read-only, including against the source repository's index and metadata.
 */
export function captureRepositoryState(repository: string): RepositoryState {
  return {
    branch: gitBuffer(repository, ["rev-parse", "--abbrev-ref", "HEAD"]),
    head: gitBuffer(repository, ["rev-parse", "--verify", "HEAD"]),
    status: gitBuffer(repository, [
      "status",
      "--porcelain=v2",
      "--branch",
      "-z",
      "--untracked-files=all",
    ]),
    refs: gitBuffer(repository, [
      "for-each-ref",
      "--sort=refname",
      "--format=%(refname)%00%(objectname)%00%(symref)",
    ]),
    worktrees: gitBuffer(repository, ["worktree", "list", "--porcelain"]),
  };
}

export function assertRepositoryStateUnchanged(
  before: RepositoryState,
  after: RepositoryState,
  description: string,
): void {
  const labels: ReadonlyArray<readonly [keyof RepositoryState, string]> = [
    ["branch", "branch"],
    ["head", "HEAD"],
    ["status", "Git status"],
    ["refs", "Git refs"],
    ["worktrees", "Git worktrees"],
  ];
  for (const [key, label] of labels) {
    if (!before[key].equals(after[key])) {
      throw new Error(`${description} ${label} changed byte-for-byte during the smoke test.`);
    }
  }
}

export function createDisposableSnapshotBranch(repository: string, branch: string): void {
  gitWrite(repository, ["check-ref-format", "--branch", branch]);
  gitWrite(repository, ["branch", branch, "HEAD"]);
}

/**
 * Copy the current working-tree view (tracked plus nonignored untracked files)
 * into a new repository. No source-repository ref, index, config, or worktree is
 * written. The caller owns and must remove the returned temporary directory.
 */
export async function createDisposableGitSnapshot(
  sourceRepository: string,
  temporaryParent = tmpdir(),
): Promise<string> {
  const sourceRoot = await realpath(sourceRepository);
  const targetRoot = await mkdtemp(join(temporaryParent, "sandcastle-smoke-snapshot-"));
  const paths = listedSnapshotPaths(sourceRoot);

  for (const relativePath of paths) {
    await copySnapshotPath(sourceRoot, targetRoot, relativePath);
  }

  execFileSync(
    GIT_EXECUTABLE,
    ["init", "--quiet", "--initial-branch=snapshot-base", targetRoot],
    { env: gitEnvironment(), stdio: "pipe" },
  );
  gitWrite(targetRoot, ["config", "user.name", "Sandcastle Smoke"]);
  gitWrite(targetRoot, [
    "config",
    "user.email",
    "sandcastle-smoke.invalid@example.invalid",
  ]);
  gitWrite(targetRoot, ["config", "commit.gpgSign", "false"]);
  gitWrite(targetRoot, ["config", "core.autocrlf", "false"]);
  gitWrite(targetRoot, ["config", "core.fileMode", "true"]);
  gitWrite(targetRoot, ["config", "core.hooksPath", "/dev/null"]);
  // The snapshot is a byte-preserving transport, not a checkout conversion.
  // Override project/global clean filters and EOL normalization for this repo.
  await writeFile(join(targetRoot, ".git", "info", "attributes"), "* -text -filter\n");
  gitWrite(targetRoot, ["add", "--all"]);
  gitWrite(
    targetRoot,
    ["commit", "--quiet", "--allow-empty", "-m", "Sandcastle smoke snapshot"],
    {
      GIT_AUTHOR_DATE: "2000-01-01T00:00:00Z",
      GIT_COMMITTER_DATE: "2000-01-01T00:00:00Z",
    },
  );

  const clean = gitBuffer(targetRoot, [
    "status",
    "--porcelain=v2",
    "-z",
    "--untracked-files=all",
  ]);
  if (clean.length !== 0) {
    throw new Error("Disposable Sandcastle smoke snapshot is not clean after its commit.");
  }
  return targetRoot;
}
