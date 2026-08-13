import { createSandbox as createOfficialSandbox } from "@ai-hero/sandcastle";

import type {
  AgentPhaseResult,
  IssueSnapshot,
  IssueWorkspace,
} from "./worker-contracts.mts";
import {
  codexAgent,
  COMPLETION_SIGNAL,
  dockerSandbox,
} from "./runtime.mts";
import { assertExactCommitSha } from "./exact-commit-sha.mts";
import {
  assertGitControlPlaneUnchanged,
  captureGitControlPlane,
  withTrustedHostProcess,
} from "./git-control-plane.mts";

export const ROUND_START_SHA_ENV = "SANDCASTLE_ROUND_START_SHA";
const PROJECT_GATE_COMMAND = "./scripts/package.sh";

interface SandboxRunResultLike {
  iterations: unknown[];
  completionSignal?: string;
  stdout: string;
  commits: Array<{ sha: string }>;
}

interface SandboxLike {
  worktreePath: string;
  run(options: Record<string, unknown>): Promise<SandboxRunResultLike>;
  exec(
    command: string,
    options?: { onLine?: (line: string) => void },
  ): Promise<{ exitCode: number; stdout: string; stderr: string }>;
  close(): Promise<{ preservedWorktreePath?: string }>;
}

interface WorkspaceBindings {
  createSandbox(options: Record<string, unknown>): Promise<SandboxLike>;
  makeAgent(): Promise<unknown>;
  makeSandboxProvider(): Promise<unknown>;
  onGateLine(line: string): void;
  guardGitControlPlane(repoRoot: string, worktreePath: string): () => void;
}

function serializeIssueForPrompt(issue: IssueSnapshot): string {
  const json = JSON.stringify(issue);
  if (json === undefined) {
    throw new Error("Selected GitHub Issue could not be serialized as JSON.");
  }
  return json
    .replace(/&/g, "\\u0026")
    .replace(/</g, "\\u003c")
    .replace(/>/g, "\\u003e");
}

const defaultBindings: WorkspaceBindings = {
  async createSandbox(options) {
    return createOfficialSandbox(
      options as Parameters<typeof createOfficialSandbox>[0],
    ) as unknown as Promise<SandboxLike>;
  },
  makeAgent() {
    return codexAgent("high", "sandbox");
  },
  makeSandboxProvider() {
    return dockerSandbox("sandbox");
  },
  onGateLine(line) {
    console.log(`[project-gates] ${line}`);
  },
  guardGitControlPlane(repoRoot, worktreePath) {
    const snapshot = captureGitControlPlane(repoRoot, worktreePath);
    return () => assertGitControlPlaneUnchanged(snapshot);
  },
};

export function createSandcastleWorkspaceFactory(
  bindings: WorkspaceBindings = defaultBindings,
) {
  return async function createWorkspace(input: {
    branch: string;
    baseCommit: string;
    issue: IssueSnapshot;
  }): Promise<IssueWorkspace> {
    const baseCommit = input.baseCommit;
    assertExactCommitSha(
      baseCommit,
      "Sandcastle workspace round start SHA",
    );
    const sandboxProvider = await bindings.makeSandboxProvider();
    const sandbox = await withTrustedHostProcess(() =>
      bindings.createSandbox({
        branch: input.branch,
        baseBranch: baseCommit,
        sandbox: sandboxProvider,
      })
    );
    const assertControlPlane = bindings.guardGitControlPlane(
      process.cwd(),
      sandbox.worktreePath,
    );
    let closed = false;

    async function runPhase(
      name: string,
      promptFile: string,
      reportTag: "issue-report" | "review-report",
      issue: IssueSnapshot,
    ): Promise<AgentPhaseResult> {
      assertControlPlane();
      const agent = await bindings.makeAgent();
      const result = await withTrustedHostProcess(() =>
        sandbox.run({
          name,
          agent,
          promptFile,
          promptArgs: {
            ISSUE_JSON: serializeIssueForPrompt(issue),
            BASE_COMMIT: baseCommit,
            ISSUE_BRANCH: input.branch,
          },
          maxIterations: 1,
          completionSignal: COMPLETION_SIGNAL,
          idleTimeoutSeconds: 600,
          completionTimeoutSeconds: 60,
          logging: { type: "stdout" },
        })
      );
      assertControlPlane();
      if (result.iterations.length !== 1) {
        throw new Error(
          `${name} expected one iteration; received ${result.iterations.length}.`,
        );
      }
      if (result.completionSignal !== COMPLETION_SIGNAL) {
        throw new Error(`${name} did not emit the completion signal.`);
      }
      const summary = extractAgentReport(result.stdout, reportTag);
      if (reportTag === "review-report") {
        validateReviewVerdict(summary, result.commits.length);
      }
      return {
        commits: result.commits,
        summary,
      };
    }

    return {
      implement(issue) {
        return runPhase(
          `issue-${issue.number}-implementer`,
          ".sandcastle/prompt.md",
          "issue-report",
          issue,
        );
      },
      async runGates() {
        assertControlPlane();
        const result = await withTrustedHostProcess(() =>
          sandbox.exec(buildProjectGateCommand(baseCommit), {
            onLine: bindings.onGateLine,
          })
        );
        assertControlPlane();
        if (result.exitCode !== 0) {
          const details = `${result.stdout}\n${result.stderr}`.trim().slice(-4_000);
          throw new Error(
            `Project gates failed with exit code ${result.exitCode}.\n${details}`,
          );
        }
        return { summary: `${PROJECT_GATE_COMMAND} passed (exit 0)` };
      },
      review(issue) {
        return runPhase(
          `issue-${issue.number}-reviewer`,
          ".sandcastle/review-prompt.md",
          "review-report",
          issue,
        );
      },
      async close() {
        if (closed) return {};
        assertControlPlane();
        const result = await withTrustedHostProcess(() => sandbox.close());
        closed = true;
        return result;
      },
    };
  };
}

/**
 * Export once in the gate shell, then execute the project command unchanged.
 * `export` deliberately scopes the value to every `&&`, `||`, pipeline,
 * subshell, and child process in a compound project gate.
 */
export function buildProjectGateCommand(
  roundStartSha: string,
  projectGateCommand = PROJECT_GATE_COMMAND,
): string {
  assertExactCommitSha(roundStartSha, "Project gate round start SHA");
  if (projectGateCommand.length === 0 || projectGateCommand.includes("\0")) {
    throw new Error("Project gate command must be non-empty shell text.");
  }
  return `export ${ROUND_START_SHA_ENV}='${roundStartSha}'; ${projectGateCommand}`;
}

export function extractAgentReport(
  stdout: string,
  tag: "issue-report" | "review-report",
): string {
  const match = stdout.match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`));
  const report = match?.[1]?.trim();
  if (!report) throw new Error(`Agent did not emit a non-empty <${tag}> report.`);
  if (report.length > 2_000) {
    throw new Error(`Agent <${tag}> report is too long (${report.length} chars).`);
  }
  return report;
}

function validateReviewVerdict(report: string, commitCount: number): void {
  const approved = report.startsWith("APPROVED:");
  const corrected = report.startsWith("CORRECTED:");
  if (!approved && !corrected) {
    throw new Error(
      "Independent reviewer must emit an APPROVED: or CORRECTED: verdict.",
    );
  }
  if (commitCount > 0 && !corrected) {
    throw new Error(
      "Independent reviewer made commits and must emit a CORRECTED: verdict.",
    );
  }
  if (commitCount === 0 && !approved) {
    throw new Error(
      "Independent reviewer made no commits and must emit an APPROVED: verdict.",
    );
  }
}
