import assert from "node:assert/strict";
import test from "node:test";

import {
  APPROVAL_COMMAND,
  isExactApproval,
  runGithubIssueWorker,
} from "./github-issue-worker.mts";
import { emptyWorkerState, type AtomicStateStore, type WorkerState } from "./durable-state.mts";
import type {
  HostGithubDelivery,
  HostIssueGit,
  IssueCommentSnapshot,
  IssueSnapshot,
  IssueWorkerConfiguration,
  IssueWorkerDependencies,
  MergeSnapshot,
  PullRequestSnapshot,
  WorkerEffect,
} from "./worker-contracts.mts";

const A = "a".repeat(40);
const B = "b".repeat(40);
const C = "c".repeat(40);
const TRUSTED = { id: "trusted-node", login: "owner" };
const DELIVERY = { id: "delivery-node", login: "worker" };
const CONFIG: IssueWorkerConfiguration = {
  repositoryId: "repo-node",
  repositoryNameWithOwner: "owner/project",
  ownerId: TRUSTED.id,
  deliveryActorId: DELIVERY.id,
  trustedActorId: TRUSTED.id,
  baseRef: "main",
  queueLabel: "Sandcastle",
  branchPrefix: "sandcastle/issue-",
  maxStatusBytes: 1_200,
  mergeMethod: "SQUASH",
  requiredCheckNames: ["package"],
};

test("qualifies an immutable trusted Issue with fresh agents/gates, exact SHA delivery, one draft PR, and bounded harness-only status", async () => {
  const world = makeWorld();
  world.issue.comments.push(
    comment("trusted-revision", TRUSTED, "Please keep the behavior deterministic.", 1),
    comment("approval-near-miss", TRUSTED, `${APPROVAL_COMMAND}\n`, 2),
    comment("untrusted", { id: "stranger", login: "stranger" }, "@owner leak /home/private", 3),
  );

  const outcome = await runGithubIssueWorker(
    { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
    world.dependencies,
    CONFIG,
  );

  assert.deepEqual(outcome, {
    kind: "delivered",
    issueNumber: 7,
    branch: "sandcastle/issue-7",
    sha: B,
    pullRequestNumber: 19,
  });
  assert.equal(world.pushes, 1);
  assert.equal(world.prCreates, 1);
  assert.equal(world.prs.length, 1);
  assert.equal(world.prs[0]!.isDraft, true);
  assert.equal(world.prs[0]!.headSha, B);
  assert.equal(world.implementationAgents.length, 1);
  assert.equal(world.reviewAgents.length, 1);
  assert.notEqual(world.implementationAgents[0], world.reviewAgents[0]);
  assert.equal(world.gates, 1);
  assert.deepEqual(
    world.agentIssues.flatMap((issue) => issue.comments.map((item) => item.id)),
    ["trusted-revision", "approval-near-miss", "trusted-revision", "approval-near-miss"],
    "near-miss approval is an ordinary trusted revision; untrusted comments stay out",
  );
  assert.equal(world.statusComments.size, 1);
  const status = [...world.statusComments.values()][0]!.body;
  assert.ok(Buffer.byteLength(status, "utf8") <= CONFIG.maxStatusBytes);
  assert.match(status, /AI-generated and authorized by a human/);
  assert.doesNotMatch(status, /@|\/home|Please keep|stranger/);
  assert.equal(world.prInputs[0]!.title, "Sandcastle work for Issue #7");
  assert.doesNotMatch(world.prInputs[0]!.body, /close[sd]?\s+#7|@|Please keep/i);
});

for (const crashEffect of ["push", "pull_request_create", "status"] as const) {
  test(`restart reconciles a crash after ${crashEffect} without duplicating host effects`, async () => {
    const world = makeWorld();
    let crashed = false;
    world.dependencies.afterEffect = (effect) => {
      if (!crashed && effect === crashEffect) {
        crashed = true;
        throw new Error(`simulated process death after ${effect}`);
      }
    };
    await assert.rejects(
      runGithubIssueWorker(
        { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
        world.dependencies,
        CONFIG,
      ),
      /simulated process death/,
    );
    world.dependencies.afterEffect = undefined;

    const recovered = await runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    );

    assert.equal(recovered.kind, "delivered");
    assert.equal(world.pushes, 1);
    assert.equal(world.prCreates, 1);
    assert.equal(world.statusComments.size, 1);
  });
}

test("restart refuses remote drift before pull request or status reconciliation", async () => {
  for (const crashEffect of ["pull_request_create", "status"] as const) {
    const world = makeWorld();
    let crashed = false;
    world.dependencies.afterEffect = (effect) => {
      if (!crashed && effect === crashEffect) {
        crashed = true;
        throw new Error(`simulated process death after ${effect}`);
      }
    };
    await assert.rejects(
      runGithubIssueWorker(
        { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
        world.dependencies,
        CONFIG,
      ),
      /simulated process death/,
    );
    const prCalls = world.prInputs.length;
    const statusCalls = world.statusComments.size;
    world.dependencies.afterEffect = undefined;
    world.remoteSha = C;
    await assert.rejects(
      runGithubIssueWorker(
        { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
        world.dependencies,
        CONFIG,
      ),
      /remote branch no longer equals.*candidate/i,
    );
    assert.equal(world.prInputs.length, prCalls);
    assert.equal(world.statusComments.size, statusCalls);
  }
});

test("report restart refuses draft PR head drift before publishing status", async () => {
  const world = makeWorld();
  let crashed = false;
  world.dependencies.afterEffect = (effect) => {
    if (!crashed && effect === "status") {
      crashed = true;
      throw new Error("simulated process death after status");
    }
  };
  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    ),
    /simulated process death/,
  );
  world.dependencies.afterEffect = undefined;
  world.prs[0] = { ...world.prs[0]!, headSha: C };
  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    ),
    /draft pull request drifted/,
  );
  assert.equal(world.statusComments.size, 1);
});

test("no-change Issue round stops before push because GitHub cannot represent it as a draft PR", async () => {
  const world = makeWorld();
  world.nextImplementationSha = A;
  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    ),
    /no commit beyond.*start SHA/i,
  );
  assert.equal(world.pushes, 0);
  assert.equal(world.prCreates, 0);
});

for (const crashEffect of ["implementation", "review"] as const) {
  test(`restart restores the owned branch before rerunning a crashed ${crashEffect} phase`, async () => {
    const world = makeWorld();
    if (crashEffect === "review") world.nextReviewSha = C;
    let crashed = false;
    world.dependencies.afterEffect = (effect) => {
      if (!crashed && effect === crashEffect) {
        crashed = true;
        throw new Error(`simulated process death after ${effect}`);
      }
    };
    await assert.rejects(
      runGithubIssueWorker(
        { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
        world.dependencies,
        CONFIG,
      ),
      /simulated process death/,
    );
    world.dependencies.afterEffect = undefined;
    const recovered = await runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    );
    assert.equal(recovered.kind, "delivered");
    assert.equal(world.restores, 1);
    assert.equal(
      crashEffect === "implementation"
        ? world.implementationAgents.length
        : world.reviewAgents.length,
      2,
    );
  });
}

test("a disabled byte-exact approval is consumed as a no-op and a later ordinary trusted comment starts a revision without relabeling", async () => {
  const world = makeWorld();
  await deliver(world);
  world.issue.labels = [];
  world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 10));

  const disabled = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
    world.dependencies,
    CONFIG,
  );
  assert.deepEqual(disabled, {
    kind: "approval_consumed_disabled",
    issueNumber: 7,
    approvalIds: ["approval"],
  });
  assert.equal(world.merges, 0);

  world.nextImplementationSha = C;
  world.issue.comments.push(comment("revision", TRUSTED, "Please revise this round.", 11));
  const revised = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
    world.dependencies,
    CONFIG,
  );
  assert.equal(revised.kind, "delivered");
  assert.equal(revised.kind === "delivered" && revised.sha, C);
  assert.equal(world.pushes, 2);
  assert.equal(world.prCreates, 1);
  assert.equal(world.prs.length, 1);
});

test("only the exact bytes are approval; enabled merge rereads stable issue/PR evidence and uses head SHA CAS while leaving Issue open", async () => {
  assert.equal(isExactApproval(APPROVAL_COMMAND), true);
  for (const nearMiss of [`${APPROVAL_COMMAND}\n`, ` ${APPROVAL_COMMAND}`, "/Sandcastle approve", `${APPROVAL_COMMAND} `]) {
    assert.equal(isExactApproval(nearMiss), false);
  }
  const world = makeWorld();
  await deliver(world);
  world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 20));

  const outcome = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
    world.dependencies,
    CONFIG,
  );

  assert.equal(outcome.kind, "merged");
  assert.equal(world.readyCalls, 1);
  assert.equal(world.merges, 1);
  assert.deepEqual(world.mergeCas, [{ sha: B, method: "SQUASH" }]);
  assert.ok(world.mergeReads >= 4);
  assert.equal(world.issue.state, "OPEN");
});

for (const crashEffect of ["pull_request_ready", "merge"] as const) {
  test(`approval restart adopts the completed ${crashEffect} intent without duplicating it`, async () => {
    const world = makeWorld();
    await deliver(world);
    world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 20));
    let crashed = false;
    world.dependencies.afterEffect = (effect) => {
      if (!crashed && effect === crashEffect) {
        crashed = true;
        throw new Error(`simulated process death after ${effect}`);
      }
    };
    await assert.rejects(
      runGithubIssueWorker(
        { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
        world.dependencies,
        CONFIG,
      ),
      /simulated process death/,
    );
    world.dependencies.afterEffect = undefined;
    const recovered = await runGithubIssueWorker(
      { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
      world.dependencies,
      CONFIG,
    );
    assert.equal(recovered.kind, "merged");
    assert.equal(world.readyCalls, 1);
    assert.equal(world.merges, 1);
  });
}

test("approval preflight fails closed when checks/comments/base/head evidence drifts between rereads", async () => {
  const world = makeWorld();
  await deliver(world);
  world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 20));
  const read = world.dependencies.github.readMergeSnapshot.bind(world.dependencies.github);
  let reads = 0;
  world.dependencies.github.readMergeSnapshot = async (...args) => {
    const snapshot = await read(...args);
    reads += 1;
    return reads === 2 ? { ...snapshot, checksFingerprint: "changed-check-set" } : snapshot;
  };
  await assert.rejects(
    runGithubIssueWorker(
      { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
      world.dependencies,
      CONFIG,
    ),
    /evidence changed/,
  );
  assert.equal(world.readyCalls, 0);
  assert.equal(world.merges, 0);
});

test("auto-merge on the draft PR is rejected before readiness", async () => {
  const world = makeWorld();
  await deliver(world);
  world.prs[0] = { ...world.prs[0]!, autoMergeEnabled: true };
  world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 20));
  await assert.rejects(
    runGithubIssueWorker(
      { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
      world.dependencies,
      CONFIG,
    ),
    /auto-merge must remain disabled/,
  );
  assert.equal(world.readyCalls, 0);
  assert.equal(world.merges, 0);
});

test("initial untracked remote/PR collision and immutable actor mismatch fail before an agent runs", async () => {
  const remoteCollision = makeWorld();
  remoteCollision.remoteSha = A;
  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      remoteCollision.dependencies,
      CONFIG,
    ),
    /already has a remote branch/,
  );
  assert.equal(remoteCollision.implementationAgents.length, 0);

  const actorMismatch = makeWorld();
  actorMismatch.issue.author = { id: "login-reused-different-node", login: TRUSTED.login };
  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      actorMismatch.dependencies,
      CONFIG,
    ),
    /author is not the configured trusted actor/,
  );
  assert.equal(actorMismatch.implementationAgents.length, 0);
});

test("label queue skips an already-delivered idle Issue and advances to the next labeled Issue", async () => {
  const world = makeWorld();
  await deliver(world);
  const secondIssue: IssueSnapshot = {
    ...structuredClone(world.issue),
    id: "issue-node-8",
    number: 8,
    title: "Second queued Issue",
    url: "https://github.com/owner/project/issues/8",
    comments: [],
  };
  world.dependencies.github.getLabeledIssues = async () => [
    structuredClone(world.issue),
    structuredClone(secondIssue),
  ];
  world.dependencies.github.getIssue = async (number) =>
    structuredClone(number === 7 ? world.issue : secondIssue);
  world.branchSha = undefined;
  world.remoteSha = undefined;
  world.prs = [];

  const outcome = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
    world.dependencies,
    CONFIG,
  );

  assert.equal(outcome.kind, "delivered");
  assert.equal(outcome.kind === "delivered" && outcome.issueNumber, 8);
});

test("approval restart rejects the same exact body when updatedAt changed after intent", async () => {
  const world = makeWorld();
  await deliver(world);
  world.issue.comments.push(comment("approval", TRUSTED, APPROVAL_COMMAND, 20));
  let crashed = false;
  const read = world.dependencies.github.readMergeSnapshot.bind(world.dependencies.github);
  world.dependencies.github.readMergeSnapshot = async (...args) => {
    if (!crashed) {
      crashed = true;
      throw new Error("simulated crash before approval snapshot");
    }
    return read(...args);
  };
  await assert.rejects(
    runGithubIssueWorker(
      { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
      world.dependencies,
      CONFIG,
    ),
    /simulated crash/,
  );
  world.issue.comments[0] = {
    ...world.issue.comments[0]!,
    updatedAt: "2026-01-03T00:00:00Z",
  };

  const outcome = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
    world.dependencies,
    CONFIG,
  );
  assert.equal(outcome.kind, "idle");
  assert.equal(world.merges, 0);
});

test("a failed phase with a preserved dirty worktree fails closed instead of auto-retrying stale files", async () => {
  const world = makeWorld();
  world.dependencies.createWorkspace = async () => ({
    async implement() {
      throw new Error("agent failed");
    },
    async runGates() { return { summary: "unused" }; },
    async review() { return { commits: [], summary: "unused" }; },
    async close() { return { preservedWorktreePath: "/synthetic/recovery" }; },
  });

  await assert.rejects(
    runGithubIssueWorker(
      { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
      world.dependencies,
      CONFIG,
    ),
    /preserved a dirty recovery worktree/,
  );
  assert.equal(world.pushes, 0);
});

test("an approval posted during a round is consumed at delivery and cannot approve that new candidate", async () => {
  const world = makeWorld();
  const implement = world.dependencies.createWorkspace;
  world.dependencies.createWorkspace = async (input) => {
    const workspace = await implement(input);
    return {
      ...workspace,
      async implement(issue) {
        const result = await workspace.implement(issue);
        world.issue.comments.push(comment("early-approval", TRUSTED, APPROVAL_COMMAND, 15));
        return result;
      },
    };
  };
  await deliver(world);

  const outcome = await runGithubIssueWorker(
    { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
    world.dependencies,
    CONFIG,
  );
  assert.equal(outcome.kind, "idle");
  assert.equal(world.merges, 0);
});

test("any unconsumed same-timestamp trusted comment invalidates approval regardless of opaque ID order", async () => {
  const world = makeWorld();
  await deliver(world);
  const approval = comment("z-approval", TRUSTED, APPROVAL_COMMAND, 20);
  const revision = { ...comment("a-later-comment", TRUSTED, "Revise again", 20) };
  world.issue.comments.push(approval);
  const read = world.dependencies.github.readMergeSnapshot.bind(world.dependencies.github);
  let reads = 0;
  world.dependencies.github.readMergeSnapshot = async (...args) => {
    reads += 1;
    if (reads === 2) world.issue.comments.push(revision);
    return read(...args);
  };

  await assert.rejects(
    runGithubIssueWorker(
      { deliveryEnabled: true, mergeEnabled: true, allowMerge: true },
      world.dependencies,
      CONFIG,
    ),
    /newer trusted comment invalidated approval/,
  );
  assert.equal(world.merges, 0);
});

async function deliver(world: ReturnType<typeof makeWorld>) {
  return runGithubIssueWorker(
    { issueNumber: 7, deliveryEnabled: true, mergeEnabled: false, allowMerge: false },
    world.dependencies,
    CONFIG,
  );
}

function makeWorld() {
  const issue: IssueSnapshot = {
    id: "issue-node-7",
    number: 7,
    title: "Untrusted title text @someone",
    body: "Implement the requested behavior.",
    url: "https://github.com/owner/project/issues/7",
    author: { ...TRUSTED },
    labels: ["Sandcastle"],
    comments: [],
    commentsComplete: true,
    createdAt: "2026-01-01T00:00:00Z",
    state: "OPEN",
  };
  let durable = emptyWorkerState(CONFIG.repositoryId);
  const stateStore: AtomicStateStore = {
    async load() {
      return structuredClone(durable);
    },
    async commit(expected, next) {
      if (durable.generation !== expected) throw new Error("stale fake generation");
      durable = structuredClone({ ...next, generation: expected + 1 });
      return structuredClone(durable);
    },
  };

  const world = {
    issue,
    branchSha: undefined as string | undefined,
    remoteSha: undefined as string | undefined,
    baseSha: A,
    nextImplementationSha: B,
    nextReviewSha: undefined as string | undefined,
    prs: [] as PullRequestSnapshot[],
    pushes: 0,
    prCreates: 0,
    gates: 0,
    readyCalls: 0,
    merges: 0,
    mergeReads: 0,
    restores: 0,
    mergeCas: [] as Array<{ sha: string; method: string }>,
    implementationAgents: [] as object[],
    reviewAgents: [] as object[],
    agentIssues: [] as IssueSnapshot[],
    statusComments: new Map<string, IssueCommentSnapshot>(),
    prInputs: [] as Array<{ title: string; body: string }>,
    dependencies: undefined as unknown as IssueWorkerDependencies,
  };

  const git: HostIssueGit = {
    validateRepository() {},
    validateForDelivery() {},
    assertNoPreservedWorktree() {},
    assertCommitExists(sha) {
      if (![A, B, C].includes(sha)) throw new Error("commit is missing");
    },
    branchSha: () => world.branchSha,
    ensureBranch(_branch, start, expected) {
      if (world.branchSha === undefined) world.branchSha = start;
      else if (world.branchSha !== (expected ?? start)) throw new Error("local branch drift");
    },
    restoreOwnedBranch(_branch, start) {
      world.restores += 1;
      world.branchSha = start;
    },
    assertBranchSha(_branch, expected) {
      if (world.branchSha !== expected) throw new Error("branch SHA mismatch");
    },
    isAncestor(ancestor, descendant) {
      return ancestor === descendant || [A, B, C].indexOf(ancestor) <= [A, B, C].indexOf(descendant);
    },
    async remoteBranchSha() { return world.remoteSha; },
    async pushExact({ candidateSha, expectedRemoteSha }) {
      if ((world.remoteSha ?? null) !== expectedRemoteSha) throw new Error("remote drift");
      world.pushes += 1;
      world.remoteSha = candidateSha;
    },
  };

  const github: HostGithubDelivery = {
    async validateContext() {
      return {
        repositoryId: CONFIG.repositoryId,
        nameWithOwner: CONFIG.repositoryNameWithOwner,
        owner: TRUSTED,
        deliveryActor: DELIVERY,
        deliveryPermission: "WRITE" as const,
        trustedActor: TRUSTED,
        baseRef: CONFIG.baseRef,
        baseSha: world.baseSha,
        deleteBranchOnMerge: false,
      };
    },
    async getIssue() { return structuredClone(issue); },
    async getLabeledIssues() {
      return issue.labels.includes(CONFIG.queueLabel) ? [structuredClone(issue)] : [];
    },
    async findPullRequests() { return structuredClone(world.prs); },
    async ensureExactlyOneDraftPullRequest(input) {
      if (world.prs.length === 0) world.prCreates += 1;
      world.prInputs.push({ title: input.title, body: input.body });
      const pr = {
        ...pullRequest(world.branchSha!),
        title: input.title,
        body: input.body,
        baseRef: input.baseRef,
        headRef: input.headRef,
      };
      world.prs = [pr];
      return structuredClone(pr);
    },
    async upsertStatusComment(_number, body) {
      const marker = body.split("\n", 1)[0]!;
      const existing = world.statusComments.get(marker);
      const value = existing
        ? { ...existing, body, updatedAt: "2026-01-02T00:00:10Z" }
        : comment("status-node", DELIVERY, body, 99);
      world.statusComments.set(marker, value);
      return structuredClone(value);
    },
    async readMergeSnapshot() {
      world.mergeReads += 1;
      const pr = world.prs[0]!;
      const comments = structuredClone(issue.comments);
      return {
        repository: await this.validateContext(),
        repositoryId: CONFIG.repositoryId,
        issueId: issue.id,
        issueState: issue.state,
        pullRequest: structuredClone(pr),
        comments,
        commentsComplete: true,
        checks: [{ id: "check-package", name: "package", status: "COMPLETED", conclusion: "SUCCESS" }],
        checksComplete: true,
        requiredChecksPass: true,
        checksFingerprint: "checks-pass",
        commentsFingerprint: JSON.stringify(comments),
      } satisfies MergeSnapshot;
    },
    async markPullRequestReady() {
      world.readyCalls += 1;
      world.prs[0] = { ...world.prs[0]!, isDraft: false };
    },
    async mergePullRequest(_id, sha, method) {
      world.merges += 1;
      world.mergeCas.push({ sha, method });
      world.prs[0] = {
        ...world.prs[0]!,
        state: "MERGED",
        isDraft: false,
        baseSha: C,
        mergeCommitSha: C,
        mergedByActorId: DELIVERY.id,
      };
      world.baseSha = C;
      return { merged: true, mergeCommitOid: C };
    },
  };

  let workspaceId = 0;
  const dependencies: IssueWorkerDependencies = {
    github,
    git,
    stateStore,
    async createWorkspace({ issue: agentIssue }) {
      const agent = { workspaceId: ++workspaceId };
      return {
        async implement() {
          world.implementationAgents.push(agent);
          world.agentIssues.push(structuredClone(agentIssue));
          const before = world.branchSha!;
          world.branchSha = world.nextImplementationSha;
          return {
            commits: before === world.branchSha ? [] : [{ sha: world.branchSha }],
            summary: "untrusted agent prose /home/private @mention",
          };
        },
        async runGates() {
          world.gates += 1;
          return { summary: "gate passed" };
        },
        async review() {
          world.reviewAgents.push(agent);
          world.agentIssues.push(structuredClone(agentIssue));
          const before = world.branchSha!;
          if (world.nextReviewSha) world.branchSha = world.nextReviewSha;
          return {
            commits: before === world.branchSha ? [] : [{ sha: world.branchSha }],
            summary: world.nextReviewSha
              ? "CORRECTED: untrusted review prose"
              : "APPROVED: untrusted review prose",
          };
        },
        async close() { return {}; },
      };
    },
  };
  world.dependencies = dependencies;
  return world;
}

function pullRequest(headSha: string): PullRequestSnapshot {
  return {
    id: "pr-node-19",
    number: 19,
    state: "OPEN",
    isDraft: true,
    title: "Sandcastle work for Issue #7",
    body: `Tracks Issue #7 for human review; does not close it.\n\nAI-generated and authorized by a human.`,
    headRef: "sandcastle/issue-7",
    headSha,
    baseRef: "main",
    baseSha: A,
    url: "https://github.com/owner/project/pull/19",
    autoMergeEnabled: false,
  };
}

function comment(id: string, author: { id: string; login: string }, body: string, second: number): IssueCommentSnapshot {
  const at = `2026-01-02T00:00:${String(second).padStart(2, "0")}Z`;
  return { id, author, body, createdAt: at, updatedAt: at };
}
