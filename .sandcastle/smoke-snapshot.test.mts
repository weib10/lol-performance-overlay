import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  assertRepositoryStateUnchanged,
  captureRepositoryState,
  createDisposableGitSnapshot,
  createDisposableSnapshotBranch,
} from "./smoke-snapshot.mts";

const GIT = "/usr/bin/git";

function git(repository: string, ...args: string[]): string {
  return execFileSync(GIT, ["--no-optional-locks", "-C", repository, ...args], {
    encoding: "utf8",
  });
}

test("disposable snapshot commits the working-tree view of tracked and visible untracked files", async () => {
  const fixture = await mkdtemp(join(tmpdir(), "sandcastle-smoke-source-test-"));
  let snapshot: string | undefined;

  try {
    git(fixture, "init", "--quiet", "--initial-branch=main");
    git(fixture, "config", "user.name", "Sandcastle Test");
    git(fixture, "config", "user.email", "sandcastle-test.invalid@example.invalid");
    await mkdir(join(fixture, ".sandcastle"));
    await writeFile(join(fixture, ".sandcastle", ".gitignore"), "worktrees/\n");
    await writeFile(join(fixture, ".gitattributes"), "* text=auto\n");
    await writeFile(join(fixture, ".gitignore"), "ignored.txt\ntracked-ignored.txt\n");
    await writeFile(join(fixture, "crlf.txt"), "one\ntwo\n");
    await writeFile(join(fixture, "tracked.txt"), "committed\n");
    await writeFile(join(fixture, "deleted.txt"), "delete me\n");
    await writeFile(join(fixture, "tracked-ignored.txt"), "tracked despite ignore\n");
    await symlink("tracked.txt", join(fixture, "tracked-link"));
    git(
      fixture,
      "add",
      ".gitattributes",
      ".gitignore",
      ".sandcastle/.gitignore",
      "crlf.txt",
      "tracked.txt",
      "deleted.txt",
      "tracked-link",
    );
    git(fixture, "add", "--force", "tracked-ignored.txt");
    git(fixture, "commit", "--quiet", "-m", "fixture");

    await writeFile(join(fixture, "crlf.txt"), "one\r\ntwo\r\n");
    await writeFile(join(fixture, "tracked.txt"), "staged\n");
    git(fixture, "add", "tracked.txt");
    await writeFile(join(fixture, "tracked.txt"), "working tree wins\n");
    await rm(join(fixture, "deleted.txt"));
    await writeFile(join(fixture, "visible.txt"), "visible\n");
    await writeFile(join(fixture, "ignored.txt"), "must not copy\n");

    const sourceBefore = captureRepositoryState(fixture);
    snapshot = await createDisposableGitSnapshot(fixture);
    const sourceAfter = captureRepositoryState(fixture);
    assertRepositoryStateUnchanged(sourceBefore, sourceAfter, "source fixture");

    assert.equal(await readFile(join(snapshot, "tracked.txt"), "utf8"), "working tree wins\n");
    assert.equal(await readFile(join(snapshot, "visible.txt"), "utf8"), "visible\n");
    assert.equal(await readFile(join(snapshot, "crlf.txt"), "utf8"), "one\r\ntwo\r\n");
    assert.equal(git(snapshot, "show", "HEAD:crlf.txt"), "one\r\ntwo\r\n");
    assert.equal(
      await readFile(join(snapshot, "tracked-ignored.txt"), "utf8"),
      "tracked despite ignore\n",
    );
    assert.equal((await lstat(join(snapshot, "tracked-link"))).isSymbolicLink(), true);
    await assert.rejects(readFile(join(snapshot, "deleted.txt")), { code: "ENOENT" });
    await assert.rejects(readFile(join(snapshot, "ignored.txt")), { code: "ENOENT" });
    assert.equal(git(snapshot, "status", "--porcelain=v2", "--untracked-files=all"), "");
    assert.equal(git(snapshot, "log", "-1", "--format=%s").trim(), "Sandcastle smoke snapshot");

    const smokeBranch = "sandcastle/no-change-smoke/test";
    createDisposableSnapshotBranch(snapshot, smokeBranch);
    const beforeWorktree = captureRepositoryState(snapshot);
    const worktree = join(snapshot, ".sandcastle", "worktrees", "smoke-test");
    git(snapshot, "worktree", "add", "--quiet", worktree, smokeBranch);
    assert.throws(
      () =>
        assertRepositoryStateUnchanged(
          beforeWorktree,
          captureRepositoryState(snapshot),
          "snapshot",
        ),
      /snapshot Git worktrees changed byte-for-byte/,
    );
    git(snapshot, "worktree", "remove", "--force", worktree);
    assertRepositoryStateUnchanged(
      beforeWorktree,
      captureRepositoryState(snapshot),
      "snapshot",
    );
  } finally {
    if (snapshot) await rm(snapshot, { recursive: true, force: true });
    await rm(fixture, { recursive: true, force: true });
  }
});

test("repository state comparison names the exact changed field", async () => {
  const fixture = await mkdtemp(join(tmpdir(), "sandcastle-smoke-state-test-"));

  try {
    git(fixture, "init", "--quiet", "--initial-branch=main");
    git(fixture, "config", "user.name", "Sandcastle Test");
    git(fixture, "config", "user.email", "sandcastle-test.invalid@example.invalid");
    await writeFile(join(fixture, "tracked.txt"), "one\n");
    git(fixture, "add", "tracked.txt");
    git(fixture, "commit", "--quiet", "-m", "fixture");

    const before = captureRepositoryState(fixture);
    await writeFile(join(fixture, "tracked.txt"), "two\n");
    const after = captureRepositoryState(fixture);

    assert.throws(
      () => assertRepositoryStateUnchanged(before, after, "fixture"),
      /fixture Git status changed byte-for-byte/,
    );
  } finally {
    await rm(fixture, { recursive: true, force: true });
  }
});
