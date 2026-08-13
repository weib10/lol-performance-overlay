import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { chmodSync, existsSync, readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { createSandbox } from "@ai-hero/sandcastle";
import { docker } from "@ai-hero/sandcastle/sandboxes/docker";

import {
  assertGitControlPlaneUnchanged,
  captureGitControlPlane,
  protectGitControlPlane,
  trustedHostProcessEnvironment,
  withTrustedHostProcess,
} from "./git-control-plane.mts";
import { IMAGE_NAME } from "./runtime.mts";

test("trusted host environment strips ambient loaders, proxies, routing, and Git overrides", () => {
  const controlled = trustedHostProcessEnvironment({
    PATH: "/attacker/bin",
    HOME: "/attacker/home",
    LD_PRELOAD: "/tmp/inject.so",
    HTTPS_PROXY: ["http:", "//proxy.invalid"].join(""),
    GIT_CONFIG_COUNT: "1",
    GIT_CONFIG_KEY_0: "core.fsmonitor",
    GIT_CONFIG_VALUE_0: "/tmp/evil",
    [["GH", "TOKEN"].join("_")]: ["connector", "token"].join("-"),
    SAFE_MARKER: "kept",
  }, "/trusted/home");

  assert.equal(controlled.HOME, "/trusted/home");
  assert.equal(controlled.PATH, "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
  assert.equal(controlled.LD_PRELOAD, undefined);
  assert.equal(controlled.HTTPS_PROXY, undefined);
  assert.equal(controlled.GH_TOKEN, undefined);
  assert.equal(controlled.SAFE_MARKER, "kept");
  assert.equal(controlled.GIT_CONFIG_COUNT, "5");
  assert.equal(controlled.GIT_CONFIG_KEY_0, "core.hooksPath");
  assert.equal(controlled.GIT_CONFIG_KEY_1, "core.fsmonitor");
  assert.equal(controlled.GIT_CONFIG_KEY_2, "credential.helper");
  assert.equal(controlled.GIT_CONFIG_KEY_3, "http.extraHeader");
  assert.equal(controlled.GIT_CONFIG_KEY_4, "push.followTags");
  assert.equal(controlled.GIT_CONFIG_VALUE_4, "false");
  assert.equal(controlled.GIT_NO_REPLACE_OBJECTS, "1");
});

test("trusted host Git status cannot execute a repository core.fsmonitor program", async () => {
  const fixture = await repositoryFixture();
  const hook = join(fixture.directory, "host-fsmonitor.sh");
  const marker = join(fixture.directory, "host-fsmonitor-ran");
  try {
    await writeFile(hook, `#!/bin/sh\ntouch '${marker}'\nexit 0\n`, "utf8");
    chmodSync(hook, 0o700);
    const configPath = join(fixture.repo, ".git", "config");
    await writeFile(
      configPath,
      `${readFileSync(configPath, "utf8")}\n[core]\n\tfsmonitor = ${hook}\n`,
      "utf8",
    );

    await withTrustedHostProcess(() => {
      execFileSync("git", ["status", "--porcelain"], {
        cwd: fixture.repo,
        stdio: "pipe",
      });
    });

    assert.equal(existsSync(marker), false);
  } finally {
    await rm(fixture.directory, { recursive: true, force: true });
  }
});

test("trusted host Git ignores an agent-created replacement ref", async () => {
  const fixture = await repositoryFixture();
  try {
    const original = git(fixture.repo, "rev-parse", "HEAD:base.txt").trim();
    const replacement = execFileSync("git", ["hash-object", "-w", "--stdin"], {
      cwd: fixture.repo,
      input: "replaced by an agent\n",
      encoding: "utf8",
      stdio: ["pipe", "pipe", "pipe"],
    }).trim();
    git(fixture.repo, "replace", original, replacement);
    assert.equal(git(fixture.repo, "show", "HEAD:base.txt"), "replaced by an agent\n");

    const guarded = await withTrustedHostProcess(() =>
      execFileSync("git", ["show", "HEAD:base.txt"], {
        cwd: fixture.repo,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"],
      })
    );
    assert.equal(guarded, "base\n");
  } finally {
    await rm(fixture.directory, { recursive: true, force: true });
  }
});

test("provider overlays common config, hooks, and worktree config read-only", async () => {
  const fixture = await gitFixture();
  try {
    let received: any;
    const base = {
      tag: "bind-mount" as const,
      name: "fake",
      env: {},
      sandboxHomedir: "/home/agent",
      async create(options: any) {
        received = options;
        return {};
      },
    };
    const protectedProvider = protectGitControlPlane(base);
    await protectedProvider.create({
      hostRepoPath: fixture.repo,
      worktreePath: fixture.worktree,
      mounts: [{
        hostPath: fixture.worktree,
        sandboxPath: "/home/agent/workspace",
      }],
      env: {},
    });

    const readonly = received.mounts.filter((mount: any) => mount.readonly);
    assert.equal(readonly.length, 6);
    assert.ok(readonly.some((mount: any) =>
      mount.hostPath.endsWith("/worktree/.git") &&
      mount.sandboxPath === "/home/agent/workspace/.git"
    ));
    assert.ok(readonly.some((mount: any) => mount.hostPath.endsWith("/.git/config")));
    assert.ok(readonly.some((mount: any) => mount.hostPath.endsWith("/.git/hooks")));
    assert.ok(readonly.some((mount: any) => mount.hostPath.endsWith("/config.worktree")));
    assert.ok(readonly.some((mount: any) => mount.hostPath.endsWith("/commondir")));
    assert.ok(readonly.some((mount: any) => mount.hostPath.endsWith("/gitdir")));
    assert.equal(received.env.GIT_CONFIG_KEY_0, "core.fsmonitor");
    assert.equal(received.env.GIT_CONFIG_VALUE_1, "/dev/null");
  } finally {
    await rm(fixture.directory, { recursive: true, force: true });
  }
});

test("raw control-plane guard blocks trusted cleanup after config bytes change", async () => {
  const fixture = await gitFixture();
  try {
    // Provider creation establishes the empty, protected worktree config file.
    await protectGitControlPlane({
      tag: "bind-mount" as const,
      name: "fake",
      env: {},
      async create() {
        return {};
      },
    }).create({
      hostRepoPath: fixture.repo,
      worktreePath: fixture.worktree,
      mounts: [{
        hostPath: fixture.worktree,
        sandboxPath: "/home/agent/workspace",
      }],
      env: {},
    });
    const snapshot = captureGitControlPlane(fixture.repo, fixture.worktree);

    const configPath = join(fixture.repo, ".git", "config");
    await writeFile(
      configPath,
      `${readFileSync(configPath, "utf8")}\n[core]\n\tfsmonitor = !false\n`,
      "utf8",
    );

    assert.throws(
      () => assertGitControlPlaneUnchanged(snapshot),
      /refusing to run trusted host Git cleanup/,
    );
  } finally {
    await rm(fixture.directory, { recursive: true, force: true });
  }
});

test(
  "Docker execution prevents agent writes while ordinary commits and official close still work",
  { skip: process.env.SANDCASTLE_DOCKER_INTEGRATION !== "1", timeout: 120_000 },
  async () => {
    const fixture = await repositoryFixture();
    const marker = join(tmpdir(), `sandcastle-host-fsmonitor-${process.pid}`);
    let sandbox: Awaited<ReturnType<typeof createSandbox>> | undefined;
    try {
      const configBefore = readFileSync(join(fixture.repo, ".git", "config"));
      sandbox = await createSandbox({
        cwd: fixture.repo,
        branch: "sandcastle/issue-99",
        baseBranch: fixture.baseSha,
        sandbox: protectGitControlPlane(docker({ imageName: IMAGE_NAME, cpus: 1 })),
      });

      const configAttack = await sandbox.exec(
        `git config --local core.fsmonitor '!touch ${marker}'`,
      );
      assert.notEqual(configAttack.exitCode, 0);

      const hookAttack = await sandbox.exec(
        "printf '#!/bin/sh\\ntouch /tmp/sandcastle-hook-ran\\n' > \"$(git rev-parse --git-common-dir)/hooks/pre-push\"",
      );
      assert.notEqual(hookAttack.exitCode, 0);

      const worktreeConfigAttack = await sandbox.exec(
        "printf '[core]\\nfsmonitor = !false\\n' > \"$(git rev-parse --git-dir)/config.worktree\"",
      );
      assert.notEqual(worktreeConfigAttack.exitCode, 0);

      const pointerAttack = await sandbox.exec(
        "printf 'gitdir: /tmp/agent-controlled-gitdir\\n' > .git",
      );
      assert.notEqual(pointerAttack.exitCode, 0);

      const commonPointerAttack = await sandbox.exec(
        "printf '../../../agent-controlled-common\\n' > \"$(git rev-parse --git-dir)/commondir\"",
      );
      assert.notEqual(commonPointerAttack.exitCode, 0);

      const ordinaryCommit = await sandbox.exec(
        "printf 'safe change\\n' > control-plane-smoke.txt && git add control-plane-smoke.txt && git -c user.name=Sandcastle -c user.email=sandcastle@example.invalid commit -m 'test: protected control plane'",
      );
      assert.equal(ordinaryCommit.exitCode, 0, ordinaryCommit.stderr);

      const closed = await sandbox.close();
      sandbox = undefined;
      assert.equal(closed.preservedWorktreePath, undefined);
      assert.equal(existsSync(marker), false);
      assert.deepEqual(readFileSync(join(fixture.repo, ".git", "config")), configBefore);
    } finally {
      if (sandbox) await sandbox.close().catch(() => {});
      await rm(marker, { force: true });
      await rm(fixture.directory, { recursive: true, force: true });
    }
  },
);

async function repositoryFixture() {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-git-control-docker-"));
  const repo = join(directory, "repo");
  git(directory, "init", "--initial-branch=main", repo);
  git(repo, "config", "user.name", "Fixture");
  git(repo, "config", "user.email", "fixture@example.invalid");
  await writeFile(join(repo, "base.txt"), "base\n", "utf8");
  git(repo, "add", "base.txt");
  git(repo, "commit", "-m", "base");
  return {
    directory,
    repo,
    baseSha: git(repo, "rev-parse", "HEAD").trim(),
  };
}

async function gitFixture() {
  const fixture = await repositoryFixture();
  const worktree = join(fixture.directory, "worktree");
  git(fixture.repo, "worktree", "add", "-b", "sandcastle/issue-42", worktree, fixture.baseSha);
  return { ...fixture, worktree };
}

function git(cwd: string, ...args: string[]): string {
  return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
}
