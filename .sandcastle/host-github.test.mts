import assert from "node:assert/strict";
import test from "node:test";

import {
  createHostGithub,
  machineLocalGh,
  type GithubHostConfig,
  type GhProcessRunner,
} from "./host-github.mts";

test("machineLocalGh rejects an ambient relative executable before process launch", () => {
  let launched = false;
  assert.throws(
    () => machineLocalGh("gh", ["auth", "status"], "/controlled/home", () => {
      launched = true;
      return "";
    }),
    /must be absolute/,
  );
  assert.equal(launched, false);
});

const SHA = "1".repeat(40);
const BASE_SHA = "2".repeat(40);
const OWNER = { id: "OWNER_ID", login: "weib10", __typename: "User" };
const VIEWER = { id: "VIEWER_ID", login: "brant92good", __typename: "User" };

const CONFIG: GithubHostConfig = {
  ghExecutable: process.execPath,
  osHome: "/controlled/home",
  repository: {
    host: "github.com",
    nameWithOwner: "weib10/lol-performance-overlay",
    nodeId: "REPO_ID",
    owner: { id: OWNER.id, login: OWNER.login },
    baseRef: "main",
  },
  deliveryActor: { id: VIEWER.id, login: VIEWER.login },
  trustedActor: { id: OWNER.id, login: OWNER.login },
  maxStatusCommentUtf8Bytes: 1200,
};

test("all host calls use an absolute gh executable, explicit github.com and explicit repository variables", async () => {
  const fake = new FakeGithub();
  const github = createHostGithub(CONFIG, fake.options());

  await github.validateContext();
  await github.getIssue(4);
  await github.getLabeledIssues("Sandcastle");

  assert.ok(fake.calls.length >= 2);
  for (const call of fake.calls) {
    assert.equal(call.file, process.execPath);
    assert.deepEqual(call.args.slice(0, 4), ["api", "graphql", "--hostname", "github.com"]);
    assert.equal(variable(call.args, "owner"), "weib10");
    assert.equal(variable(call.args, "name"), "lol-performance-overlay");
    assert.equal(call.environment.HOME, "/controlled/home");
    assert.equal(call.environment.GH_TOKEN, undefined);
  }
});

test("Issue comments are fully paginated and retain immutable author and comment IDs", async () => {
  const fake = new FakeGithub();
  fake.paginateIssueComments = true;
  const issue = await createHostGithub(CONFIG, fake.options()).getIssue(4);

  assert.equal(issue.commentsComplete, true);
  assert.deepEqual(issue.comments.map((comment) => [comment.id, comment.author.id]), [
    ["COMMENT_1", OWNER.id],
    ["COMMENT_2", OWNER.id],
  ]);
  assert.equal(fake.calls.filter((call) => query(call.args).includes("title body url state")).length, 2);
});

test("immutable identity mismatch fails before a status mutation", async () => {
  const fake = new FakeGithub();
  fake.repositoryIdDuringValidation = "WRONG_REPOSITORY";
  const github = createHostGithub(CONFIG, fake.options());

  await assert.rejects(
    github.upsertStatusComment(4, statusBody("running")),
    /repository node ID mismatch/,
  );
  assert.equal(fake.mutations, 0);
});

test("draft PR reconciliation creates exactly one draft and fails closed on duplicates", async () => {
  const fake = new FakeGithub();
  const github = createHostGithub(CONFIG, fake.options());
  const input = {
    issueNumber: 4,
    headRef: "sandcastle/issue-4",
    baseRef: "main",
    title: "[Sandcastle] Issue #4",
    body: "Harness-owned draft body",
  };

  const created = await github.ensureExactlyOneDraftPullRequest(input);
  assert.equal(created.isDraft, true);
  assert.equal(created.state, "OPEN");
  assert.equal(fake.prs.length, 1);
  assert.equal(fake.mutations, 1);

  fake.prs.push({ ...fake.prs[0]!, id: "PR_DUPLICATE", number: 10 });
  const before = fake.mutations;
  await assert.rejects(github.ensureExactlyOneDraftPullRequest(input), /found 2/);
  assert.equal(fake.mutations, before);
});

test("status upsert is byte-bounded, author-bound and exactly one", async () => {
  const fake = new FakeGithub();
  const github = createHostGithub(CONFIG, fake.options());

  await assert.rejects(github.upsertStatusComment(4, "no marker"), /must contain/);
  await assert.rejects(
    github.upsertStatusComment(4, statusBody("界".repeat(500))),
    /1200-byte/,
  );
  assert.equal(fake.mutations, 0);

  const comment = await github.upsertStatusComment(4, statusBody("candidate verified"));
  assert.equal(comment.author.id, VIEWER.id);
  assert.equal(fake.issueComments.filter((value) => value.body.includes("sandcastle-status")).length, 1);

  fake.issueComments.push({ ...fake.issueComments.at(-1)!, id: "STATUS_DUPLICATE" });
  const before = fake.mutations;
  await assert.rejects(github.upsertStatusComment(4, statusBody("retry")), /Multiple/);
  assert.equal(fake.mutations, before);
});

test("merge snapshot binds Issue, PR and review conversation plus checks; merge uses expectedHeadOid CAS", async () => {
  const fake = new FakeGithub();
  fake.prs = [fake.pullRequest()];
  const github = createHostGithub(CONFIG, fake.options());

  const first = await github.readMergeSnapshot(4, 9);
  assert.equal(first.pullRequest.headOid, SHA);
  assert.equal(first.checksPass, true);
  assert.equal(first.commentsComplete, true);
  assert.match(first.commentsFingerprint, /^[0-9a-f]{64}$/);

  fake.prConversationBody = "human changed this after approval";
  const second = await github.readMergeSnapshot(4, 9);
  assert.notEqual(second.commentsFingerprint, first.commentsFingerprint);

  const result = await github.mergePullRequest("PR_ID", SHA, "SQUASH");
  assert.equal(result.merged, true);
  const mergeCall = fake.calls.find((call) => query(call.args).includes("mergePullRequest(input"))!;
  assert.equal(variable(mergeCall.args, "expectedHeadOid"), SHA);
  assert.equal(variable(mergeCall.args, "method"), "SQUASH");
  assert.equal(variable(mergeCall.args, "headline"), "Sandcastle reviewed changes");
  assert.doesNotMatch(variable(mergeCall.args, "body") ?? "", /(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#/i);
  assert.doesNotMatch(query(mergeCall.args), /admin|autoMerge|delete|closeIssue/i);
});

class FakeGithub {
  calls: Array<{ file: string; args: string[]; environment: Record<string, string> }> = [];
  mutations = 0;
  repositoryIdDuringValidation = CONFIG.repository.nodeId;
  paginateIssueComments = false;
  prConversationBody = "PR conversation";
  issueComments: any[] = [this.comment("COMMENT_1", OWNER, "Initial issue comment")];
  prs: any[] = [];

  options() {
    const runProcess: GhProcessRunner = (file, args, options) => {
      this.calls.push({ file, args, environment: options.env });
      return JSON.stringify({ data: this.respond(query(args), variables(args)) });
    };
    return {
      runProcess,
      sourceEnvironment: {
        PATH: "/usr/bin",
        [["GH", "TOKEN"].join("_")]: "must-not-leak",
      },
    };
  }

  respond(document: string, vars: Record<string, string>): any {
    if (document.includes("viewer{id login")) {
      return {
        viewer: VIEWER,
        user: OWNER,
        repository: {
          id: this.repositoryIdDuringValidation,
          nameWithOwner: CONFIG.repository.nameWithOwner,
          viewerPermission: "WRITE",
          deleteBranchOnMerge: false,
          owner: OWNER,
          ref: { target: { oid: BASE_SHA } },
        },
      };
    }
    if (document.includes("issue(number:$number)") && document.includes("title body url state")) {
      const secondPage = vars.cursor === "ISSUE_CURSOR";
      const nodes = this.paginateIssueComments
        ? secondPage
          ? [this.comment("COMMENT_2", OWNER, "Revision")]
          : [this.issueComments[0]!]
        : this.issueComments;
      return {
        repository: {
          id: CONFIG.repository.nodeId,
          issue: {
            id: "ISSUE_ID",
            number: 4,
            title: "Safe qualification",
            body: "No raw credentials",
            url: "https://github.com/weib10/lol-performance-overlay/issues/4",
            state: "OPEN",
            createdAt: "2026-08-01T00:00:00Z",
            updatedAt: "2026-08-02T00:00:00Z",
            author: OWNER,
            labels: { nodes: [{ name: "Sandcastle" }] },
            comments: {
              pageInfo: {
                hasNextPage: this.paginateIssueComments && !secondPage,
                endCursor: this.paginateIssueComments && !secondPage ? "ISSUE_CURSOR" : null,
              },
              nodes,
            },
          },
        },
      };
    }
    if (document.includes("pullRequests(first:100")) {
      return {
        repository: {
          id: CONFIG.repository.nodeId,
          pullRequests: {
            pageInfo: { hasNextPage: false, endCursor: null },
            nodes: this.prs,
          },
        },
      };
    }
    if (document.includes("issues(first:100")) {
      return {
        repository: {
          id: CONFIG.repository.nodeId,
          issues: {
            pageInfo: { hasNextPage: false, endCursor: null },
            nodes: [{ number: 4 }],
          },
        },
      };
    }
    if (document.includes("createPullRequest(input")) {
      this.mutations += 1;
      const pr = this.pullRequest({
        title: vars.title,
        body: vars.body,
        baseRefName: vars.baseRef,
        headRefName: vars.headRef,
      });
      this.prs = [pr];
      return { createPullRequest: { pullRequest: pr } };
    }
    if (document.includes("updatePullRequest(input")) {
      this.mutations += 1;
      this.prs[0] = {
        ...this.prs[0],
        state: "OPEN",
        baseRefName: vars.baseRef,
        title: vars.title,
        body: vars.body,
      };
      return { updatePullRequest: { pullRequest: this.prs[0] } };
    }
    if (document.includes("convertPullRequestToDraft")) {
      this.mutations += 1;
      this.prs[0].isDraft = true;
      return { convertPullRequestToDraft: { pullRequest: { id: this.prs[0].id } } };
    }
    if (document.includes("addComment(input")) {
      this.mutations += 1;
      const comment = this.comment("STATUS_ID", VIEWER, vars.body);
      this.issueComments.push(comment);
      return { addComment: { commentEdge: { node: comment } } };
    }
    if (document.includes("updateIssueComment(input")) {
      this.mutations += 1;
      const target = this.issueComments.find((comment) => comment.id === vars.id)!;
      target.body = vars.body;
      target.updatedAt = "2026-08-03T00:00:00Z";
      return { updateIssueComment: { issueComment: target } };
    }
    if (document.includes("issue(number:$issueNumber)")) {
      return this.mergeSnapshotResponse();
    }
    if (document.includes("markPullRequestReadyForReview")) {
      this.mutations += 1;
      return { markPullRequestReadyForReview: { pullRequest: { id: vars.id } } };
    }
    if (document.includes("mergePullRequest(input")) {
      this.mutations += 1;
      return {
        mergePullRequest: {
          pullRequest: { merged: true, state: "MERGED", mergeCommit: { oid: "3".repeat(40) } },
        },
      };
    }
    throw new Error(`Unexpected fake GraphQL document: ${document.slice(0, 80)}`);
  }

  mergeSnapshotResponse() {
    const pr = this.prs[0] ?? this.pullRequest();
    return {
      repository: {
        id: CONFIG.repository.nodeId,
        issue: {
          id: "ISSUE_ID",
          number: 4,
          state: "OPEN",
          comments: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: this.issueComments },
        },
        pullRequest: {
          ...pr,
          comments: {
            pageInfo: { hasNextPage: false, endCursor: null },
            nodes: [this.comment("PR_COMMENT", OWNER, this.prConversationBody)],
          },
          reviews: {
            pageInfo: { hasNextPage: false, endCursor: null },
            nodes: [{
              id: "REVIEW_ID",
              body: "Independent review",
              state: "APPROVED",
              submittedAt: "2026-08-02T00:00:00Z",
              updatedAt: "2026-08-02T00:00:00Z",
              author: OWNER,
              comments: {
                pageInfo: { hasNextPage: false, endCursor: null },
                nodes: [this.comment("REVIEW_COMMENT", OWNER, "Inline review")],
              },
            }],
          },
          commits: {
            nodes: [{ commit: { oid: SHA, statusCheckRollup: { contexts: {
              pageInfo: { hasNextPage: false, endCursor: null },
              nodes: [{ __typename: "CheckRun", id: "CHECK_ID", name: "package", status: "COMPLETED", conclusion: "SUCCESS" }],
            } } } }],
          },
        },
      },
    };
  }

  pullRequest(overrides: Record<string, unknown> = {}) {
    return {
      id: "PR_ID",
      number: 9,
      url: "https://github.com/weib10/lol-performance-overlay/pull/9",
      state: "OPEN",
      isDraft: true,
      title: "[Sandcastle] Issue #4",
      body: "Harness-owned draft body",
      baseRefName: "main",
      baseRefOid: BASE_SHA,
      headRefName: "sandcastle/issue-4",
      headRefOid: SHA,
      headRepository: { id: CONFIG.repository.nodeId },
      autoMergeRequest: null,
      mergeCommit: null,
      mergedBy: null,
      ...overrides,
    };
  }

  comment(id: string, author: any, body: string) {
    return {
      id,
      author,
      body,
      createdAt: "2026-08-01T00:00:00Z",
      updatedAt: "2026-08-01T00:00:00Z",
      url: `https://github.com/weib10/lol-performance-overlay/issues/4#issuecomment-${id}`,
    };
  }
}

function statusBody(message: string): string {
  return [
    "<!-- sandcastle-status:v1 -->",
    `Sandcastle: ${message}`,
    "AI-generated and authorized by a human",
  ].join("\n");
}

function query(args: string[]): string {
  return args.find((value) => value.startsWith("query="))?.slice(6) ?? "";
}

function variables(args: string[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (let index = 0; index < args.length; index += 1) {
    if (args[index] !== "-f" && args[index] !== "-F") continue;
    const assignment = args[index + 1] ?? "";
    const equals = assignment.indexOf("=");
    if (equals > 0 && !assignment.startsWith("query=")) {
      result[assignment.slice(0, equals)] = assignment.slice(equals + 1);
    }
    index += 1;
  }
  return result;
}

function variable(args: string[], name: string): string | undefined {
  return variables(args)[name];
}
