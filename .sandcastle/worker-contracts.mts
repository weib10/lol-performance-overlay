import type { AtomicStateStore } from "./durable-state.mts";

export interface ImmutableActor {
  id: string;
  login: string;
}

export interface IssueCommentSnapshot {
  id: string;
  author: ImmutableActor;
  body: string;
  createdAt: string;
  updatedAt: string;
}

export interface IssueSnapshot {
  id: string;
  number: number;
  title: string;
  body: string;
  url: string;
  author: ImmutableActor;
  labels: string[];
  comments: IssueCommentSnapshot[];
  commentsComplete: boolean;
  createdAt: string;
  state: "OPEN" | "CLOSED";
}

export interface RepositoryContext {
  repositoryId: string;
  nameWithOwner: string;
  owner: ImmutableActor;
  deliveryActor: ImmutableActor;
  deliveryPermission: "ADMIN" | "MAINTAIN" | "WRITE";
  trustedActor: ImmutableActor;
  baseRef: string;
  baseSha: string;
  deleteBranchOnMerge: boolean;
}

export interface PullRequestSnapshot {
  id: string;
  number: number;
  state: "OPEN" | "CLOSED" | "MERGED";
  isDraft: boolean;
  title: string;
  body: string;
  headRef: string;
  headSha: string;
  baseRef: string;
  baseSha: string;
  url: string;
  autoMergeEnabled: boolean;
  mergeCommitSha?: string;
  mergedByActorId?: string;
}

export interface MergeSnapshot {
  repository: RepositoryContext;
  repositoryId: string;
  issueId: string;
  issueState: "OPEN" | "CLOSED";
  pullRequest: PullRequestSnapshot;
  comments: IssueCommentSnapshot[];
  commentsComplete: boolean;
  checks: Array<{
    id: string;
    name: string;
    status: string;
    conclusion: string | null;
  }>;
  checksComplete: boolean;
  requiredChecksPass: boolean;
  checksFingerprint: string;
  /** Covers fully paginated Issue, PR conversation, and PR review comments. */
  commentsFingerprint: string;
}

/**
 * The only GitHub mutations the worker is allowed to request. There are
 * deliberately no Issue-close, label, branch-delete, release, deploy,
 * publish, force-push, admin-merge, or auto-merge methods here.
 */
export interface HostGithubDelivery {
  validateContext(): Promise<RepositoryContext>;
  getIssue(number: number): Promise<IssueSnapshot>;
  getLabeledIssues(label: string): Promise<IssueSnapshot[]>;
  findPullRequests(headRef: string): Promise<PullRequestSnapshot[]>;
  ensureExactlyOneDraftPullRequest(input: {
    issueNumber: number;
    headRef: string;
    baseRef: string;
    title: string;
    body: string;
  }): Promise<PullRequestSnapshot>;
  upsertStatusComment(
    issueNumber: number,
    body: string,
  ): Promise<IssueCommentSnapshot>;
  readMergeSnapshot(
    issueNumber: number,
    pullRequestNumber: number,
  ): Promise<MergeSnapshot>;
  markPullRequestReady(pullRequestId: string): Promise<void>;
  mergePullRequest(
    pullRequestId: string,
    expectedHeadSha: string,
    method: "MERGE" | "SQUASH" | "REBASE",
  ): Promise<{ merged: boolean; mergeCommitOid?: string }>;
}

/** A structural subset of the host Git adapter used by the state machine. */
export interface HostIssueGit {
  validateRepository(): unknown;
  validateForDelivery(): unknown;
  assertNoPreservedWorktree(branch: string): void;
  assertCommitExists(sha: string): void;
  branchSha(branch: string): string | undefined;
  ensureBranch(
    branch: string,
    startSha: string,
    expectedExistingSha?: string,
  ): void;
  restoreOwnedBranch(
    branch: string,
    startSha: string,
    recoveryRef: string,
  ): void;
  assertBranchSha(branch: string, expectedSha: string): void;
  isAncestor(ancestor: string, descendant: string): boolean;
  remoteBranchSha(branch: string): Promise<string | undefined>;
  pushExact(input: {
    branch: string;
    candidateSha: string;
    expectedRemoteSha: string | null;
  }): Promise<void>;
}

export interface AgentPhaseResult {
  commits: Array<{ sha: string }>;
  summary: string;
}

export interface GateResult {
  summary: string;
}

export interface IssueWorkspace {
  implement(issue: IssueSnapshot): Promise<AgentPhaseResult>;
  runGates(): Promise<GateResult>;
  review(issue: IssueSnapshot): Promise<AgentPhaseResult>;
  close(): Promise<{ preservedWorktreePath?: string }>;
}

export interface IssueWorkerConfiguration {
  repositoryId: string;
  repositoryNameWithOwner: string;
  ownerId: string;
  deliveryActorId: string;
  trustedActorId: string;
  baseRef: string;
  queueLabel: string;
  branchPrefix: "sandcastle/issue-";
  maxStatusBytes: number;
  mergeMethod: "MERGE" | "SQUASH" | "REBASE";
  requiredCheckNames: string[];
}

export interface IssueWorkerDependencies {
  github: HostGithubDelivery;
  git: HostIssueGit;
  stateStore: AtomicStateStore;
  createWorkspace(input: {
    branch: string;
    baseCommit: string;
    issue: IssueSnapshot;
  }): Promise<IssueWorkspace>;
  /** Test seam representing a process death after a durable effect. */
  afterEffect?(effect: WorkerEffect): void | Promise<void>;
}

export type WorkerEffect =
  | "status"
  | "implementation"
  | "gate1"
  | "review"
  | "gate2"
  | "push"
  | "pull_request_create"
  | "pull_request_reconcile"
  | "pull_request_ready"
  | "merge";
