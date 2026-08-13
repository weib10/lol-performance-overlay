import { randomUUID } from "node:crypto";
import {
  mkdir,
  open,
  readFile,
  rename,
  rm,
  writeFile,
} from "node:fs/promises";
import { dirname } from "node:path";

interface LockOwner {
  schemaVersion: 1;
  pid: number;
  processStart: string;
  nonce: string;
}

export interface ProcessLockLease {
  release(): Promise<void>;
}

export async function acquireProcessLock(
  lockPath: string,
  options: {
    pid?: number;
    processStart?: string;
    isOwnerAlive?: (owner: LockOwner) => Promise<boolean>;
  } = {},
): Promise<ProcessLockLease> {
  const pid = options.pid ?? process.pid;
  const processStart = options.processStart ?? await readProcessStart(pid);
  const isOwnerAlive = options.isOwnerAlive ?? defaultIsOwnerAlive;
  const owner: LockOwner = {
    schemaVersion: 1,
    pid,
    processStart,
    nonce: randomUUID(),
  };
  const lockDirectory = dirname(lockPath);
  await mkdir(lockDirectory, { recursive: true, mode: 0o700 });

  for (let attempt = 0; attempt < 4; attempt += 1) {
    const candidatePath = `${lockPath}.candidate.${owner.nonce}.${randomUUID()}`;
    try {
      await mkdir(candidatePath, { mode: 0o700 });
      await writeFile(
        `${candidatePath}/owner.json`,
        `${JSON.stringify(owner)}\n`,
        { mode: 0o600, flag: "wx" },
      );
      const candidateHandle = await open(candidatePath, "r");
      try {
        await candidateHandle.sync();
      } finally {
        await candidateHandle.close();
      }
      await rename(candidatePath, lockPath);
      return lease(lockPath, owner);
    } catch (error) {
      await rm(candidatePath, { recursive: true, force: true });
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "EEXIST" && code !== "ENOTEMPTY") throw error;
      let existing: LockOwner;
      try {
        existing = await readOwner(lockPath);
      } catch (readError) {
        if ((readError as NodeJS.ErrnoException).code === "ENOENT") continue;
        throw readError;
      }
      if (await isOwnerAlive(existing)) {
        throw new Error(
          `A Sandcastle worker is already running (PID ${existing.pid}).`,
        );
      }
      const stalePath = `${lockPath}.stale.${existing.nonce}.${randomUUID()}`;
      try {
        await rename(lockPath, stalePath);
      } catch (renameError) {
        if ((renameError as NodeJS.ErrnoException).code === "ENOENT") continue;
        throw renameError;
      }
      await rm(stalePath, { recursive: true, force: true });
    }
  }
  throw new Error("Could not acquire the Sandcastle process lock after reconciliation.");
}

function lease(lockPath: string, owner: LockOwner): ProcessLockLease {
  let released = false;
  return {
    async release() {
      if (released) return;
      const current = await readOwner(lockPath);
      if (current.nonce !== owner.nonce) {
        throw new Error("Sandcastle process lock ownership changed; refusing release.");
      }
      const releasedPath = `${lockPath}.released.${owner.nonce}`;
      await rename(lockPath, releasedPath);
      await rm(releasedPath, { recursive: true, force: true });
      released = true;
    },
  };
}

async function readOwner(lockPath: string): Promise<LockOwner> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(await readFile(`${lockPath}/owner.json`, "utf8"));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      const missing = new Error("Sandcastle process lock metadata is missing.") as NodeJS.ErrnoException;
      missing.code = "ENOENT";
      throw missing;
    }
    throw new Error("Sandcastle process lock metadata is missing or corrupt.");
  }
  if (!isLockOwner(parsed)) {
    throw new Error("Sandcastle process lock metadata is invalid.");
  }
  return parsed;
}

function isLockOwner(value: unknown): value is LockOwner {
  if (typeof value !== "object" || value === null) return false;
  const owner = value as Partial<LockOwner>;
  return owner.schemaVersion === 1 &&
    Number.isSafeInteger(owner.pid) && Number(owner.pid) > 0 &&
    typeof owner.processStart === "string" && owner.processStart.length > 0 &&
    typeof owner.nonce === "string" && owner.nonce.length > 0;
}

async function defaultIsOwnerAlive(owner: LockOwner): Promise<boolean> {
  try {
    process.kill(owner.pid, 0);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ESRCH") return false;
    if (code === "EPERM") return true;
    throw error;
  }
  try {
    return await readProcessStart(owner.pid) === owner.processStart;
  } catch {
    // PID reuse cannot be disproven without its start token, so fail closed.
    return true;
  }
}

async function readProcessStart(pid: number): Promise<string> {
  const stat = await readFile(`/proc/${pid}/stat`, "utf8");
  const closingParenthesis = stat.lastIndexOf(")");
  if (closingParenthesis < 0) throw new Error(`Could not parse /proc/${pid}/stat.`);
  const fieldsAfterCommand = stat.slice(closingParenthesis + 2).split(" ");
  const startTime = fieldsAfterCommand[19];
  if (!startTime) throw new Error(`Could not read process start time for PID ${pid}.`);
  return startTime;
}
