import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { acquireProcessLock } from "./process-lock.mts";

test("one repository process lock wins and release permits the next worker", async () => {
  const root = await mkdtemp(join(tmpdir(), "sandcastle-lock-live-"));
  const lockPath = join(root, "worker.lock");
  const first = await acquireProcessLock(lockPath, {
    pid: 101,
    processStart: "start-a",
    isOwnerAlive: async () => true,
  });

  await assert.rejects(
    acquireProcessLock(lockPath, {
      pid: 202,
      processStart: "start-b",
      isOwnerAlive: async () => true,
    }),
    /already running/,
  );

  await first.release();
  const second = await acquireProcessLock(lockPath, {
    pid: 202,
    processStart: "start-b",
    isOwnerAlive: async () => true,
  });
  await second.release();
});

test("a dead owner is reclaimed but a lease never removes a different nonce", async () => {
  const root = await mkdtemp(join(tmpdir(), "sandcastle-lock-stale-"));
  const lockPath = join(root, "worker.lock");
  const stale = await acquireProcessLock(lockPath, {
    pid: 303,
    processStart: "dead-start",
    isOwnerAlive: async () => false,
  });
  await writeFile(
    join(lockPath, "owner.json"),
    JSON.stringify({
      schemaVersion: 1,
      pid: 404,
      processStart: "replacement",
      nonce: "different-owner",
    }),
  );
  await assert.rejects(stale.release(), /ownership changed/);

  const owner = JSON.parse(await readFile(join(lockPath, "owner.json"), "utf8"));
  assert.equal(owner.nonce, "different-owner");
});

test("two stale reclaimers still produce exactly one live lease", async () => {
  const root = await mkdtemp(join(tmpdir(), "sandcastle-lock-race-"));
  const lockPath = join(root, "worker.lock");
  await acquireProcessLock(lockPath, {
    pid: 505,
    processStart: "dead-start",
    isOwnerAlive: async () => false,
  });

  const contenders = await Promise.allSettled([
    acquireProcessLock(lockPath, {
      pid: 601,
      processStart: "one",
      isOwnerAlive: async (owner) => owner.pid !== 505,
    }),
    acquireProcessLock(lockPath, {
      pid: 602,
      processStart: "two",
      isOwnerAlive: async (owner) => owner.pid !== 505,
    }),
  ]);
  const winners = contenders.filter((result) => result.status === "fulfilled");
  assert.equal(winners.length, 1);
  await (winners[0] as PromiseFulfilledResult<{ release(): Promise<void> }>).value.release();
});

test("an abandoned candidate is harmless while corrupt acquired metadata fails closed", async () => {
  const root = await mkdtemp(join(tmpdir(), "sandcastle-lock-atomic-"));
  const lockPath = join(root, "worker.lock");
  await mkdir(`${lockPath}.candidate.abandoned`);
  const lease = await acquireProcessLock(lockPath, {
    pid: 707,
    processStart: "live",
    isOwnerAlive: async () => true,
  });
  await lease.release();

  await mkdir(lockPath);
  await writeFile(join(lockPath, "owner.json"), "truncated");
  await assert.rejects(
    acquireProcessLock(lockPath, {
      pid: 808,
      processStart: "other",
      isOwnerAlive: async () => false,
    }),
    /missing or corrupt/,
  );
});
