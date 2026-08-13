import { Buffer } from "node:buffer";

import type {
  DurableIssueState,
  DurableRoundState,
  WorkerState,
} from "./durable-state.mts";
import type {
  IssueCommentSnapshot,
  IssueSnapshot,
  IssueWorkerConfiguration,
  IssueWorkerDependencies,
  MergeSnapshot,
  PullRequestSnapshot,
} from "./worker-contracts.mts";

export const APPROVAL_COMMAND = "/sandcastle approve";
const DISCLOSURE = "AI-generated and authorized by a human";

export interface GithubIssueWorkerOptions {
  issueNumber?: number;
  deliveryEnabled: boolean;
  mergeEnabled: boolean;
  allowMerge: boolean;
}

export type GithubIssueWorkerOutcome =
  | { kind: "idle" }
  | { kind: "candidate"; issueNumber: number; branch: string; sha: string }
  | { kind: "delivered"; issueNumber: number; branch: string; sha: string; pullRequestNumber: number }
  | { kind: "approval_consumed_disabled"; issueNumber: number; approvalIds: string[] }
  | { kind: "merged"; issueNumber: number; pullRequestNumber: number; sha: string };

export interface TrustedCommentClassification {
  ordinary: IssueCommentSnapshot[];
  approvals: IssueCommentSnapshot[];
}

export function isExactApproval(body: string): boolean {
  return Buffer.from(body, "utf8").equals(Buffer.from(APPROVAL_COMMAND, "utf8"));
}

export function classifyTrustedComments(
  issue: IssueSnapshot,
  trustedActorId: string,
  consumedIds: readonly string[],
): TrustedCommentClassification {
  const consumed = new Set(consumedIds);
  const trusted = issue.comments
    .filter((comment) =>
      comment.author.id === trustedActorId &&
      !consumed.has(comment.id) &&
      !comment.body.startsWith("<!-- sandcastle-status:v1 -->")
    )
    .sort(compareComments);
  return {
    ordinary: trusted.filter((comment) => !isExactApproval(comment.body)),
    approvals: trusted.filter((comment) => isExactApproval(comment.body)),
  };
}

export async function runGithubIssueWorker(
  options: GithubIssueWorkerOptions,
  dependencies: IssueWorkerDependencies,
  config: IssueWorkerConfiguration,
): Promise<GithubIssueWorkerOutcome> {
  assertConfiguration(config);
  dependencies.git.validateRepository();
  const context = await dependencies.github.validateContext();
  validateContext(context, config);

  let state = await dependencies.stateStore.load();
  if (state.repoId !== config.repositoryId) {
    throw new Error("Durable state repository identity mismatch.");
  }

  const commitIssue = async (issue: DurableIssueState): Promise<void> => {
    state = await dependencies.stateStore.commit(state.generation, {
      ...state,
      issues: { ...state.issues, [String(issue.number)]: issue },
    });
  };

  const selection = await selectIssue(options.issueNumber, state, dependencies, config);
  if (!selection) return { kind: "idle" };
  let { issue, durable } = selection;
  validateIssue(issue, durable, config);

  if (!durable) {
    const branch = `${config.branchPrefix}${issue.number}` as const;
    const remote = await dependencies.git.remoteBranchSha(branch);
    if (remote !== undefined) {
      throw new Error(`Untracked Issue #${issue.number} already has a remote branch.`);
    }
    if ((await dependencies.github.findPullRequests(branch)).length !== 0) {
      throw new Error(`Untracked Issue #${issue.number} already has a pull request.`);
    }
    dependencies.git.ensureBranch(branch, context.baseSha, context.baseSha);
    durable = {
      issueId: issue.id,
      number: issue.number,
      branch,
      baseRef: config.baseRef,
      baseSha: context.baseSha,
      consumedCommentIds: [],
      nextRound: 1,
    };
    await commitIssue(durable);
  }

  if (durable.round) {
    dependencies.git.assertCommitExists(durable.round.startSha);
    dependencies.git.assertNoPreservedWorktree(durable.branch);
  }

  if (durable.round && durable.round.phase !== "waiting") {
    if (["approval_pending", "ready_pending", "merge_pending", "merge_verified"].includes(durable.round.phase)) {
      if (durable.round.phase === "merge_verified") {
        const pullRequest = requiredPullRequest(durable);
        return {
          kind: "merged",
          issueNumber: durable.number,
          pullRequestNumber: pullRequest.number,
          sha: durable.lastDeliveredSha!,
        };
      }
      const approvalIds = durable.round.approvalCommentIds ?? [];
      const pending = classifyTrustedComments(issue, config.trustedActorId, durable.consumedCommentIds);
      const persistedApproval = issue.comments.find((comment) => comment.id === durable!.round!.approvalCommentId);
      const superseded = pending.approvals.at(-1)?.id !== durable.round.approvalCommentId;
      if (pending.ordinary.length > 0 || superseded) {
        durable = {
          ...durable,
          consumedCommentIds: unionIds(durable.consumedCommentIds, approvalIds),
          round: { ...durable.round, phase: "waiting" },
        };
        await commitIssue(durable);
      } else if (!persistedApproval ||
          persistedApproval.updatedAt !== durable.round.approvalUpdatedAt ||
          !isExactApproval(persistedApproval.body)) {
        durable = {
          ...durable,
          consumedCommentIds: unionIds(durable.consumedCommentIds, approvalIds),
          round: { ...durable.round, phase: "waiting" },
        };
        await commitIssue(durable);
        return { kind: "idle" };
      } else {
        if (!options.mergeEnabled || !options.allowMerge) {
          durable = {
            ...durable,
            consumedCommentIds: unionIds(durable.consumedCommentIds, approvalIds),
            round: { ...durable.round, phase: "waiting" },
          };
          await commitIssue(durable);
          return { kind: "approval_consumed_disabled", issueNumber: issue.number, approvalIds };
        }
        return continueApproval(dependencies, config, durable, [persistedApproval], commitIssue);
      }
    } else {
      return continueRound(options, dependencies, config, issue, durable, commitIssue);
    }
  }

  const classified = classifyTrustedComments(
    issue,
    config.trustedActorId,
    durable.consumedCommentIds,
  );

  if (durable.lastDeliveredSha && classified.ordinary.length === 0 && classified.approvals.length > 0) {
    if (!options.mergeEnabled || !options.allowMerge) {
      durable = {
        ...durable,
        consumedCommentIds: unionIds(
          durable.consumedCommentIds,
          classified.approvals.map((comment) => comment.id),
        ),
      };
      await commitIssue(durable);
      return {
        kind: "approval_consumed_disabled",
        issueNumber: issue.number,
        approvalIds: classified.approvals.map((comment) => comment.id),
      };
    }
    return continueApproval(dependencies, config, durable, classified.approvals, commitIssue);
  }

  if (durable.lastDeliveredSha && classified.ordinary.length === 0) {
    return { kind: "idle" };
  }

  const startSha = durable.lastDeliveredSha ?? durable.baseSha;
  if (durable.lastDeliveredSha) {
    const remote = await dependencies.git.remoteBranchSha(durable.branch);
    if (remote !== durable.lastDeliveredSha) {
      throw new Error(`Tracked Issue #${issue.number} remote branch drifted.`);
    }
    assertPersistedPullRequest(durable, await dependencies.github.findPullRequests(durable.branch));
    dependencies.git.ensureBranch(durable.branch, startSha, startSha);
  }

  const triggerCommentIds = classified.ordinary.map((comment) => comment.id);
  const approvalCommentIds = classified.approvals.map((comment) => comment.id);
  const round: DurableRoundState = {
    number: durable.nextRound,
    phase: "implement_pending",
    trigger: {
      kind: durable.lastDeliveredSha ? "revision" : "initial",
      commentIds: triggerCommentIds,
      snapshotCommentIds: issue.comments.map((comment) => comment.id),
    },
    startSha,
    statusMarker: "<!-- sandcastle-status:v1 -->",
    instructionSnapshot: {
      title: issue.title,
      body: issue.body,
      url: issue.url,
      comments: classified.ordinary.map((comment) => ({
        id: comment.id,
        body: comment.body,
        createdAt: comment.createdAt,
        updatedAt: comment.updatedAt,
      })),
    },
    approvalCommentIds,
  };
  durable = { ...durable, round };
  await commitIssue(durable);
  return continueRound(options, dependencies, config, issue, durable, commitIssue);
}

async function continueRound(
  options: GithubIssueWorkerOptions,
  dependencies: IssueWorkerDependencies,
  config: IssueWorkerConfiguration,
  issueSnapshot: IssueSnapshot,
  initial: DurableIssueState,
  commitIssue: (issue: DurableIssueState) => Promise<void>,
): Promise<GithubIssueWorkerOutcome> {
  let durable = initial;
  let round = requiredRound(durable);
  const issue = agentIssueSnapshot(issueSnapshot, round, config.trustedActorId);

  if (round.phase === "implement_pending") {
    const current = dependencies.git.branchSha(durable.branch);
    if (current !== round.startSha) {
      dependencies.git.restoreOwnedBranch(
        durable.branch,
        round.startSha,
        `refs/sandcastle/recovery/issue-${durable.number}-round-${round.number}-implement`,
      );
    }
    const result = await withWorkspace(dependencies, durable.branch, round.startSha, issue, (workspace) => workspace.implement(issue));
    const sha = requirePhaseHead(dependencies, durable.branch, round.startSha, result.commits, "implementation");
    await dependencies.afterEffect?.("implementation");
    round = { ...round, implementation: { sha, outcome: "completed" }, phase: "gate1_pending" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "gate1_pending") {
    const sha = requiredQualification(round.implementation, "implementation").sha;
    dependencies.git.assertBranchSha(durable.branch, sha);
    await withWorkspace(dependencies, durable.branch, round.startSha, issue, (workspace) => workspace.runGates());
    dependencies.git.assertBranchSha(durable.branch, sha);
    await dependencies.afterEffect?.("gate1");
    round = { ...round, gate1: { sha, outcome: "passed" }, phase: "review_pending" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "review_pending") {
    const before = requiredQualification(round.gate1, "gate1").sha;
    if (dependencies.git.branchSha(durable.branch) !== before) {
      dependencies.git.restoreOwnedBranch(
        durable.branch,
        before,
        `refs/sandcastle/recovery/issue-${durable.number}-round-${round.number}-review`,
      );
    }
    const result = await withWorkspace(dependencies, durable.branch, round.startSha, issue, (workspace) => workspace.review(issue));
    const sha = requirePhaseHead(dependencies, durable.branch, before, result.commits, "review");
    await dependencies.afterEffect?.("review");
    const corrected = sha !== before;
    round = {
      ...round,
      review: { sha, outcome: corrected ? "corrected" : "approved" },
      phase: corrected ? "gate2_pending" : "candidate_verified",
      candidateSha: corrected ? undefined : sha,
    };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "gate2_pending") {
    const sha = requiredQualification(round.review, "review").sha;
    dependencies.git.assertBranchSha(durable.branch, sha);
    await withWorkspace(dependencies, durable.branch, round.startSha, issue, (workspace) => workspace.runGates());
    dependencies.git.assertBranchSha(durable.branch, sha);
    await dependencies.afterEffect?.("gate2");
    round = { ...round, gate2: { sha, outcome: "passed" }, candidateSha: sha, phase: "candidate_verified" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "candidate_verified") {
    const candidate = requiredCandidate(round);
    validateCandidate(dependencies, durable, round, candidate);
    if (candidate === round.startSha) {
      throw new Error(
        "Issue round produced no commit beyond its durable start SHA; a draft pull request cannot represent a no-change round.",
      );
    }
    if (!options.deliveryEnabled) {
      return { kind: "candidate", issueNumber: durable.number, branch: durable.branch, sha: candidate };
    }
    // Agents and gates can run for a long time. Reread the immutable host
    // context and open Issue immediately before recording push intent so a
    // mid-round ownership, base, actor, or Issue-state change fails before
    // the trusted host writes the remote ref.
    validateContext(await dependencies.github.validateContext(), config);
    validateIssue(
      await dependencies.github.getIssue(durable.number),
      durable,
      config,
    );
    round = {
      ...round,
      expectedRemoteSha: durable.lastDeliveredSha ?? null,
      phase: "push_pending",
    };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "push_pending") {
    const candidate = requiredCandidate(round);
    validateCandidate(dependencies, durable, round, candidate);
    dependencies.git.validateForDelivery();
    const remote = await dependencies.git.remoteBranchSha(durable.branch);
    if (remote !== candidate) {
      if ((remote ?? null) !== round.expectedRemoteSha) {
        throw new Error(`Remote drift detected for ${durable.branch}.`);
      }
      await dependencies.git.pushExact({
        branch: durable.branch,
        candidateSha: candidate,
        expectedRemoteSha: round.expectedRemoteSha ?? null,
      });
      await dependencies.afterEffect?.("push");
    }
    if (await dependencies.git.remoteBranchSha(durable.branch) !== candidate) {
      throw new Error("Exact remote candidate verification failed.");
    }
    round = { ...round, pushedSha: candidate, phase: "push_verified" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "push_verified") {
    round = { ...round, phase: "pr_pending" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "pr_pending") {
    validateContext(await dependencies.github.validateContext(), config);
    validateIssue(
      await dependencies.github.getIssue(durable.number),
      durable,
      config,
    );
    const candidate = requiredCandidate(round);
    dependencies.git.validateForDelivery();
    if (await dependencies.git.remoteBranchSha(durable.branch) !== candidate) {
      throw new Error("Remote branch no longer equals the qualified candidate before pull request reconciliation.");
    }
    const title = `Sandcastle work for Issue #${durable.number}`;
    const body = `Tracks Issue #${durable.number} for human review; does not close it.\n\n${DISCLOSURE}.`;
    const pullRequests = await dependencies.github.findPullRequests(durable.branch);
    if (pullRequests.length > 1) throw new Error("More than one pull request exists for the Issue branch.");
    let pullRequest = pullRequests[0];
    if (!pullRequest || !isExactDraftPullRequest(pullRequest, durable, candidate, title, body)) {
      const effect = pullRequest ? "pull_request_reconcile" : "pull_request_create";
      pullRequest = await dependencies.github.ensureExactlyOneDraftPullRequest({
        issueNumber: durable.number,
        headRef: durable.branch,
        baseRef: durable.baseRef,
        title,
        body,
      });
      await dependencies.afterEffect?.(effect);
    }
    if (!isExactDraftPullRequest(pullRequest, durable, candidate, title, body)) {
      throw new Error("Draft pull request reconciliation did not reach the exact candidate.");
    }
    durable = {
      ...durable,
      pullRequest: persistedPullRequest(pullRequest),
      round: { ...round, phase: "pr_verified" },
    };
    round = requiredRound(durable);
    await commitIssue(durable);
  }

  if (round.phase === "pr_verified") {
    round = { ...round, phase: "report_pending" };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "report_pending") {
    validateContext(await dependencies.github.validateContext(), config);
    const preStatusIssue = await dependencies.github.getIssue(durable.number);
    validateIssue(preStatusIssue, durable, config);
    const candidate = requiredCandidate(round);
    const pullRequest = requiredPullRequest(durable);
    dependencies.git.validateForDelivery();
    if (await dependencies.git.remoteBranchSha(durable.branch) !== candidate) {
      throw new Error("Remote branch no longer equals the qualified candidate before status reconciliation.");
    }
    const currentPullRequests = await dependencies.github.findPullRequests(durable.branch);
    assertPersistedPullRequest(durable, currentPullRequests);
    const currentPullRequest = currentPullRequests[0]!;
    const title = `Sandcastle work for Issue #${durable.number}`;
    const prBody = `Tracks Issue #${durable.number} for human review; does not close it.\n\n${DISCLOSURE}.`;
    if (!isExactDraftPullRequest(currentPullRequest, durable, candidate, title, prBody) ||
        currentPullRequest.id !== pullRequest.nodeId) {
      throw new Error("Persisted draft pull request drifted before status reconciliation.");
    }
    const body = renderStatus(round.statusMarker, "delivered", durable, candidate, pullRequest.number, config.maxStatusBytes);
    const comment = await dependencies.github.upsertStatusComment(durable.number, body);
    await dependencies.afterEffect?.("status");
    // The status marker defines the delivery boundary. Exact approvals already
    // present at that boundary cannot authorize a candidate the human had not
    // yet been shown; a later approval remains actionable.
    const postStatusIssue = await dependencies.github.getIssue(durable.number);
    validateIssue(postStatusIssue, durable, config);
    const consumed = unionIds(
      durable.consumedCommentIds,
      round.trigger.commentIds,
      round.approvalCommentIds ?? [],
      postStatusIssue.comments
        .filter((comment) =>
          comment.author.id === config.trustedActorId &&
          isExactApproval(comment.body)
        )
        .map((comment) => comment.id),
    );
    round = { ...round, statusCommentId: comment.id, phase: "waiting" };
    durable = {
      ...durable,
      lastDeliveredSha: candidate,
      consumedCommentIds: consumed,
      nextRound: round.number + 1,
      round,
    };
    await commitIssue(durable);
    return {
      kind: "delivered",
      issueNumber: durable.number,
      branch: durable.branch,
      sha: candidate,
      pullRequestNumber: pullRequest.number,
    };
  }

  if (round.phase === "waiting") {
    const pullRequest = requiredPullRequest(durable);
    return {
      kind: "delivered",
      issueNumber: durable.number,
      branch: durable.branch,
      sha: requiredCandidate(round),
      pullRequestNumber: pullRequest.number,
    };
  }
  throw new Error(`Unsupported Issue round phase: ${round.phase}.`);
}

async function continueApproval(
  dependencies: IssueWorkerDependencies,
  config: IssueWorkerConfiguration,
  initial: DurableIssueState,
  approvals: IssueCommentSnapshot[],
  commitIssue: (issue: DurableIssueState) => Promise<void>,
): Promise<GithubIssueWorkerOutcome> {
  let durable = initial;
  const pullRequest = requiredPullRequest(durable);
  const approval = approvals.at(-1)!;
  let round = requiredRound(durable);
  if (round.phase === "waiting") {
    round = {
      ...round,
      phase: "approval_pending",
      approvalCommentId: approval.id,
      approvalCommentIds: approvals.map((comment) => comment.id),
      approvalUpdatedAt: approval.updatedAt,
    };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "approval_pending") {
    const first = await dependencies.github.readMergeSnapshot(durable.number, pullRequest.number);
    validateMergeSnapshot(first, durable, approval, config);
    const second = await dependencies.github.readMergeSnapshot(durable.number, pullRequest.number);
    validateMergeSnapshot(second, durable, approval, config);
    const fingerprint = mergeFingerprint(second, approval);
    if (fingerprint !== mergeFingerprint(first, approval)) {
      throw new Error("Merge evidence changed during approval preflight.");
    }
    round = { ...round, phase: "ready_pending", mergeEvidenceFingerprint: fingerprint };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "ready_pending") {
    const beforeReady = await dependencies.github.readMergeSnapshot(durable.number, pullRequest.number);
    validateMergeSnapshot(beforeReady, durable, approval, config);
    if (!readyIntentMatches(round.mergeEvidenceFingerprint, beforeReady, approval)) {
      throw new Error("Merge evidence changed before draft readiness.");
    }
    if (beforeReady.pullRequest.isDraft) {
      await dependencies.github.markPullRequestReady(pullRequest.id);
      await dependencies.afterEffect?.("pull_request_ready");
    }
    const afterReady = await dependencies.github.readMergeSnapshot(durable.number, pullRequest.number);
    validateMergeSnapshot(afterReady, durable, approval, config);
    if (afterReady.pullRequest.isDraft) throw new Error("Pull request is still draft after readiness reconciliation.");
    round = {
      ...round,
      phase: "merge_pending",
      mergeEvidenceFingerprint: mergeFingerprint(afterReady, approval),
      mergeBaseSha: afterReady.repository.baseSha,
    };
    durable = { ...durable, round };
    await commitIssue(durable);
  }

  if (round.phase === "merge_pending") {
    const finalSnapshot = await dependencies.github.readMergeSnapshot(durable.number, pullRequest.number);
    validateMergeSnapshot(
      finalSnapshot,
      durable,
      approval,
      config,
      true,
      round.mergeBaseSha,
    );
    if (!mergeIntentMatches(
      round.mergeEvidenceFingerprint,
      finalSnapshot,
      approval,
      round.mergeBaseSha,
    )) {
      throw new Error("Merge evidence changed before SHA compare-and-swap.");
    }
    if (finalSnapshot.pullRequest.state === "OPEN") {
      const result = await dependencies.github.mergePullRequest(
        pullRequest.id,
        durable.lastDeliveredSha!,
        config.mergeMethod,
      );
      if (!result.merged) throw new Error("GitHub did not merge the pull request.");
      await dependencies.afterEffect?.("merge");
      const verified = await dependencies.github.readMergeSnapshot(
        durable.number,
        pullRequest.number,
      );
      validateMergeSnapshot(
        verified,
        durable,
        approval,
        config,
        true,
        round.mergeBaseSha,
      );
      if (verified.pullRequest.state !== "MERGED" ||
          !mergeIntentMatches(
            round.mergeEvidenceFingerprint,
            verified,
            approval,
            round.mergeBaseSha,
          ) ||
          (result.mergeCommitOid !== undefined &&
            verified.pullRequest.mergeCommitSha !== result.mergeCommitOid)) {
        throw new Error("Merged pull request did not reconcile to the exact compare-and-swap result.");
      }
    }
    round = { ...round, phase: "merge_verified" };
    durable = {
      ...durable,
      consumedCommentIds: unionIds(durable.consumedCommentIds, round.approvalCommentIds ?? []),
      round,
    };
    await commitIssue(durable);
  }

  if (round.phase !== "merge_verified") {
    throw new Error(`Unsupported approval phase: ${round.phase}.`);
  }
  return {
    kind: "merged",
    issueNumber: durable.number,
    pullRequestNumber: pullRequest.number,
    sha: durable.lastDeliveredSha!,
  };
}

async function selectIssue(
  explicit: number | undefined,
  state: WorkerState,
  dependencies: IssueWorkerDependencies,
  config: IssueWorkerConfiguration,
): Promise<{ issue: IssueSnapshot; durable?: DurableIssueState } | undefined> {
  if (explicit !== undefined) {
    const issue = await dependencies.github.getIssue(explicit);
    return { issue, durable: state.issues[String(explicit)] };
  }
  const active = Object.values(state.issues)
    .filter((item) => item.round && item.round.phase !== "waiting" && item.round.phase !== "merge_verified")
    .sort((left, right) => left.number - right.number)[0];
  if (active) return { issue: await dependencies.github.getIssue(active.number), durable: active };

  const actionable: Array<{ issue: IssueSnapshot; durable: DurableIssueState; at: string; id: string }> = [];
  for (const durable of Object.values(state.issues)) {
    if (!durable.lastDeliveredSha) continue;
    const issue = await dependencies.github.getIssue(durable.number);
    const comments = classifyTrustedComments(issue, config.trustedActorId, durable.consumedCommentIds);
    const chosen = comments.ordinary[0] ?? comments.approvals[0];
    if (chosen) actionable.push({ issue, durable, at: chosen.createdAt, id: chosen.id });
  }
  actionable.sort((left, right) => left.at.localeCompare(right.at) || left.id.localeCompare(right.id));
  if (actionable[0]) return actionable[0];

  const labeled = await dependencies.github.getLabeledIssues(config.queueLabel);
  for (const issue of labeled) {
    const durable = state.issues[String(issue.number)];
    if (!durable) return { issue };
    if (!durable.lastDeliveredSha || durable.round?.phase !== "waiting") {
      return { issue, durable };
    }
    const comments = classifyTrustedComments(
      issue,
      config.trustedActorId,
      durable.consumedCommentIds,
    );
    if (comments.ordinary.length > 0 || comments.approvals.length > 0) {
      return { issue, durable };
    }
  }
  return undefined;
}

function validateContext(context: Awaited<ReturnType<IssueWorkerDependencies["github"]["validateContext"]>>, config: IssueWorkerConfiguration): void {
  if (context.repositoryId !== config.repositoryId || context.nameWithOwner !== config.repositoryNameWithOwner) {
    throw new Error("Immutable GitHub repository identity mismatch.");
  }
  if (context.owner.id !== config.ownerId || context.deliveryActor.id !== config.deliveryActorId || context.trustedActor.id !== config.trustedActorId) {
    throw new Error("Immutable GitHub actor identity mismatch.");
  }
  if (context.deliveryPermission !== "WRITE") {
    throw new Error("Delivery actor must have exact WRITE permission.");
  }
  if (context.baseRef !== config.baseRef) throw new Error("Configured base ref mismatch.");
  if (context.deleteBranchOnMerge) throw new Error("Automatic branch deletion must be disabled.");
}

function validateIssue(issue: IssueSnapshot, durable: DurableIssueState | undefined, config: IssueWorkerConfiguration): void {
  if (issue.state !== "OPEN") throw new Error(`Issue #${issue.number} is not open.`);
  if (!issue.commentsComplete) throw new Error(`Issue #${issue.number} comments are incomplete.`);
  if (issue.author.id !== config.trustedActorId) throw new Error(`Issue #${issue.number} author is not the configured trusted actor.`);
  if (durable && durable.issueId !== issue.id) throw new Error(`Issue #${issue.number} immutable identity changed.`);
}

function agentIssueSnapshot(issue: IssueSnapshot, round: DurableRoundState, trustedActorId: string): IssueSnapshot {
  const persisted = round.instructionSnapshot;
  if (!persisted) throw new Error("Durable round has no immutable instruction snapshot.");
  return {
    ...issue,
    title: persisted.title,
    body: persisted.body,
    url: persisted.url,
    labels: [],
    comments: persisted.comments.map((comment) => ({
      ...comment,
      author: { id: trustedActorId, login: issue.author.login },
    })),
  };
}

async function withWorkspace<T>(
  dependencies: IssueWorkerDependencies,
  branch: string,
  baseCommit: string,
  issue: IssueSnapshot,
  run: (workspace: Awaited<ReturnType<IssueWorkerDependencies["createWorkspace"]>>) => Promise<T>,
): Promise<T> {
  const workspace = await dependencies.createWorkspace({ branch, baseCommit, issue });
  let result: T;
  let runError: unknown;
  try {
    result = await run(workspace);
  } catch (error) {
    runError = error;
    throw error;
  } finally {
    const closed = await workspace.close();
    if (closed.preservedWorktreePath) {
      const dirty = new Error(
        "Workspace left uncommitted changes; refusing qualification and automatic retry.",
      );
      if (runError) {
        throw new AggregateError(
          [runError, dirty],
          "Issue phase failed and preserved a dirty recovery worktree.",
        );
      }
      throw dirty;
    }
  }
  return result!;
}

function requirePhaseHead(
  dependencies: IssueWorkerDependencies,
  branch: string,
  before: string,
  commits: Array<{ sha: string }>,
  phase: string,
): string {
  const after = dependencies.git.branchSha(branch);
  if (!after) throw new Error(`${phase} removed the owned Issue branch.`);
  if (commits.length === 0 && after !== before) throw new Error(`${phase} changed the branch but reported zero commits.`);
  if (commits.length > 0 && (after === before || commits.at(-1)?.sha !== after)) {
    throw new Error(`${phase} commit report does not match the exact branch head.`);
  }
  if (!dependencies.git.isAncestor(before, after)) throw new Error(`${phase} did not advance from its verified start SHA.`);
  return after;
}

function validateCandidate(
  dependencies: IssueWorkerDependencies,
  durable: DurableIssueState,
  round: DurableRoundState,
  candidate: string,
): void {
  dependencies.git.assertBranchSha(durable.branch, candidate);
  if (!dependencies.git.isAncestor(round.startSha, candidate)) throw new Error("Candidate is not descended from the round start SHA.");
  if (durable.lastDeliveredSha && !dependencies.git.isAncestor(durable.lastDeliveredSha, candidate)) {
    throw new Error("Revision candidate is not descended from the last delivered SHA.");
  }
  const finalGate = round.gate2 ?? round.gate1;
  if (!finalGate || finalGate.sha !== candidate) throw new Error("Project gates are not bound to the exact candidate SHA.");
  if (!round.review || round.review.sha !== candidate) throw new Error("Independent review is not bound to the exact candidate SHA.");
}

function validateMergeSnapshot(
  snapshot: MergeSnapshot,
  durable: DurableIssueState,
  approval: IssueCommentSnapshot,
  config: IssueWorkerConfiguration,
  allowMerged = false,
  expectedPreMergeBaseSha?: string,
): void {
  const pr = requiredPullRequest(durable);
  validateContext(snapshot.repository, config);
  if (snapshot.repositoryId !== config.repositoryId || snapshot.issueId !== durable.issueId) throw new Error("Merge snapshot identity mismatch.");
  if (snapshot.issueState !== "OPEN" || !snapshot.commentsComplete) throw new Error("Merge snapshot is incomplete or Issue is closed.");
  if (snapshot.pullRequest.id !== pr.nodeId || snapshot.pullRequest.number !== pr.number) throw new Error("Merge snapshot pull request identity mismatch.");
  if (snapshot.pullRequest.autoMergeEnabled) throw new Error("Pull request auto-merge must remain disabled.");
  if ((!allowMerged && snapshot.pullRequest.state !== "OPEN") || (allowMerged && !["OPEN", "MERGED"].includes(snapshot.pullRequest.state))) throw new Error("Pull request is not in an allowed merge state.");
  if (snapshot.pullRequest.baseRef !== durable.baseRef || snapshot.pullRequest.headRef !== durable.branch || snapshot.pullRequest.headSha !== durable.lastDeliveredSha) throw new Error("Merge base/head changed.");
  if (snapshot.pullRequest.state === "MERGED") {
    if (!expectedPreMergeBaseSha ||
        !snapshot.pullRequest.mergeCommitSha ||
        snapshot.pullRequest.mergedByActorId !== config.deliveryActorId ||
        snapshot.repository.baseSha !== snapshot.pullRequest.mergeCommitSha ||
        ![expectedPreMergeBaseSha, snapshot.pullRequest.mergeCommitSha]
          .includes(snapshot.pullRequest.baseSha)) {
      throw new Error("Merged pull request does not match the controlled base transition.");
    }
  } else if (snapshot.pullRequest.baseSha !== snapshot.repository.baseSha ||
      (expectedPreMergeBaseSha !== undefined &&
        snapshot.pullRequest.baseSha !== expectedPreMergeBaseSha)) {
    throw new Error("Pull request base OID is not the current configured base OID.");
  }
  if (!snapshot.requiredChecksPass) throw new Error("Required checks do not pass.");
  if (!snapshot.checksComplete || config.requiredCheckNames.length === 0) {
    throw new Error("Required check policy is missing or incomplete.");
  }
  for (const requiredName of config.requiredCheckNames) {
    const matches = snapshot.checks.filter((check) => check.name === requiredName);
    if (matches.length !== 1 || matches[0]!.status !== "COMPLETED" ||
        !["SUCCESS", "NEUTRAL", "SKIPPED"].includes(matches[0]!.conclusion ?? "")) {
      throw new Error(`Configured required check did not pass exactly once: ${requiredName}.`);
    }
  }
  const unchanged = snapshot.comments.find((comment) => comment.id === approval.id);
  if (!unchanged || unchanged.author.id !== config.trustedActorId || unchanged.updatedAt !== approval.updatedAt || !isExactApproval(unchanged.body)) {
    throw new Error("Approval is missing, edited, or not exact.");
  }
  const approvalIds = new Set(durable.round?.approvalCommentIds ?? [approval.id]);
  const newerActionable = snapshot.comments.some((comment) =>
    comment.author.id === config.trustedActorId &&
    comment.id !== approval.id &&
    !approvalIds.has(comment.id) &&
    !durable.consumedCommentIds.includes(comment.id)
  );
  if (newerActionable) throw new Error("A newer trusted comment invalidated approval.");
}

function mergeFingerprint(snapshot: MergeSnapshot, approval: IssueCommentSnapshot): string {
  return JSON.stringify({
    repositoryId: snapshot.repositoryId,
    issueId: snapshot.issueId,
    issueState: snapshot.issueState,
    pullRequest: stablePullRequest(snapshot.pullRequest),
    checksFingerprint: snapshot.checksFingerprint,
    commentsFingerprint: snapshot.commentsFingerprint,
    requiredChecksPass: snapshot.requiredChecksPass,
    approval: { id: approval.id, updatedAt: approval.updatedAt, body: approval.body },
  });
}

function readyIntentMatches(
  intended: string | undefined,
  observed: MergeSnapshot,
  approval: IssueCommentSnapshot,
): boolean {
  if (!intended) return false;
  if (mergeFingerprint(observed, approval) === intended) return true;
  if (observed.pullRequest.isDraft) return false;
  return mergeFingerprint(
    { ...observed, pullRequest: { ...observed.pullRequest, isDraft: true } },
    approval,
  ) === intended;
}

function mergeIntentMatches(
  intended: string | undefined,
  observed: MergeSnapshot,
  approval: IssueCommentSnapshot,
  expectedPreMergeBaseSha: string | undefined,
): boolean {
  if (!intended) return false;
  if (mergeFingerprint(observed, approval) === intended) return true;
  if (observed.pullRequest.state !== "MERGED") return false;
  return mergeFingerprint(
    {
      ...observed,
      pullRequest: {
        ...observed.pullRequest,
        state: "OPEN",
        baseSha: expectedPreMergeBaseSha ?? observed.pullRequest.baseSha,
      },
    },
    approval,
  ) === intended;
}

function stablePullRequest(pullRequest: PullRequestSnapshot) {
  const {
    mergeCommitSha: _mergeCommitSha,
    mergedByActorId: _mergedByActorId,
    ...stable
  } = pullRequest;
  return stable;
}

function assertPersistedPullRequest(durable: DurableIssueState, pullRequests: PullRequestSnapshot[]): void {
  const persisted = requiredPullRequest(durable);
  if (pullRequests.length !== 1 || pullRequests[0]!.id !== persisted.nodeId || pullRequests[0]!.number !== persisted.number) {
    throw new Error("Tracked Issue must have exactly its one persisted pull request.");
  }
}

function isExactDraftPullRequest(
  pr: PullRequestSnapshot,
  durable: DurableIssueState,
  candidate: string,
  title: string,
  body: string,
): boolean {
  return pr.state === "OPEN" && pr.isDraft && !pr.autoMergeEnabled && pr.headRef === durable.branch &&
    pr.headSha === candidate && pr.baseRef === durable.baseRef &&
    pr.title === title && pr.body === body;
}

function persistedPullRequest(pr: PullRequestSnapshot) {
  return { nodeId: pr.id, number: pr.number, headRef: pr.headRef, baseRef: pr.baseRef, url: pr.url, headSha: pr.headSha };
}

function renderStatus(marker: string, stage: "delivered", durable: DurableIssueState, sha: string, pr: number, maxBytes: number): string {
  const body = [
    marker,
    "## Sandcastle worker 狀態",
    "",
    `狀態：${stage === "delivered" ? "已交付草稿" : stage}`,
    `Branch：\`${durable.branch}\``,
    `已驗證 commit：\`${sha}\``,
    `Draft PR：#${pr}`,
    "Issue 保持開啟，仍需人工審查。",
    "",
    DISCLOSURE,
  ].join("\n");
  if (Buffer.byteLength(body, "utf8") > maxBytes) throw new Error("Harness status exceeds configured byte bound.");
  return body;
}

function requiredRound(issue: DurableIssueState): DurableRoundState {
  if (!issue.round) throw new Error(`Issue #${issue.number} has no durable round.`);
  return issue.round;
}

function requiredCandidate(round: DurableRoundState): string {
  if (!round.candidateSha) throw new Error("Durable round has no verified candidate SHA.");
  return round.candidateSha;
}

function requiredQualification(value: { sha: string; outcome: string } | undefined, name: string) {
  if (!value) throw new Error(`Durable round has no ${name} qualification.`);
  return value;
}

function requiredPullRequest(issue: DurableIssueState) {
  if (!issue.pullRequest) throw new Error(`Issue #${issue.number} has no persisted pull request.`);
  return issue.pullRequest;
}

function compareComments(left: IssueCommentSnapshot, right: IssueCommentSnapshot): number {
  return left.createdAt.localeCompare(right.createdAt) || left.id.localeCompare(right.id);
}

function unionIds(...groups: readonly (readonly string[])[]): string[] {
  return [...new Set(groups.flat())].sort();
}

function assertConfiguration(config: IssueWorkerConfiguration): void {
  if (!config.repositoryId || !config.repositoryNameWithOwner || !config.ownerId || !config.deliveryActorId || !config.trustedActorId) throw new Error("Worker immutable identity configuration is incomplete.");
  if (config.branchPrefix !== "sandcastle/issue-") throw new Error("Worker branch prefix is not permitted.");
  if (!Number.isSafeInteger(config.maxStatusBytes) || config.maxStatusBytes < 256) throw new Error("Worker status byte bound is invalid.");
  if (!Array.isArray(config.requiredCheckNames) ||
      !config.requiredCheckNames.every((name) => typeof name === "string")) {
    throw new Error("Worker required-check configuration is invalid.");
  }
}
