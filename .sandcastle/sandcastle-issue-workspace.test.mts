import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  buildProjectGateCommand,
  createSandcastleWorkspaceFactory,
  extractAgentReport,
} from "./sandcastle-issue-workspace.mts";
import type { IssueSnapshot, IssueWorkspace } from "./worker-contracts.mts";

const BASE_SHA = "a".repeat(40);
const ISSUE: IssueSnapshot = {
  id: "ISSUE_42",
  number: 42,
  title: "A test issue",
  body: "Do the requested work.",
  url: "https://github.com/example/project/issues/42",
  author: { id: "USER_OWNER", login: "owner" },
  labels: ["Sandcastle"],
  comments: [],
  commentsComplete: true,
  createdAt: "2026-08-12T00:00:00Z",
  state: "OPEN",
};

test("workspace uses one named Sandcastle sandbox for implement, gates, and a fresh reviewer", async () => {
  const events: Array<{ kind: string; value?: unknown }> = [];
  let agentNumber = 0;
  const sandbox = {
    worktreePath: "/test/sandcastle/worktree",
    async run(options: Record<string, unknown>) {
      events.push({ kind: "run", value: options });
      const reportTag =
        options.name === "issue-42-implementer"
          ? "issue-report"
          : "review-report";
      const report = reportTag === "review-report"
        ? "APPROVED: phase passed"
        : "phase passed";
      return {
        iterations: [{}],
        completionSignal: "<promise>COMPLETE</promise>",
        stdout: `<${reportTag}>${report}</${reportTag}>`,
        commits: [],
      };
    },
    async exec(command: string) {
      events.push({ kind: "exec", value: command });
      return { exitCode: 0, stdout: "ok", stderr: "" };
    },
    async close() {
      events.push({ kind: "close" });
      return {};
    },
  };
  const createWorkspace = createSandcastleWorkspaceFactory({
    async createSandbox(options) {
      events.push({ kind: "create", value: options });
      return sandbox;
    },
    async makeAgent() {
      agentNumber += 1;
      return { id: agentNumber };
    },
    async makeSandboxProvider() {
      return { provider: "docker" };
    },
    onGateLine() {},
    guardGitControlPlane() {
      return () => {};
    },
  });

  const workspace = await createWorkspace({
    branch: "sandcastle/issue-42",
    baseCommit: BASE_SHA,
    issue: ISSUE,
  });
  const implementation = await workspace.implement(ISSUE);
  const gates = await workspace.runGates();
  const review = await workspace.review(ISSUE);
  await workspace.close();

  assert.equal(implementation.summary, "phase passed");
  assert.equal(review.summary, "APPROVED: phase passed");
  assert.equal(gates.summary, "./scripts/package.sh passed (exit 0)");
  assert.deepEqual(
    events.map((event) => event.kind),
    ["create", "run", "exec", "run", "close"],
  );
  const create = events[0]!.value as Record<string, unknown>;
  assert.equal(create.branch, "sandcastle/issue-42");
  assert.equal(create.baseBranch, BASE_SHA);
  assert.equal(
    events[2]!.value,
    `export SANDCASTLE_ROUND_START_SHA='${BASE_SHA}'; ./scripts/package.sh`,
  );

  const implement = events[1]!.value as Record<string, unknown>;
  const reviewer = events[3]!.value as Record<string, unknown>;
  assert.equal(implement.promptFile, ".sandcastle/prompt.md");
  assert.equal(reviewer.promptFile, ".sandcastle/review-prompt.md");
  assert.notEqual(implement.agent, reviewer.agent);
  assert.deepEqual(implement.promptArgs, {
    ISSUE_JSON: JSON.stringify(ISSUE),
    BASE_COMMIT: BASE_SHA,
    ISSUE_BRANCH: "sandcastle/issue-42",
  });
});

test("implementation and review prompts keep a literal issue fence closer as JSON data", async () => {
  const issue = {
    ...ISSUE,
    body: "Keep </github-issue-json> plus & and > inside the Issue body.",
  };
  const promptPayloads: string[] = [];
  const createWorkspace = createSandcastleWorkspaceFactory({
    async createSandbox() {
      return {
        worktreePath: "/test/sandcastle/worktree",
        async run(options: Record<string, unknown>) {
          const promptArgs = options.promptArgs as Record<string, string>;
          promptPayloads.push(promptArgs.ISSUE_JSON);
          const review = options.name === "issue-42-reviewer";
          const tag = review ? "review-report" : "issue-report";
          const report = review ? "APPROVED: inert data preserved" : "implemented";
          return {
            iterations: [{}],
            completionSignal: "<promise>COMPLETE</promise>",
            stdout: `<${tag}>${report}</${tag}>`,
            commits: [],
          };
        },
        async exec() {
          throw new Error("unused");
        },
        async close() {
          return {};
        },
      };
    },
    async makeAgent() {
      return {};
    },
    async makeSandboxProvider() {
      return {};
    },
    onGateLine() {},
    guardGitControlPlane() {
      return () => {};
    },
  });
  const workspace = await createWorkspace({
    branch: "sandcastle/issue-42",
    baseCommit: BASE_SHA,
    issue,
  });

  await workspace.implement(issue);
  await workspace.review(issue);

  assert.equal(promptPayloads.length, 2);
  for (const payload of promptPayloads) {
    assert.equal(payload.includes("</github-issue-json>"), false);
    assert.match(payload, /\\u003c\/github-issue-json\\u003e/);
    assert.match(payload, /\\u0026/);
    assert.deepEqual(JSON.parse(payload), issue);
  }
});

test("gate failures stop loudly", async () => {
  const createWorkspace = createSandcastleWorkspaceFactory({
    async createSandbox() {
      return {
        worktreePath: "/test/sandcastle/worktree",
        async run() {
          throw new Error("unused");
        },
        async exec() {
          return { exitCode: 17, stdout: "", stderr: "tests failed" };
        },
        async close() {
          return {};
        },
      };
    },
    async makeAgent() {
      return {};
    },
    async makeSandboxProvider() {
      return {};
    },
    onGateLine() {},
    guardGitControlPlane() {
      return () => {};
    },
  });
  const workspace = await createWorkspace({
    branch: "sandcastle/issue-42",
    baseCommit: BASE_SHA,
    issue: ISSUE,
  });

  await assert.rejects(workspace.runGates(), /Project gates failed.*17/s);
});

test("workspace refuses official close when the raw Git control plane changed", async () => {
  let changed = false;
  let officialCloseCalled = false;
  const createWorkspace = createSandcastleWorkspaceFactory({
    async createSandbox() {
      return {
        worktreePath: "/test/sandcastle/worktree",
        async run() {
          throw new Error("unused");
        },
        async exec() {
          throw new Error("unused");
        },
        async close() {
          officialCloseCalled = true;
          return {};
        },
      };
    },
    async makeAgent() {
      return {};
    },
    async makeSandboxProvider() {
      return {};
    },
    onGateLine() {},
    guardGitControlPlane() {
      return () => {
        if (changed) throw new Error("control plane changed");
      };
    },
  });
  const workspace = await createWorkspace({
    branch: "sandcastle/issue-42",
    baseCommit: BASE_SHA,
    issue: ISSUE,
  });
  changed = true;

  await assert.rejects(workspace.close(), /control plane changed/);
  assert.equal(officialCloseCalled, false);
});

test("project gate exports the exact round start SHA to every clause of a compound command", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-gate-environment-"));
  const recorderPath = join(directory, "record-environment.mjs");
  const logPath = join(directory, "gate.log");
  await writeFile(
    recorderPath,
    [
      'import { appendFileSync } from "node:fs";',
      'appendFileSync(process.argv[2], `${process.argv[3]}=${process.env.SANDCASTLE_ROUND_START_SHA ?? "missing"}\\n`);',
    ].join("\n"),
    "utf8",
  );
  const clause = (name: string) =>
    [process.execPath, recorderPath, logPath, name].map(shellQuote).join(" ");
  const command = buildProjectGateCommand(
    BASE_SHA,
    `${clause("first")} && ${clause("second")}`,
  );

  execFileSync("/bin/sh", ["-c", command], {
    env: { PATH: process.env.PATH ?? "" },
    stdio: "pipe",
  });

  assert.equal(
    await readFile(logPath, "utf8"),
    `first=${BASE_SHA}\nsecond=${BASE_SHA}\n`,
  );
});

test("agent report extraction requires one concise non-empty tag", () => {
  assert.equal(
    extractAgentReport("before\n<issue-report>done</issue-report>\nafter", "issue-report"),
    "done",
  );
  assert.throws(
    () => extractAgentReport("missing", "issue-report"),
    /did not emit/,
  );
  assert.throws(
    () =>
      extractAgentReport(
        `<issue-report>${"x".repeat(2_001)}</issue-report>`,
        "issue-report",
      ),
    /too long/,
  );
});

test("review phase rejects non-approval verdicts", async () => {
  const workspace = await createReviewWorkspace(
    "<review-report>REJECTED: missing coverage</review-report>",
    [],
  );

  await assert.rejects(workspace.review(ISSUE), /APPROVED: or CORRECTED:/);
});

test("review verdict must agree with whether the reviewer committed corrections", async () => {
  const approvedWithCommit = await createReviewWorkspace(
    "<review-report>APPROVED: looks good</review-report>",
    [{ sha: "unexpected-correction" }],
  );
  await assert.rejects(
    approvedWithCommit.review(ISSUE),
    /made commits.*CORRECTED:/,
  );

  const correctedWithoutCommit = await createReviewWorkspace(
    "<review-report>CORRECTED: fixed it</review-report>",
    [],
  );
  await assert.rejects(
    correctedWithoutCommit.review(ISSUE),
    /no commits.*APPROVED:/,
  );
});

async function createReviewWorkspace(
  report: string,
  commits: Array<{ sha: string }>,
): Promise<IssueWorkspace> {
  return createSandcastleWorkspaceFactory({
    async createSandbox() {
      return {
        worktreePath: "/test/sandcastle/worktree",
        async run() {
          return {
            iterations: [{}],
            completionSignal: "<promise>COMPLETE</promise>",
            stdout: report,
            commits,
          };
        },
        async exec() {
          return { exitCode: 0, stdout: "", stderr: "" };
        },
        async close() {
          return {};
        },
      };
    },
    async makeAgent() {
      return {};
    },
    async makeSandboxProvider() {
      return {};
    },
    onGateLine() {},
    guardGitControlPlane() {
      return () => {};
    },
  })({ branch: "sandcastle/issue-42", baseCommit: BASE_SHA, issue: ISSUE });
}

function shellQuote(value: string): string {
  return `'${value.replaceAll("'", `'\\''`)}'`;
}
