import { randomUUID } from "node:crypto";
import { mkdtemp, realpath, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { run } from "@ai-hero/sandcastle";

import { withTrustedHostProcess } from "./git-control-plane.mts";

import {
  codexAgent,
  COMPLETION_SIGNAL,
  dockerSandbox,
  IMAGE_NAME,
} from "./runtime.mts";
import {
  assertRepositoryStateUnchanged,
  captureRepositoryState,
  createDisposableGitSnapshot,
  createDisposableSnapshotBranch,
  type RepositoryState,
} from "./smoke-snapshot.mts";

const originalWorkingDirectory = process.cwd();
const sourceRepository = await realpath(originalWorkingDirectory);
const sourceBefore = captureRepositoryState(sourceRepository);
const failures: unknown[] = [];

let snapshotRepository: string | undefined;
let disposableRoot: string | undefined;
let snapshotBefore: RepositoryState | undefined;
let summary:
  | {
      iterations: number;
      completionSignal: string;
      commits: number;
      branch: string;
    }
  | undefined;

try {
  disposableRoot = await mkdtemp(join(tmpdir(), "sandcastle-no-change-smoke-"));
  snapshotRepository = await createDisposableGitSnapshot(sourceRepository, disposableRoot);
  assertRepositoryStateUnchanged(
    sourceBefore,
    captureRepositoryState(sourceRepository),
    "Source repository",
  );

  const smokeBranch = `sandcastle/no-change-smoke/${randomUUID()}`;
  createDisposableSnapshotBranch(snapshotRepository, smokeBranch);
  snapshotBefore = captureRepositoryState(snapshotRepository);
  // One-shot `run()` creates the container with the agent provider already
  // available, so the token belongs to the agent side only. Reusable
  // `createSandbox()` uses the opposite explicit placement in the workspace.
  const agent = await codexAgent("low", "agent");
  const sandbox = await dockerSandbox("agent");
  process.chdir(snapshotRepository);

  const result = await withTrustedHostProcess(() =>
    run({
      cwd: snapshotRepository,
      name: "no-change-smoke",
      agent,
      sandbox,
      promptFile: ".sandcastle/smoke-prompt.md",
      maxIterations: 1,
      branchStrategy: { type: "branch", branch: smokeBranch },
      completionSignal: COMPLETION_SIGNAL,
      idleTimeoutSeconds: 600,
      completionTimeoutSeconds: 60,
      logging: { type: "stdout" },
    })
  );

  if (result.iterations.length !== 1) {
    throw new Error(`Expected one iteration; received ${result.iterations.length}.`);
  }
  if (result.completionSignal !== COMPLETION_SIGNAL) {
    throw new Error("The real agent did not emit the required completion signal.");
  }
  if (result.commits.length !== 0) {
    throw new Error(`Expected zero commits; received ${result.commits.length}.`);
  }
  if (result.preservedWorktreePath) {
    throw new Error(`Unexpected preserved worktree: ${result.preservedWorktreePath}`);
  }
  if (result.branch !== smokeBranch) {
    throw new Error(`Expected disposable branch ${smokeBranch}; received ${result.branch}.`);
  }

  summary = {
    iterations: result.iterations.length,
    completionSignal: result.completionSignal,
    commits: result.commits.length,
    branch: result.branch,
  };
} catch (error) {
  failures.push(error);
} finally {
  try {
    process.chdir(originalWorkingDirectory);
  } catch (error) {
    failures.push(error);
  }

  if (snapshotRepository && snapshotBefore) {
    try {
      assertRepositoryStateUnchanged(
        snapshotBefore,
        captureRepositoryState(snapshotRepository),
        "Disposable snapshot",
      );
    } catch (error) {
      failures.push(error);
    }
  }

  try {
    assertRepositoryStateUnchanged(
      sourceBefore,
      captureRepositoryState(sourceRepository),
      "Source repository",
    );
  } catch (error) {
    failures.push(error);
  }

  if (disposableRoot) {
    try {
      await rm(disposableRoot, { recursive: true, force: true });
    } catch (error) {
      failures.push(error);
    }
  }
}

if (failures.length === 1) throw failures[0];
if (failures.length > 1) {
  throw new AggregateError(failures, "Sandcastle smoke failed with multiple invariant violations.");
}
if (!summary) throw new Error("Sandcastle smoke completed without a result summary.");

console.log("\nSandcastle no-change smoke: PASS");
console.log(`Image: ${IMAGE_NAME}`);
console.log(`Iterations: ${summary.iterations}`);
console.log(`Completion signal: ${summary.completionSignal}`);
console.log(`Commits: ${summary.commits}`);
console.log(`Disposable branch: ${summary.branch}`);
console.log("Disposable snapshot branch/HEAD/status/refs/worktrees unchanged: yes");
console.log("Source repository branch/HEAD/status/refs/worktrees unchanged: yes");
console.log("GitHub access: none");
