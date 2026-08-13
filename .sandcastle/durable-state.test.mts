import assert from "node:assert/strict";
import { mkdtemp, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  createAtomicStateStore,
  emptyWorkerState,
  type WorkerState,
} from "./durable-state.mts";

const REPO_ID = "repo-node-id";

test("atomic state keeps the previous generation when a crash occurs before rename", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-state-before-"));
  const filePath = join(directory, "state.json");
  const store = createAtomicStateStore({ filePath, repoId: REPO_ID });
  const first = await store.commit(0, {
    ...emptyWorkerState(REPO_ID),
    issues: { "7": trackedIssue(7) },
  });

  const crashingStore = createAtomicStateStore({
    filePath,
    repoId: REPO_ID,
    hooks: { beforeRename: () => { throw new Error("simulated crash"); } },
  });
  await assert.rejects(
    crashingStore.commit(first.generation, {
      ...first,
      issues: { ...first.issues, "8": trackedIssue(8) },
    }),
    /simulated crash/,
  );

  const recovered = await store.load();
  assert.equal(recovered.generation, 1);
  assert.deepEqual(Object.keys(recovered.issues), ["7"]);
});

test("restart reads the complete new generation after rename even if directory fsync crashes", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-state-after-"));
  const filePath = join(directory, "state.json");
  const store = createAtomicStateStore({ filePath, repoId: REPO_ID });
  const first = await store.commit(0, emptyWorkerState(REPO_ID));
  const crashingStore = createAtomicStateStore({
    filePath,
    repoId: REPO_ID,
    hooks: { afterRename: () => { throw new Error("simulated crash"); } },
  });

  await assert.rejects(
    crashingStore.commit(first.generation, {
      ...first,
      issues: { "7": trackedIssue(7) },
    }),
    /simulated crash/,
  );

  const recovered = await store.load();
  assert.equal(recovered.generation, 2);
  assert.deepEqual(Object.keys(recovered.issues), ["7"]);
  assert.equal((await stat(directory)).mode & 0o777, 0o700);
  assert.equal((await stat(filePath)).mode & 0o777, 0o600);
});

test("corrupt, unknown-version, repository-mismatched, and stale-generation state fail closed", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-state-invalid-"));
  const filePath = join(directory, "state.json");
  const store = createAtomicStateStore({ filePath, repoId: REPO_ID });
  const first = await store.commit(0, emptyWorkerState(REPO_ID));

  await assert.rejects(
    store.commit(0, first),
    /generation changed/,
  );
  await assert.rejects(
    createAtomicStateStore({ filePath, repoId: "different-repo" }).load(),
    /repository identity mismatch/,
  );

  const unknown = { ...first, schemaVersion: 999 } as unknown as WorkerState;
  const otherPath = join(directory, "unknown.json");
  const unknownStore = createAtomicStateStore({
    filePath: otherPath,
    repoId: REPO_ID,
  });
  await writeFile(otherPath, JSON.stringify(unknown));
  await assert.rejects(unknownStore.load(), /schema version/);
});

test("durable rounds reject a non-exact round start SHA before host work can resume", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sandcastle-state-round-sha-"));
  const store = createAtomicStateStore({
    filePath: join(directory, "state.json"),
    repoId: REPO_ID,
  });
  const issue = trackedIssue(7);

  await assert.rejects(
    store.commit(0, {
      ...emptyWorkerState(REPO_ID),
      issues: {
        "7": {
          ...issue,
          round: {
            number: 1,
            phase: "implement_pending",
            trigger: {
              kind: "initial",
              commentIds: [],
              snapshotCommentIds: [],
            },
            startSha: "not-an-exact-commit",
            statusMarker: "<!-- sandcastle-status:v1 -->",
            instructionSnapshot: {
              title: "Issue title",
              body: "Issue body",
              url: "https://github.com/example/project/issues/7",
              comments: [],
            },
          },
        },
      },
    }),
    /round start SHA/i,
  );
});

function trackedIssue(number: number) {
  return {
    issueId: `issue-${number}`,
    number,
    branch: `sandcastle/issue-${number}`,
    baseRef: "main",
    baseSha: "a".repeat(40),
    consumedCommentIds: [],
    nextRound: 1,
  };
}
