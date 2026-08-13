import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir, userInfo } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  assertAllowedHostGitArgs,
  buildExactPushArgs,
  createHostGit,
} from "./host-git.mts";

test("exact push uses a literal SHA refspec and contains no force or delete capability", () => {
  const sha = "a".repeat(40);
  const args = buildExactPushArgs("origin", sha, "sandcastle/issue-7");
  assert.deepEqual(args, [
    "push",
    "--porcelain",
    "--no-follow-tags",
    "--recurse-submodules=no",
    "origin",
    `${sha}:refs/heads/sandcastle/issue-7`,
  ]);
  assert.equal(args.includes("-f"), false);
  assert.equal(args.some((argument) => argument.startsWith("--force")), false);
  assert.equal(args.includes("--mirror"), false);
  assert.equal(args.includes("--delete"), false);
  assert.equal(args.some((argument) => argument.startsWith("+")), false);
});

test("host Git command guard has no force, branch-delete, or broad mutation seam", () => {
  const forbidden = [
    ["push", "--force", "origin", "main"],
    ["push", "origin", "+main:main"],
    ["push", "origin", ":refs/heads/main"],
    ["branch", "-D", "sandcastle/issue-7"],
    ["update-ref", "-d", "refs/heads/sandcastle/issue-7"],
    ["clean", "-fdx"],
    ["reset", "--hard"],
    ["remote", "set-url", "origin", ["https:/", "/example.invalid/repo.git"].join("")],
    ["config", "credential.helper", "evil"],
  ];
  for (const args of forbidden) {
    assert.throws(
      () => assertAllowedHostGitArgs(args),
      /forbidden|allow|outside|exact|mutation|invalid/i,
    );
  }
});

test("host git validates immutable root/remotes and performs a verified non-force SHA push", async () => {
  const fixture = await gitFixture();
  const ledger: string[][] = [];
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
    runGit(args, options) {
      ledger.push(args);
      return execFileSync("/usr/bin/git", args, options);
    },
  });

  const identity = hostGit.validateRepository();
  assert.equal(identity.root, fixture.work);
  assert.equal(hostGit.currentBranch(), "main");
  assert.equal(hostGit.currentHeadSha(), fixture.firstSha);
  hostGit.assertCleanWorkingTree();
  hostGit.assertCommitExists(fixture.firstSha);
  assert.equal(await hostGit.remoteBranchSha("sandcastle/issue-7"), undefined);
  await hostGit.pushExact({
    branch: "sandcastle/issue-7",
    candidateSha: fixture.firstSha,
    expectedRemoteSha: null,
  });
  assert.equal(
    await hostGit.remoteBranchSha("sandcastle/issue-7"),
    fixture.firstSha,
  );
  assert.ok(
    ledger.some((args) =>
      args.at(-1) === `${fixture.firstSha}:refs/heads/sandcastle/issue-7`
    ),
  );
  assert.ok(ledger.every((args) => !args.some((arg) => arg.startsWith("--force"))));
});

test("owner config is read from an exact commit and hidden index state fails closed", async () => {
  const fixture = await gitFixture();
  const configPath = join(fixture.work, ".sandcastle", "project.json");
  await mkdir(join(fixture.work, ".sandcastle"), { recursive: true });
  await writeFile(configPath, "{\"delivery\":false}\n");
  git(fixture.work, "add", ".sandcastle/project.json");
  git(fixture.work, "commit", "-m", "owner config");
  const ownerSha = git(fixture.work, "rev-parse", "HEAD").trim();
  git(fixture.work, "branch", "-f", "sandcastle/issue-7", ownerSha);
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  assert.equal(hostGit.readProjectConfigAtCommit(ownerSha), "{\"delivery\":false}\n");

  git(fixture.work, "update-index", "--assume-unchanged", ".sandcastle/project.json");
  await writeFile(configPath, "{\"delivery\":true}\n");
  assert.throws(
    () => hostGit.validateRepository(),
    /skip-worktree or assume-unchanged/,
  );
});

test("remote drift fails closed and leaves the remote unchanged", async () => {
  const fixture = await gitFixture();
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  await hostGit.pushExact({
    branch: "sandcastle/issue-7",
    candidateSha: fixture.firstSha,
    expectedRemoteSha: null,
  });

  await writeFile(join(fixture.work, "next.txt"), "next\n");
  git(fixture.work, "add", "next.txt");
  git(fixture.work, "commit", "-m", "next");
  const nextSha = git(fixture.work, "rev-parse", "HEAD").trim();
  git(fixture.work, "branch", "-f", "sandcastle/issue-7", nextSha);

  await assert.rejects(
    hostGit.pushExact({
      branch: "sandcastle/issue-7",
      candidateSha: nextSha,
      expectedRemoteSha: null,
    }),
    /remote drift/i,
  );
  assert.equal(
    await hostGit.remoteBranchSha("sandcastle/issue-7"),
    fixture.firstSha,
  );
});

test("pre-push revalidation rejects agent-written hook or credential configuration", async () => {
  const fixture = await gitFixture();
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  git(fixture.work, "config", "credential.helper", "malicious-helper");

  await assert.rejects(
    hostGit.pushExact({
      branch: "sandcastle/issue-7",
      candidateSha: fixture.firstSha,
      expectedRemoteSha: null,
    }),
    /forbidden routing, credential, hook, or transport configuration/,
  );
});

test("a second push URL fails identity validation before exact delivery", async () => {
  const fixture = await gitFixture();
  git(fixture.work, "remote", "set-url", "--add", "--push", "origin", fixture.remote);
  git(fixture.work, "remote", "set-url", "--add", "--push", "origin", join(fixture.work, "second.git"));
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  assert.throws(() => hostGit.validateRepository(), /remote identity mismatch/);
  assert.equal(git(fixture.remote, "for-each-ref", "--format=%(refname)").trim(), "");
});

test("pre-push revalidation rejects agent-written replacement refs", async () => {
  const fixture = await gitFixture();
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  git(fixture.work, "replace", fixture.firstSha, fixture.firstSha);

  await assert.rejects(
    hostGit.pushExact({
      branch: "sandcastle/issue-7",
      candidateSha: fixture.firstSha,
      expectedRemoteSha: null,
    }),
    /replacement refs or grafts/,
  );
});

test("a preserved managed worktree is detected before a retry opens Sandcastle", async () => {
  const fixture = await gitFixture();
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });
  const preserved = join(fixture.work, "..", "preserved-worktree");
  execFileSync("/usr/bin/git", ["worktree", "add", preserved, "sandcastle/issue-7"], {
    cwd: fixture.work,
  });

  assert.throws(
    () => hostGit.assertNoPreservedWorktree("sandcastle/issue-7"),
    /preserved Sandcastle worktree/,
  );
});

test("exact push never follows annotated tags even when local config requests it", async () => {
  const fixture = await gitFixture();
  git(fixture.work, "tag", "-a", "surprise", "-m", "must stay local", fixture.firstSha);
  git(fixture.work, "config", "push.followTags", "true");
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });

  await assert.rejects(
    hostGit.pushExact({
      branch: "sandcastle/issue-7",
      candidateSha: fixture.firstSha,
      expectedRemoteSha: null,
    }),
    /forbidden routing, credential, hook, or transport configuration/,
  );
  assert.equal(git(fixture.remote, "for-each-ref", "--format=%(refname)").trim(), "");
});

test("exact push overrides a trusted-home global followTags setting", async () => {
  const fixture = await gitFixture();
  const fakeHome = join(await mkdtemp(join(tmpdir(), "sandcastle-git-home-")), "home");
  await mkdir(fakeHome, { recursive: true });
  await writeFile(
    join(fakeHome, ".gitconfig"),
    [
      "[push]",
      "\tfollowTags = true",
      "[http]",
      ["\tproxy = http:", "//intercept.invalid"].join(""),
      "\tsslVerify = false",
      "[credential]",
      "\thelper = !false",
      "[url \"file:///untrusted/\"]",
      "\tinsteadOf = file:///",
      "",
    ].join("\n"),
  );
  git(fixture.work, "tag", "-a", "global-surprise", "-m", "must stay local", fixture.firstSha);
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: fakeHome,
  });

  await hostGit.pushExact({
    branch: "sandcastle/issue-7",
    candidateSha: fixture.firstSha,
    expectedRemoteSha: null,
  });
  assert.equal(
    git(fixture.remote, "for-each-ref", "--format=%(refname)").trim(),
    "refs/heads/sandcastle/issue-7",
  );
});

test("worktree-scoped Git config is rejected before delivery", async () => {
  const fixture = await gitFixture();
  git(fixture.work, "config", "extensions.worktreeConfig", "true");
  git(fixture.work, "config", "--worktree", "credential.helper", "evil");
  const hostGit = createHostGit({
    root: fixture.work,
    gitPath: "/usr/bin/git",
    remote: "origin",
    expectedFetchUrl: fixture.remote,
    expectedPushUrl: fixture.remote,
    osHome: userInfo().homedir,
  });

  await assert.rejects(
    hostGit.pushExact({
      branch: "sandcastle/issue-7",
      candidateSha: fixture.firstSha,
      expectedRemoteSha: null,
    }),
    /forbidden routing, credential, hook, or transport configuration/,
  );
});

async function gitFixture() {
  const root = await mkdtemp(join(tmpdir(), "sandcastle-host-git-"));
  const remote = join(root, "remote.git");
  const work = join(root, "work");
  execFileSync("/usr/bin/git", ["init", "--bare", remote]);
  execFileSync("/usr/bin/git", ["init", "-b", "main", work]);
  git(work, "config", "user.name", "Sandcastle Test");
  git(work, "config", "user.email", "sandcastle@example.invalid");
  await writeFile(join(work, "README.md"), "fixture\n");
  git(work, "add", "README.md");
  git(work, "commit", "-m", "initial");
  git(work, "branch", "sandcastle/issue-7");
  git(work, "remote", "add", "origin", remote);
  const firstSha = git(work, "rev-parse", "HEAD").trim();
  return { remote, work, firstSha };
}

function git(cwd: string, ...args: string[]): string {
  return execFileSync("/usr/bin/git", args, { cwd, encoding: "utf8" });
}
