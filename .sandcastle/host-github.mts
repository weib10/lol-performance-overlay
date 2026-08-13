import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { realpathSync, statSync } from "node:fs";
import { isAbsolute } from "node:path";

import { sanitizeHostEnvironment } from "./host-environment.mts";

export interface GithubActor {
  id: string;
  login: string;
  type: string;
}

export interface GithubIssueComment {
  id: string;
  author: GithubActor;
  body: string;
  createdAt: string;
  updatedAt: string;
  url: string;
}

export interface GithubIssue {
  id: string;
  number: number;
  title: string;
  body: string;
  url: string;
  state: "OPEN" | "CLOSED";
  author: GithubActor;
  labels: string[];
  comments: GithubIssueComment[];
  commentsComplete: true;
  createdAt: string;
  updatedAt: string;
}

export interface GithubPullRequest {
  id: string;
  number: number;
  url: string;
  state: "OPEN" | "CLOSED" | "MERGED";
  isDraft: boolean;
  title: string;
  body: string;
  baseRef: string;
  baseOid: string;
  baseSha: string;
  headRef: string;
  headOid: string;
  headSha: string;
  headRepositoryId: string;
  autoMergeEnabled: boolean;
  mergeCommitSha?: string;
  mergedByActorId?: string;
}

export interface GithubCheck {
  id: string;
  name: string;
  status: string;
  conclusion: string | null;
}

export interface GithubRepositoryContext {
  repositoryId: string;
  nameWithOwner: string;
  owner: GithubActor;
  viewer: GithubActor;
  viewerPermission: "WRITE" | "MAINTAIN" | "ADMIN";
  deliveryActor: GithubActor;
  deliveryPermission: "WRITE" | "MAINTAIN" | "ADMIN";
  trustedActor: GithubActor;
  baseRef: string;
  baseSha: string;
  deleteBranchOnMerge: boolean;
}

export interface GithubMergeSnapshot {
  repository: GithubRepositoryContext;
  issueId: string;
  issueNumber: number;
  issueState: "OPEN" | "CLOSED";
  issueComments: GithubIssueComment[];
  commentsComplete: true;
  pullRequest: GithubPullRequest;
  checks: GithubCheck[];
  checksComplete: true;
  checksPass: boolean;
  commentsFingerprint: string;
  repositoryId: string;
  comments: GithubIssueComment[];
  requiredChecksPass: boolean;
  checksFingerprint: string;
}

export interface DraftPullRequestInput {
  issueNumber: number;
  headRef: string;
  baseRef: string;
  title: string;
  body: string;
}

export interface GithubHostConfig {
  ghExecutable: string;
  osHome: string;
  repository: {
    host: "github.com";
    nameWithOwner: string;
    nodeId: string;
    owner: { id: string; login: string };
    baseRef: string;
  };
  deliveryActor: { id: string; login: string };
  trustedActor: { id: string; login: string };
  maxStatusCommentUtf8Bytes: number;
}

export type GhProcessRunner = (
  file: string,
  args: string[],
  options: {
    encoding: "utf8";
    stdio: ["ignore", "pipe", "pipe"];
    env: Record<string, string>;
  },
) => string;

export interface HostGithub {
  validateContext(): Promise<GithubRepositoryContext>;
  getIssue(number: number): Promise<GithubIssue>;
  getLabeledIssues(label: string): Promise<GithubIssue[]>;
  findPullRequests(headRef: string): Promise<GithubPullRequest[]>;
  createDraftPullRequest(input: {
    issueNumber: number;
    headRef: string;
    headSha: string;
    baseRef: string;
    title: string;
    body: string;
  }): Promise<GithubPullRequest>;
  reconcileDraftPullRequest(
    pullRequest: GithubPullRequest,
    input: { headRef: string; headSha: string; baseRef: string },
  ): Promise<GithubPullRequest>;
  ensureExactlyOneDraftPullRequest(
    input: DraftPullRequestInput,
  ): Promise<GithubPullRequest>;
  upsertStatusComment(
    issueNumber: number,
    body: string,
  ): Promise<GithubIssueComment>;
  readMergeSnapshot(
    issueNumber: number,
    pullRequestNumber: number,
  ): Promise<GithubMergeSnapshot>;
  markPullRequestReady(pullRequestId: string): Promise<void>;
  mergePullRequest(
    pullRequestId: string,
    expectedHeadOid: string,
    method: "MERGE" | "SQUASH" | "REBASE",
  ): Promise<{ merged: boolean; mergeCommitOid?: string }>;
}

interface HostGithubOptions {
  runProcess?: GhProcessRunner;
  sourceEnvironment?: Record<string, string | undefined>;
}

const STATUS_MARKER = "<!-- sandcastle-status:v1 -->";
const DISCLOSURE = "AI-generated and authorized by a human";

const CONTEXT_QUERY = `query($owner:String!,$name:String!,$trustedLogin:String!,$baseQualified:String!){
  viewer{id login __typename}
  user(login:$trustedLogin){id login __typename}
  repository(owner:$owner,name:$name){id nameWithOwner viewerPermission deleteBranchOnMerge owner{id login __typename} ref(qualifiedName:$baseQualified){target{oid}}}
}`;

const ISSUE_QUERY = `query($owner:String!,$name:String!,$number:Int!,$cursor:String){
  repository(owner:$owner,name:$name){id issue(number:$number){
    id number title body url state createdAt updatedAt author{id login __typename}
    labels(first:100){nodes{name}}
    comments(first:100,after:$cursor){pageInfo{hasNextPage endCursor}nodes{id body createdAt updatedAt url author{id login __typename}}}
  }}
}`;

const LABELED_ISSUES_QUERY = `query($owner:String!,$name:String!,$label:String!,$cursor:String){
  repository(owner:$owner,name:$name){id issues(first:100,after:$cursor,states:OPEN,labels:[$label],orderBy:{field:CREATED_AT,direction:ASC}){pageInfo{hasNextPage endCursor}nodes{number}}}
}`;

const PULL_REQUESTS_QUERY = `query($owner:String!,$name:String!,$headRef:String!,$cursor:String){
  repository(owner:$owner,name:$name){id pullRequests(first:100,after:$cursor,states:[OPEN,CLOSED,MERGED],headRefName:$headRef){
    pageInfo{hasNextPage endCursor}
    nodes{id number url state isDraft title body baseRefName baseRefOid headRefName headRefOid headRepository{id} autoMergeRequest{enabledAt} mergeCommit{oid} mergedBy{id}}
  }}
}`;

const CREATE_PR_MUTATION = `mutation($repositoryId:ID!,$baseRef:String!,$headRef:String!,$title:String!,$body:String!){
  createPullRequest(input:{repositoryId:$repositoryId,baseRefName:$baseRef,headRefName:$headRef,title:$title,body:$body,draft:true}){
    pullRequest{id number url state isDraft title body baseRefName baseRefOid headRefName headRefOid headRepository{id} autoMergeRequest{enabledAt} mergeCommit{oid} mergedBy{id}}
  }
}`;

const UPDATE_PR_MUTATION = `mutation($id:ID!,$baseRef:String!,$title:String!,$body:String!){
  updatePullRequest(input:{pullRequestId:$id,state:OPEN,baseRefName:$baseRef,title:$title,body:$body}){
    pullRequest{id number url state isDraft title body baseRefName baseRefOid headRefName headRefOid headRepository{id} autoMergeRequest{enabledAt} mergeCommit{oid} mergedBy{id}}
  }
}`;

const DRAFT_PR_MUTATION = `mutation($id:ID!){convertPullRequestToDraft(input:{pullRequestId:$id}){pullRequest{id}}}`;
const READY_PR_MUTATION = `mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){pullRequest{id}}}`;
const ADD_COMMENT_MUTATION = `mutation($subjectId:ID!,$body:String!){addComment(input:{subjectId:$subjectId,body:$body}){commentEdge{node{id body createdAt updatedAt url author{id login __typename}}}}}`;
const UPDATE_COMMENT_MUTATION = `mutation($id:ID!,$body:String!){updateIssueComment(input:{id:$id,body:$body}){issueComment{id body createdAt updatedAt url author{id login __typename}}}}`;
const MERGE_MUTATION = `mutation($id:ID!,$expectedHeadOid:GitObjectID!,$method:PullRequestMergeMethod!,$headline:String!,$body:String!){
  mergePullRequest(input:{pullRequestId:$id,expectedHeadOid:$expectedHeadOid,mergeMethod:$method,commitHeadline:$headline,commitBody:$body}){
    pullRequest{merged state mergeCommit{oid}}
  }
}`;

const MERGE_SNAPSHOT_QUERY = `query($owner:String!,$name:String!,$issueNumber:Int!,$pullRequestNumber:Int!,$issueCursor:String,$prCommentCursor:String,$reviewCursor:String,$checkCursor:String){
  repository(owner:$owner,name:$name){id
    issue(number:$issueNumber){id number state comments(first:100,after:$issueCursor){pageInfo{hasNextPage endCursor}nodes{id body createdAt updatedAt url author{id login __typename}}}}
    pullRequest(number:$pullRequestNumber){
      id number url state isDraft title body baseRefName baseRefOid headRefName headRefOid headRepository{id} autoMergeRequest{enabledAt} mergeCommit{oid} mergedBy{id}
      comments(first:100,after:$prCommentCursor){pageInfo{hasNextPage endCursor}nodes{id body createdAt updatedAt url author{id login __typename}}}
      reviews(first:100,after:$reviewCursor){pageInfo{hasNextPage endCursor}nodes{id body state submittedAt updatedAt author{id login __typename} comments(first:100){pageInfo{hasNextPage endCursor}nodes{id body createdAt updatedAt url author{id login __typename}}}}}
      commits(last:1){nodes{commit{oid statusCheckRollup{contexts(first:100,after:$checkCursor){pageInfo{hasNextPage endCursor}nodes{
        __typename ... on CheckRun{id name status conclusion} ... on StatusContext{id context state}
      }}}}}}
    }
  }
}`;

const REVIEW_COMMENTS_QUERY = `query($owner:String!,$name:String!,$reviewId:ID!,$cursor:String){repository(owner:$owner,name:$name){id}node(id:$reviewId){... on PullRequestReview{comments(first:100,after:$cursor){pageInfo{hasNextPage endCursor}nodes{id body createdAt updatedAt url author{id login __typename}}}}}}`;

export function machineLocalGh(
  executable: string,
  args: string[],
  osHome: string,
  runProcess: GhProcessRunner = (file, processArgs, options) =>
    execFileSync(file, processArgs, options),
  sourceEnvironment: Record<string, string | undefined> = process.env,
): string {
  if (!isAbsolute(executable)) {
    throw new Error("The configured machine-local gh executable must be absolute.");
  }
  const resolved = realpathSync(executable);
  const executableStat = statSync(resolved);
  if (!isAbsolute(resolved) || !executableStat.isFile() || (executableStat.mode & 0o111) === 0) {
    throw new Error("The configured machine-local gh executable is not an executable absolute regular file.");
  }
  return runProcess(resolved, args, {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    env: sanitizeHostEnvironment(sourceEnvironment, osHome),
  });
}

export function createHostGithub(
  config: GithubHostConfig,
  options: HostGithubOptions = {},
): HostGithub {
  validateConfiguration(config);
  const [owner, name] = config.repository.nameWithOwner.split("/") as [string, string];
  const run = (args: string[]) => machineLocalGh(
    config.ghExecutable,
    args,
    config.osHome,
    options.runProcess,
    options.sourceEnvironment,
  );

  const graphql = (query: string, variables: Record<string, string | number | undefined>): any => {
    const args = ["api", "graphql", "--hostname", config.repository.host];
    for (const [key, value] of Object.entries({ owner, name, ...variables })) {
      if (value === undefined) continue;
      args.push(typeof value === "number" ? "-F" : "-f", `${key}=${value}`);
    }
    args.push("-f", `query=${query}`);
    const parsed = JSON.parse(run(args));
    if (parsed.errors?.length) {
      throw new Error(`GitHub GraphQL failed: ${parsed.errors.map((error: any) => error.message).join("; ")}`);
    }
    return parsed.data;
  };

  async function validateContext(): Promise<GithubRepositoryContext> {
    const data = graphql(CONTEXT_QUERY, {
      trustedLogin: config.trustedActor.login,
      baseQualified: `refs/heads/${config.repository.baseRef}`,
    });
    const repository = required(data.repository, "configured GitHub repository");
    const viewer = actor(data.viewer);
    const trustedActor = actor(data.user);
    const repositoryOwner = actor(repository.owner);
    assertEqual(repository.id, config.repository.nodeId, "repository node ID");
    assertEqual(repository.nameWithOwner, config.repository.nameWithOwner, "repository name");
    assertActor(repositoryOwner, config.repository.owner, "repository owner");
    assertActor(viewer, config.deliveryActor, "delivery actor");
    assertActor(trustedActor, config.trustedActor, "trusted actor");
    if (repository.viewerPermission !== "WRITE") {
      throw new Error(
        `Delivery actor must have exact WRITE permission; elevated/admin-capable permission is not accepted (${repository.viewerPermission ?? "NONE"}).`,
      );
    }
    const deliveryPermission = repository.viewerPermission as "WRITE" | "MAINTAIN" | "ADMIN";
    const baseSha = required(repository.ref?.target?.oid, `base ref ${config.repository.baseRef}`);
    assertOid(baseSha);
    return {
      repositoryId: repository.id,
      nameWithOwner: repository.nameWithOwner,
      owner: repositoryOwner,
      viewer,
      viewerPermission: deliveryPermission,
      deliveryActor: viewer,
      deliveryPermission,
      trustedActor,
      baseRef: config.repository.baseRef,
      baseSha,
      deleteBranchOnMerge: repository.deleteBranchOnMerge === true,
    };
  }

  async function getIssue(number: number): Promise<GithubIssue> {
    assertPositiveInteger(number, "issue number");
    let cursor: string | undefined;
    let value: any;
    const comments: GithubIssueComment[] = [];
    do {
      const data = graphql(ISSUE_QUERY, { number, cursor });
      assertEqual(data.repository?.id, config.repository.nodeId, "repository node ID");
      value ??= required(data.repository?.issue, `Issue #${number}`);
      const page = required(data.repository?.issue?.comments, "Issue comments");
      comments.push(...(page.nodes ?? []).map(issueComment));
      cursor = nextCursor(page.pageInfo);
    } while (cursor);
    return {
      id: value.id,
      number: value.number,
      title: value.title,
      body: value.body ?? "",
      url: value.url,
      state: issueState(value.state),
      author: actor(value.author),
      labels: (value.labels?.nodes ?? []).map((label: any) => label.name),
      comments,
      commentsComplete: true,
      createdAt: value.createdAt,
      updatedAt: value.updatedAt,
    };
  }

  async function getLabeledIssues(label: string): Promise<GithubIssue[]> {
    if (!label.trim()) throw new Error("Queue label must not be empty.");
    let cursor: string | undefined;
    const numbers: number[] = [];
    do {
      const data = graphql(LABELED_ISSUES_QUERY, { label, cursor });
      assertEqual(data.repository?.id, config.repository.nodeId, "repository node ID");
      const page = required(data.repository?.issues, "labeled issues");
      numbers.push(...(page.nodes ?? []).map((node: any) => node.number));
      cursor = nextCursor(page.pageInfo);
    } while (cursor);
    const issues: GithubIssue[] = [];
    for (const number of numbers) issues.push(await getIssue(number));
    return issues;
  }

  async function findPullRequests(headRef: string): Promise<GithubPullRequest[]> {
    assertBranch(headRef);
    let cursor: string | undefined;
    const result: GithubPullRequest[] = [];
    do {
      const data = graphql(PULL_REQUESTS_QUERY, { headRef, cursor });
      assertEqual(data.repository?.id, config.repository.nodeId, "repository node ID");
      const page = required(data.repository?.pullRequests, "pull requests");
      result.push(...(page.nodes ?? []).map(pullRequest).filter((pr: GithubPullRequest) =>
        pr.headRef === headRef && pr.headRepositoryId === config.repository.nodeId
      ));
      cursor = nextCursor(page.pageInfo);
    } while (cursor);
    return result;
  }

  async function mutate(query: string, variables: Record<string, string | number>): Promise<any> {
    await validateContext();
    return graphql(query, variables);
  }

  async function createDraftPullRequest(input: {
    issueNumber: number;
    headRef: string;
    headSha: string;
    baseRef: string;
    title: string;
    body: string;
  }): Promise<GithubPullRequest> {
    validateDraftInput(input);
    assertOid(input.headSha);
    const before = await findPullRequests(input.headRef);
    if (before.length !== 0) {
      throw new Error(`Draft creation requires zero pull requests for ${input.headRef}; found ${before.length}.`);
    }
    const context = await validateContext();
    graphql(CREATE_PR_MUTATION, {
      repositoryId: context.repositoryId,
      baseRef: input.baseRef,
      headRef: input.headRef,
      title: input.title,
      body: input.body,
    });
    const after = await findPullRequests(input.headRef);
    if (after.length !== 1 || after[0]!.state !== "OPEN" || !after[0]!.isDraft || after[0]!.headSha !== input.headSha) {
      throw new Error("Draft pull request creation did not reconcile to the exact verified head SHA.");
    }
    return after[0]!;
  }

  async function reconcileDraftPullRequest(
    requested: GithubPullRequest,
    input: { headRef: string; headSha: string; baseRef: string },
  ): Promise<GithubPullRequest> {
    assertBranch(input.headRef);
    assertBranch(input.baseRef);
    assertOid(input.headSha);
    const matches = await findPullRequests(input.headRef);
    if (matches.length !== 1 || matches[0]!.id !== requested.id) {
      throw new Error(`Draft reconciliation requires exactly one unchanged pull request for ${input.headRef}.`);
    }
    const existing = matches[0]!;
    if (existing.state === "MERGED") throw new Error("A merged pull request cannot be reconciled as a draft.");
    await mutate(UPDATE_PR_MUTATION, {
      id: existing.id,
      baseRef: input.baseRef,
      title: existing.title,
      body: existing.body,
    });
    if (!existing.isDraft) await mutate(DRAFT_PR_MUTATION, { id: existing.id });
    const after = await findPullRequests(input.headRef);
    if (
      after.length !== 1 || after[0]!.id !== existing.id || after[0]!.state !== "OPEN" ||
      !after[0]!.isDraft || after[0]!.headSha !== input.headSha || after[0]!.baseRef !== input.baseRef
    ) {
      throw new Error("Draft pull request reconciliation did not converge to the exact verified branch state.");
    }
    return after[0]!;
  }

  async function ensureExactlyOneDraftPullRequest(
    input: DraftPullRequestInput,
  ): Promise<GithubPullRequest> {
    validateDraftInput(input);
    const existing = await findPullRequests(input.headRef);
    if (existing.length > 1) {
      throw new Error(`Expected at most one pull request for ${input.headRef}; found ${existing.length}.`);
    }
    if (existing[0]?.state === "MERGED") {
      throw new Error(`Pull request #${existing[0].number} is already merged; refusing to reuse the branch.`);
    }
    if (existing[0]?.autoMergeEnabled) {
      throw new Error(`Pull request #${existing[0].number} has auto-merge enabled; Sandcastle will not adopt it.`);
    }
    if (!existing[0]) {
      const context = await validateContext();
      graphql(CREATE_PR_MUTATION, {
        repositoryId: context.repositoryId,
        baseRef: input.baseRef,
        headRef: input.headRef,
        title: input.title,
        body: input.body,
      });
    } else {
      await mutate(UPDATE_PR_MUTATION, {
        id: existing[0].id,
        baseRef: input.baseRef,
        title: input.title,
        body: input.body,
      });
      if (!existing[0].isDraft) {
        await mutate(DRAFT_PR_MUTATION, { id: existing[0].id });
      }
    }
    const reconciled = await findPullRequests(input.headRef);
    if (reconciled.length !== 1 || reconciled[0]!.state !== "OPEN" || !reconciled[0]!.isDraft) {
      throw new Error(`Draft pull request reconciliation for ${input.headRef} did not converge to exactly one open draft.`);
    }
    const result = reconciled[0]!;
    if (result.baseRef !== input.baseRef || result.title !== input.title || result.body !== input.body) {
      throw new Error("Draft pull request metadata did not match the requested harness state.");
    }
    return result;
  }

  async function upsertStatusComment(issueNumber: number, body: string): Promise<GithubIssueComment> {
    validateStatusBody(body, config.maxStatusCommentUtf8Bytes);
    const before = await getIssue(issueNumber);
    if (before.state !== "OPEN") {
      throw new Error(`Issue #${issueNumber} closed before status reconciliation.`);
    }
    const marked = before.comments.filter((comment) => comment.body.includes(STATUS_MARKER));
    if (marked.some((comment) => comment.author.id !== config.deliveryActor.id)) {
      throw new Error("A Sandcastle status marker exists under an unexpected immutable author ID.");
    }
    if (marked.length > 1) {
      throw new Error("Multiple Sandcastle status comments exist; refusing an ambiguous update.");
    }
    if (marked[0]) {
      await mutate(UPDATE_COMMENT_MUTATION, { id: marked[0].id, body });
    } else {
      await mutate(ADD_COMMENT_MUTATION, { subjectId: before.id, body });
    }
    const after = await getIssue(issueNumber);
    const reconciled = after.comments.filter((comment) => comment.body.includes(STATUS_MARKER));
    if (reconciled.length !== 1 || reconciled[0]!.author.id !== config.deliveryActor.id || reconciled[0]!.body !== body) {
      throw new Error("Sandcastle status comment reconciliation did not converge exactly.");
    }
    return reconciled[0]!;
  }

  async function readMergeSnapshot(issueNumber: number, pullRequestNumber: number): Promise<GithubMergeSnapshot> {
    const repository = await validateContext();
    const issue = await getIssue(issueNumber);
    const collected = await collectPullRequestSnapshot(issueNumber, pullRequestNumber);
    const records = [
      ...issue.comments.map((comment) => fingerprintComment("issue", comment)),
      ...collected.prComments.map((comment) => fingerprintComment("pr", comment)),
      ...collected.reviews.map((review) => ({
        kind: "review",
        id: review.id,
        authorId: actor(review.author).id,
        body: review.body ?? "",
        state: review.state,
        submittedAt: review.submittedAt ?? "",
        updatedAt: review.updatedAt ?? "",
      })),
      ...collected.reviewComments.map((comment) => fingerprintComment("review-comment", comment)),
    ].sort(compareStable);
    const checks = collected.checks.sort((left, right) => `${left.id}\0${left.name}`.localeCompare(`${right.id}\0${right.name}`));
    const checksFingerprint = createHash("sha256").update(JSON.stringify(checks)).digest("hex");
    return {
      repository,
      issueId: issue.id,
      issueNumber,
      issueState: issue.state,
      issueComments: issue.comments,
      commentsComplete: true,
      pullRequest: collected.pullRequest,
      checks,
      checksComplete: true,
      checksPass: checks.length > 0 && checks.every(checkPasses),
      commentsFingerprint: createHash("sha256").update(JSON.stringify(records)).digest("hex"),
      repositoryId: repository.repositoryId,
      comments: issue.comments,
      requiredChecksPass: checks.length > 0 && checks.every(checkPasses),
      checksFingerprint,
    };
  }

  async function collectPullRequestSnapshot(issueNumber: number, pullRequestNumber: number) {
    assertPositiveInteger(issueNumber, "issue number");
    assertPositiveInteger(pullRequestNumber, "pull request number");
    let issueCursor: string | undefined;
    let prCommentCursor: string | undefined;
    let reviewCursor: string | undefined;
    let checkCursor: string | undefined;
    let prValue: any;
    const prComments: GithubIssueComment[] = [];
    const reviews: any[] = [];
    const reviewComments: GithubIssueComment[] = [];
    const checks: GithubCheck[] = [];
    let issueDone = false;
    let prCommentsDone = false;
    let reviewsDone = false;
    let checksDone = false;
    while (!issueDone || !prCommentsDone || !reviewsDone || !checksDone) {
      const data = graphql(MERGE_SNAPSHOT_QUERY, {
        issueNumber,
        pullRequestNumber,
        issueCursor,
        prCommentCursor,
        reviewCursor,
        checkCursor,
      });
      assertEqual(data.repository?.id, config.repository.nodeId, "repository node ID");
      const repository = required(data.repository, "repository");
      required(repository.issue, `Issue #${issueNumber}`);
      const current = required(repository.pullRequest, `Pull request #${pullRequestNumber}`);
      prValue ??= current;
      if (!issueDone) {
        issueCursor = nextCursor(repository.issue.comments?.pageInfo);
        issueDone = !issueCursor;
      }
      if (!prCommentsDone) {
        prComments.push(...(current.comments?.nodes ?? []).map(issueComment));
        prCommentCursor = nextCursor(current.comments?.pageInfo);
        prCommentsDone = !prCommentCursor;
      }
      if (!reviewsDone) {
        for (const review of current.reviews?.nodes ?? []) {
          reviews.push(review);
          reviewComments.push(...(review.comments?.nodes ?? []).map(issueComment));
          let cursor = nextCursor(review.comments?.pageInfo);
          while (cursor) {
            const reviewData = graphql(REVIEW_COMMENTS_QUERY, { reviewId: review.id, cursor });
            assertEqual(reviewData.repository?.id, config.repository.nodeId, "repository node ID");
            const page = required(reviewData.node?.comments, "review comments");
            reviewComments.push(...(page.nodes ?? []).map(issueComment));
            cursor = nextCursor(page.pageInfo);
          }
        }
        reviewCursor = nextCursor(current.reviews?.pageInfo);
        reviewsDone = !reviewCursor;
      }
      if (!checksDone) {
        const contexts = current.commits?.nodes?.[0]?.commit?.statusCheckRollup?.contexts;
        if (contexts) {
          checks.push(...(contexts.nodes ?? []).map(githubCheck));
          checkCursor = nextCursor(contexts.pageInfo);
        } else {
          checkCursor = undefined;
        }
        checksDone = !checkCursor;
      }
    }
    return { pullRequest: pullRequest(prValue), prComments, reviews, reviewComments, checks };
  }

  async function markPullRequestReady(pullRequestId: string): Promise<void> {
    assertNodeId(pullRequestId, "pull request ID");
    await mutate(READY_PR_MUTATION, { id: pullRequestId });
  }

  async function mergePullRequest(
    pullRequestId: string,
    expectedHeadOid: string,
    method: "MERGE" | "SQUASH" | "REBASE",
  ): Promise<{ merged: boolean; mergeCommitOid?: string }> {
    assertNodeId(pullRequestId, "pull request ID");
    assertOid(expectedHeadOid);
    if (!["MERGE", "SQUASH", "REBASE"].includes(method)) throw new Error("Unsupported merge method.");
    const context = await validateContext();
    if (context.deleteBranchOnMerge) {
      throw new Error("Repository deleteBranchOnMerge must be disabled before Sandcastle merge is allowed.");
    }
    const data = graphql(MERGE_MUTATION, {
      id: pullRequestId,
      expectedHeadOid,
      method,
      headline: "Sandcastle reviewed changes",
      body: "Human-authorized AI work; Issue remains open for explicit human lifecycle management.",
    });
    const value = required(data.mergePullRequest?.pullRequest, "merge result");
    return { merged: value.merged === true, ...(value.mergeCommit?.oid ? { mergeCommitOid: value.mergeCommit.oid } : {}) };
  }

  return {
    validateContext,
    getIssue,
    getLabeledIssues,
    findPullRequests,
    createDraftPullRequest,
    reconcileDraftPullRequest,
    ensureExactlyOneDraftPullRequest,
    upsertStatusComment,
    readMergeSnapshot,
    markPullRequestReady,
    mergePullRequest,
  };
}

function validateConfiguration(config: GithubHostConfig): void {
  if (config.repository.host !== "github.com") throw new Error("Only the explicit github.com host is supported.");
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(config.repository.nameWithOwner)) throw new Error("Invalid configured repository name.");
  assertBranch(config.repository.baseRef);
  for (const [value, label] of [
    [config.repository.nodeId, "repository node ID"],
    [config.repository.owner.id, "owner node ID"],
    [config.deliveryActor.id, "delivery actor node ID"],
    [config.trustedActor.id, "trusted actor node ID"],
  ] as const) assertNodeId(value, label);
  if (!Number.isSafeInteger(config.maxStatusCommentUtf8Bytes) || config.maxStatusCommentUtf8Bytes < 256) {
    throw new Error("Status comment byte bound must be an integer of at least 256.");
  }
}

function validateDraftInput(input: DraftPullRequestInput): void {
  assertPositiveInteger(input.issueNumber, "issue number");
  assertBranch(input.headRef);
  assertBranch(input.baseRef);
  if (!input.title.trim() || !input.body.trim()) throw new Error("Draft pull request title and body are required.");
}

function validateStatusBody(body: string, maximum: number): void {
  if (body.split(STATUS_MARKER).length !== 2 || !body.includes(DISCLOSURE)) {
    throw new Error(`Status body must contain exactly one ${STATUS_MARKER} and the AI authorization disclosure.`);
  }
  if (body.includes("\0") || /(^|\s)@[A-Za-z0-9-]+/.test(body)) {
    throw new Error("Status body must not contain NUL bytes or user mentions.");
  }
  if (Buffer.byteLength(body, "utf8") > maximum) {
    throw new Error(`Status body exceeds the ${maximum}-byte UTF-8 limit.`);
  }
}

function actor(value: any): GithubActor {
  const requiredActor = required(value, "GitHub actor");
  assertNodeId(requiredActor.id, "actor node ID");
  if (!requiredActor.login || !requiredActor.__typename) throw new Error("GitHub actor lacks immutable identity fields.");
  return { id: requiredActor.id, login: requiredActor.login, type: requiredActor.__typename };
}

function issueComment(value: any): GithubIssueComment {
  return {
    id: required(value?.id, "comment node ID"),
    author: actor(value?.author),
    body: value?.body ?? "",
    createdAt: required(value?.createdAt, "comment createdAt"),
    updatedAt: required(value?.updatedAt, "comment updatedAt"),
    url: required(value?.url, "comment URL"),
  };
}

function pullRequest(value: any): GithubPullRequest {
  const requiredPr = required(value, "pull request");
  const state = requiredPr.state;
  if (!["OPEN", "CLOSED", "MERGED"].includes(state)) throw new Error(`Unknown pull request state: ${state}`);
  return {
    id: requiredPr.id,
    number: requiredPr.number,
    url: requiredPr.url,
    state,
    isDraft: requiredPr.isDraft === true,
    title: requiredPr.title,
    body: requiredPr.body ?? "",
    baseRef: requiredPr.baseRefName,
    baseOid: requiredPr.baseRefOid,
    baseSha: requiredPr.baseRefOid,
    headRef: requiredPr.headRefName,
    headOid: requiredPr.headRefOid,
    headSha: requiredPr.headRefOid,
    headRepositoryId: required(requiredPr.headRepository?.id, "head repository ID"),
    autoMergeEnabled: requiredPr.autoMergeRequest != null,
    ...(requiredPr.mergeCommit?.oid
      ? { mergeCommitSha: requiredPr.mergeCommit.oid }
      : {}),
    ...(requiredPr.mergedBy?.id
      ? { mergedByActorId: requiredPr.mergedBy.id }
      : {}),
  };
}

function githubCheck(value: any): GithubCheck {
  if (value.__typename === "CheckRun") {
    return { id: value.id, name: value.name, status: value.status, conclusion: value.conclusion ?? null };
  }
  if (value.__typename === "StatusContext") {
    const pass = ["SUCCESS", "NEUTRAL"].includes(value.state);
    return { id: value.id, name: value.context, status: pass || ["FAILURE", "ERROR"].includes(value.state) ? "COMPLETED" : "PENDING", conclusion: pass ? value.state : value.state === "PENDING" ? null : value.state };
  }
  throw new Error(`Unknown check type: ${value.__typename}`);
}

function checkPasses(check: GithubCheck): boolean {
  return check.status === "COMPLETED" && ["SUCCESS", "NEUTRAL", "SKIPPED"].includes(check.conclusion ?? "");
}

function fingerprintComment(kind: string, comment: GithubIssueComment) {
  return { kind, id: comment.id, authorId: comment.author.id, body: comment.body, createdAt: comment.createdAt, updatedAt: comment.updatedAt };
}

function compareStable(left: any, right: any): number {
  return `${left.kind}\0${left.id}`.localeCompare(`${right.kind}\0${right.id}`);
}

function nextCursor(pageInfo: any): string | undefined {
  if (!pageInfo?.hasNextPage) return undefined;
  if (!pageInfo.endCursor) throw new Error("GitHub pagination claimed another page without an end cursor.");
  return pageInfo.endCursor;
}

function issueState(value: string): "OPEN" | "CLOSED" {
  if (value !== "OPEN" && value !== "CLOSED") throw new Error(`Unknown issue state: ${value}`);
  return value;
}

function required<T>(value: T | null | undefined, label: string): T {
  if (value === null || value === undefined) throw new Error(`GitHub did not return ${label}.`);
  return value;
}

function assertEqual(actual: unknown, expected: unknown, label: string): void {
  if (actual !== expected) throw new Error(`Configured ${label} mismatch.`);
}

function assertActor(actual: GithubActor, expected: { id: string; login: string }, label: string): void {
  if (actual.id !== expected.id || actual.login !== expected.login) throw new Error(`Configured ${label} immutable identity mismatch.`);
}

function assertPositiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 1) throw new Error(`Invalid ${label}.`);
}

function assertNodeId(value: string, label: string): void {
  if (!value || /\s/.test(value)) throw new Error(`Invalid ${label}.`);
}

function assertOid(value: string): void {
  if (!/^[0-9a-f]{40,64}$/.test(value)) throw new Error("Expected an exact lowercase Git object ID.");
}

function assertBranch(value: string): void {
  if (!value || value.startsWith("-") || value.includes("..") || /[~^:?*[\\\s]/.test(value)) throw new Error("Invalid Git branch name.");
}
