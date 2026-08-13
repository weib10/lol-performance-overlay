import {
  closeSync,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
} from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { userInfo } from "node:os";

import { sanitizeHostEnvironment } from "./host-environment.mts";

interface BindMountCreateOptionsLike {
  worktreePath: string;
  hostRepoPath: string;
  mounts: Array<{
    hostPath: string;
    sandboxPath: string;
    readonly?: boolean;
  }>;
  env: Record<string, string>;
}

interface BindMountProviderLike {
  tag: "bind-mount";
  name: string;
  env: Record<string, string>;
  sandboxHomedir?: string;
  create(options: BindMountCreateOptionsLike): Promise<unknown>;
}

export interface GitControlPlaneSnapshot {
  commonGitDir: string;
  worktreeGitDir: string;
  worktreeGitPointer: string;
  worktreeCommonPointer: string;
  worktreeBackPointer: string;
  entries: ReadonlyArray<{
    path: string;
    type: "file" | "directory";
    mode: number;
    content?: string;
  }>;
}

const SAFE_HOST_PATH = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
const HOST_GIT_CONFIG = [
  ["core.hooksPath", "/dev/null"],
  ["core.fsmonitor", "false"],
  ["credential.helper", ""],
  ["http.extraHeader", ""],
  ["push.followTags", "false"],
] as const;

const FORBIDDEN_PROCESS_KEY = /^(?:LD_|DYLD_|_RLD|GIT_|HTTP_PROXY$|HTTPS_PROXY$|ALL_PROXY$|NO_PROXY$|http_proxy$|https_proxy$|all_proxy$|no_proxy$)/;
const FORBIDDEN_PROCESS_EXACT = new Set([
  "BASH_ENV",
  "ENV",
  "CDPATH",
  "SHELLOPTS",
  "PROMPT_COMMAND",
  "NODE_OPTIONS",
  "PERL5OPT",
  "RUBYOPT",
  "PYTHONPATH",
  "SSH_AUTH_SOCK",
]);

/** Build the only environment inherited by Sandcastle's trusted host tools. */
export function trustedHostProcessEnvironment(
  source: Record<string, string | undefined>,
  osHome: string = userInfo().homedir,
): Record<string, string> {
  const environment = sanitizeHostEnvironment(source, osHome);
  for (const key of Object.keys(environment)) {
    if (FORBIDDEN_PROCESS_KEY.test(key) || FORBIDDEN_PROCESS_EXACT.has(key)) {
      delete environment[key];
    }
  }
  environment.HOME = osHome;
  environment.PATH = SAFE_HOST_PATH;
  environment.GIT_TERMINAL_PROMPT = "0";
  environment.GIT_NO_REPLACE_OBJECTS = "1";
  environment.GIT_CONFIG_COUNT = String(HOST_GIT_CONFIG.length);
  HOST_GIT_CONFIG.forEach(([key, value], index) => {
    environment[`GIT_CONFIG_KEY_${index}`] = key;
    environment[`GIT_CONFIG_VALUE_${index}`] = value;
  });
  return environment;
}

let trustedHostGuardActive = false;

/**
 * Sandcastle 0.12.0 launches several host-side Git children internally. It has
 * no environment injection seam, so this process-scoped guard is held across
 * each official API call. The worker's process lock guarantees no concurrent
 * Issue round shares this environment.
 */
export async function withTrustedHostProcess<T>(
  operation: () => Promise<T> | T,
): Promise<T> {
  if (trustedHostGuardActive) return operation();
  trustedHostGuardActive = true;
  const previous = { ...process.env };
  const controlled = trustedHostProcessEnvironment(previous);
  replaceProcessEnvironment(controlled);
  try {
    return await operation();
  } finally {
    replaceProcessEnvironment(previous);
    trustedHostGuardActive = false;
  }
}

/**
 * Sandcastle 0.12.0 deliberately bind-mounts the common Git directory so an
 * agent can commit. Overlay the executable Git control files read-only while
 * leaving objects and the named Issue ref writable.
 */
export function protectGitControlPlane<T extends BindMountProviderLike>(
  provider: T,
): T {
  if (provider.tag !== "bind-mount") {
    throw new Error("Git control-plane protection requires a bind-mount provider.");
  }
  return {
    ...provider,
    env: {
      ...provider.env,
      GIT_CONFIG_COUNT: "3",
      GIT_CONFIG_KEY_0: "core.fsmonitor",
      GIT_CONFIG_VALUE_0: "false",
      GIT_CONFIG_KEY_1: "core.hooksPath",
      GIT_CONFIG_VALUE_1: "/dev/null",
      GIT_CONFIG_KEY_2: "commit.gpgSign",
      GIT_CONFIG_VALUE_2: "false",
      GIT_NO_REPLACE_OBJECTS: "1",
    },
    async create(options: BindMountCreateOptionsLike) {
      const paths = resolveGitControlPaths(
        options.hostRepoPath,
        options.worktreePath,
        true,
      );
      const sandboxWorktreePath = options.mounts.find(
        (mount) => resolve(mount.hostPath) === resolve(options.worktreePath),
      )?.sandboxPath;
      if (!sandboxWorktreePath || !isAbsolute(sandboxWorktreePath)) {
        throw new Error("Sandcastle did not provide an absolute worktree bind mount.");
      }
      const protectedMounts = [
        readonlyMount(
          paths.worktreeGitPointer,
          join(sandboxWorktreePath, ".git"),
        ),
        readonlyMount(paths.worktreeCommonPointer),
        readonlyMount(paths.worktreeBackPointer),
        readonlyMount(paths.commonConfig),
        readonlyMount(paths.commonHooks),
        readonlyMount(paths.worktreeConfig),
      ];
      return provider.create({
        ...options,
        env: {
          ...options.env,
          GIT_CONFIG_COUNT: "3",
          GIT_CONFIG_KEY_0: "core.fsmonitor",
          GIT_CONFIG_VALUE_0: "false",
          GIT_CONFIG_KEY_1: "core.hooksPath",
          GIT_CONFIG_VALUE_1: "/dev/null",
          GIT_CONFIG_KEY_2: "commit.gpgSign",
          GIT_CONFIG_VALUE_2: "false",
          GIT_NO_REPLACE_OBJECTS: "1",
        },
        mounts: [...options.mounts, ...protectedMounts],
      });
    },
  } as T;
}

export function captureGitControlPlane(
  repoRoot: string,
  worktreePath: string,
): GitControlPlaneSnapshot {
  const paths = resolveGitControlPaths(repoRoot, worktreePath, false);
  return {
    commonGitDir: paths.commonGitDir,
    worktreeGitDir: paths.worktreeGitDir,
    worktreeGitPointer: paths.worktreeGitPointer,
    worktreeCommonPointer: paths.worktreeCommonPointer,
    worktreeBackPointer: paths.worktreeBackPointer,
    entries: [
      snapshotFile(paths.worktreeGitPointer),
      snapshotFile(paths.worktreeCommonPointer),
      snapshotFile(paths.worktreeBackPointer),
      snapshotFile(paths.commonConfig),
      snapshotDirectory(paths.commonHooks),
      ...readdirSync(paths.commonHooks)
        .sort()
        .map((name) => snapshotFile(join(paths.commonHooks, name))),
      snapshotFile(paths.worktreeConfig),
    ],
  };
}

export function assertGitControlPlaneUnchanged(
  expected: GitControlPlaneSnapshot,
): void {
  const actualEntries = [
    snapshotFile(expected.worktreeGitPointer),
    snapshotFile(expected.worktreeCommonPointer),
    snapshotFile(expected.worktreeBackPointer),
    snapshotFile(join(expected.commonGitDir, "config")),
    snapshotDirectory(join(expected.commonGitDir, "hooks")),
    ...readdirSync(join(expected.commonGitDir, "hooks"))
      .sort()
      .map((name) => snapshotFile(join(expected.commonGitDir, "hooks", name))),
    snapshotFile(join(expected.worktreeGitDir, "config.worktree")),
  ];
  if (JSON.stringify(actualEntries) !== JSON.stringify(expected.entries)) {
    throw new Error(
      "Sandcastle Git control-plane files changed; refusing to run trusted host Git cleanup.",
    );
  }
}

function resolveGitControlPaths(
  repoRoot: string,
  worktreePath: string,
  createWorktreeConfig: boolean,
) {
  const repositoryGitDir = resolveGitDirEntry(join(repoRoot, ".git"));
  const commonGitDir = resolveCommonDir(repositoryGitDir);
  const worktreeGitPointer = join(worktreePath, ".git");
  const worktreeGitDir = resolveGitDirEntry(worktreeGitPointer);
  const expectedWorktreesRoot = join(commonGitDir, "worktrees");
  if (!isWithin(expectedWorktreesRoot, worktreeGitDir)) {
    throw new Error(
      "Sandcastle worktree Git directory is outside the repository common Git directory.",
    );
  }
  const worktreeCommonPointer = join(worktreeGitDir, "commondir");
  const worktreeBackPointer = join(worktreeGitDir, "gitdir");
  assertRegularFile(worktreeCommonPointer, "Sandcastle worktree commondir pointer");
  assertRegularFile(worktreeBackPointer, "Sandcastle worktree gitdir pointer");
  if (resolve(worktreeGitDir, readFileSync(worktreeCommonPointer, "utf8").trim()) !== commonGitDir) {
    throw new Error("Sandcastle worktree commondir pointer does not target the verified common Git directory.");
  }
  if (resolve(readFileSync(worktreeBackPointer, "utf8").trim()) !== resolve(worktreeGitPointer)) {
    throw new Error("Sandcastle worktree gitdir back-pointer does not target the verified .git pointer.");
  }

  const commonConfig = join(commonGitDir, "config");
  const commonHooks = join(commonGitDir, "hooks");
  assertRegularFile(commonConfig, "repository Git config");
  assertDirectory(commonHooks, "repository Git hooks directory");
  assertNoActiveHooks(commonHooks);
  assertNoConfigIndirection(readFileSync(commonConfig, "utf8"));

  const worktreeConfig = join(worktreeGitDir, "config.worktree");
  if (createWorktreeConfig) {
    mkdirSync(dirname(worktreeConfig), { recursive: true, mode: 0o700 });
    try {
      const descriptor = openSync(worktreeConfig, "wx", 0o600);
      closeSync(descriptor);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
    }
  }
  assertRegularFile(worktreeConfig, "Sandcastle worktree Git config");
  if (readFileSync(worktreeConfig, "utf8").length !== 0) {
    throw new Error("Sandcastle worktree Git config must be empty before launch.");
  }
  return {
    commonGitDir,
    worktreeGitDir,
    worktreeGitPointer,
    worktreeCommonPointer,
    worktreeBackPointer,
    commonConfig,
    commonHooks,
    worktreeConfig,
  };
}

function resolveGitDirEntry(entryPath: string): string {
  const metadata = lstatSync(entryPath);
  if (metadata.isSymbolicLink()) {
    throw new Error(`${entryPath} must not be a symbolic link.`);
  }
  if (metadata.isDirectory()) return resolve(entryPath);
  if (!metadata.isFile()) throw new Error(`${entryPath} is not Git metadata.`);
  const match = readFileSync(entryPath, "utf8").trim().match(/^gitdir:\s*(.+)$/);
  if (!match) throw new Error(`${entryPath} has an invalid gitdir pointer.`);
  return resolve(dirname(entryPath), match[1]!);
}

function resolveCommonDir(gitDir: string): string {
  const commondir = join(gitDir, "commondir");
  try {
    const metadata = lstatSync(commondir);
    if (!metadata.isFile() || metadata.isSymbolicLink()) {
      throw new Error(`${commondir} must be a regular file.`);
    }
    return resolve(gitDir, readFileSync(commondir, "utf8").trim());
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return gitDir;
    throw error;
  }
}

function readonlyMount(hostPath: string, sandboxPath: string = hostPath) {
  return { hostPath, sandboxPath, readonly: true as const };
}

function snapshotFile(path: string) {
  const metadata = lstatSync(path);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error(`${path} must remain a regular non-symlink file.`);
  }
  return {
    path,
    type: "file" as const,
    mode: metadata.mode & 0o777,
    content: readFileSync(path).toString("base64"),
  };
}

function snapshotDirectory(path: string) {
  const metadata = lstatSync(path);
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    throw new Error(`${path} must remain a non-symlink directory.`);
  }
  return { path, type: "directory" as const, mode: metadata.mode & 0o777 };
}

function assertRegularFile(path: string, label: string): void {
  const metadata = lstatSync(path);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error(`${label} must be a regular non-symlink file.`);
  }
}

function assertDirectory(path: string, label: string): void {
  const metadata = lstatSync(path);
  if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
    throw new Error(`${label} must be a non-symlink directory.`);
  }
}

function assertNoActiveHooks(hooksPath: string): void {
  const active = readdirSync(hooksPath).filter((name) => !name.endsWith(".sample"));
  if (active.length > 0) {
    throw new Error(
      `Repository-local Git hooks are outside the Sandcastle trust boundary: ${active.join(", ")}`,
    );
  }
}

function assertNoConfigIndirection(content: string): void {
  if (/^\s*\[(?:include|includeIf)\b/im.test(content)) {
    throw new Error("Repository Git config includes are outside the Sandcastle trust boundary.");
  }
}

function isWithin(parent: string, child: string): boolean {
  const result = relative(resolve(parent), resolve(child));
  return result.length > 0 && result !== ".." && !result.startsWith(`..${sep}`) && !isAbsolute(result);
}

function replaceProcessEnvironment(
  replacement: Record<string, string | undefined>,
): void {
  for (const key of Object.keys(process.env)) delete process.env[key];
  for (const [key, value] of Object.entries(replacement)) {
    if (value !== undefined) process.env[key] = value;
  }
}
