import { readFileSync, realpathSync } from "node:fs";
import { userInfo } from "node:os";
import { join } from "node:path";

import { assertOwnerControlledCheckout } from "./activation-boundary.mts";
import { decideActivation, parseWorkerCli } from "./cli.mts";
import { createAtomicStateStore } from "./durable-state.mts";
import { runGithubIssueWorker } from "./github-issue-worker.mts";
import { createHostGit } from "./host-git.mts";
import { createHostGithub } from "./host-github.mts";
import { acquireProcessLock } from "./process-lock.mts";
import { loadProjectConfig, PROJECT_CONFIG_PATH } from "./project-config.mts";
import { createSandcastleWorkspaceFactory } from "./sandcastle-issue-workspace.mts";

const HELP = `Usage:
  npm run sandcastle -- --issue N --allow-delivery
  npm run sandcastle -- --allow-delivery

Options:
  --issue N          Select one open Issue explicitly.
  --label NAME       Override the initial queue label for this run.
  --allow-delivery   Runtime key for exact-SHA push, one draft PR, and status.
  --allow-merge      Additional runtime key; also requires --allow-delivery.
  --help             Show this help.

Checked-in project.json must separately enable delivery and merge. This
repository ships with both disabled. Issue close, deploy, release, publish,
branch deletion, force/admin/auto merge are not implemented.`;

async function main(): Promise<void> {
  const rawArgs = process.argv.slice(2);
  if (rawArgs.includes("--help") || rawArgs.includes("-h")) {
    console.log(HELP);
    return;
  }

  const config = await loadProjectConfig();
  const cli = parseWorkerCli(rawArgs);
  const activation = decideActivation(config, cli);
  if (!activation.active) {
    console.log("Sandcastle GitHub Issue worker: INERT");
    console.log(activation.reason);
    console.log("No gh, agent, GitHub mutation, push, PR, or merge was started.");
    return;
  }

  const osHome = userInfo().homedir;
  const root = realpathSync(process.cwd());
  const stateDirectory = join(osHome, ".local", "state", config.stateNamespace);
  const lease = await acquireProcessLock(join(stateDirectory, "worker.lock"));

  try {
    const git = createHostGit({
      root,
      gitPath: config.tools.git,
      remote: config.repository.remote,
      expectedFetchUrl: config.repository.fetchUrl,
      expectedPushUrl: config.repository.pushUrl,
      osHome,
    });
    git.validateRepository();
    git.assertCleanWorkingTree();

    const github = createHostGithub({
      ghExecutable: config.tools.gh,
      osHome,
      repository: {
        host: config.repository.host,
        nameWithOwner: config.repository.nameWithOwner,
        nodeId: config.repository.nodeId,
        owner: {
          id: config.repository.owner.nodeId,
          login: config.repository.owner.login,
        },
        baseRef: config.repository.baseRef,
      },
      deliveryActor: {
        id: config.deliveryActor.nodeId,
        login: config.deliveryActor.login,
      },
      trustedActor: {
        id: config.trustedActor.nodeId,
        login: config.trustedActor.login,
      },
      maxStatusCommentUtf8Bytes: config.comments.maxUtf8Bytes,
    });

    // Resolve the immutable GitHub base before any state/agent action and make
    // the missing-fetch failure explicit. The worker rereads the same identity
    // as its first operation, so a concurrent base change fails closed.
    const firstContext = await github.validateContext();
    git.assertCommitExists(firstContext.baseSha);
    const ownerConfig = git.readProjectConfigAtCommit(firstContext.baseSha);
    const localConfig = readFileSync(PROJECT_CONFIG_PATH, "utf8");
    if (localConfig !== ownerConfig) {
      throw new Error(
        "Local Sandcastle project config is not byte-identical to the immutable GitHub base config.",
      );
    }
    assertOwnerControlledCheckout({
      localBranch: git.currentBranch(),
      localHeadSha: git.currentHeadSha(),
      expectedBaseRef: config.repository.baseRef,
      githubBaseSha: firstContext.baseSha,
    });
    const guardedGithub = {
      ...github,
      async validateContext() {
        const current = await github.validateContext();
        if (current.baseSha !== firstContext.baseSha) {
          throw new Error("Configured GitHub base SHA changed while the worker was starting.");
        }
        return current;
      },
    };

    const stateStore = createAtomicStateStore({
      filePath: join(stateDirectory, "state.json"),
      repoId: config.repository.nodeId,
    });
    const outcome = await runGithubIssueWorker(
      {
        issueNumber: cli.issueNumber,
        deliveryEnabled: true,
        mergeEnabled: config.merge.enabled,
        allowMerge: activation.allowMerge,
      },
      {
        github: guardedGithub,
        git,
        stateStore,
        createWorkspace: createSandcastleWorkspaceFactory(),
      },
      {
        repositoryId: config.repository.nodeId,
        repositoryNameWithOwner: config.repository.nameWithOwner,
        ownerId: config.repository.owner.nodeId,
        deliveryActorId: config.deliveryActor.nodeId,
        trustedActorId: config.trustedActor.nodeId,
        baseRef: config.repository.baseRef,
        queueLabel: cli.label ?? config.queueLabel,
        branchPrefix: config.branchPrefix,
        maxStatusBytes: config.comments.maxUtf8Bytes,
        mergeMethod: config.merge.method,
        requiredCheckNames: config.merge.requiredChecks,
      },
    );

    console.log("Sandcastle GitHub Issue worker: PASS");
    console.log(JSON.stringify(outcome));
    console.log("Issue close, deploy, release, publish, branch deletion, force-push, admin merge, and auto-merge: none");
  } finally {
    await lease.release();
  }
}

try {
  await main();
} catch (error) {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`Sandcastle GitHub Issue worker failed: ${message}`);
  process.exitCode = 1;
}
