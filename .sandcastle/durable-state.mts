import { randomUUID } from "node:crypto";
import {
  chmod,
  mkdir,
  open,
  readFile,
  rename,
  rm,
} from "node:fs/promises";
import { dirname, join } from "node:path";

import { assertExactCommitSha } from "./exact-commit-sha.mts";

export const WORKER_STATE_SCHEMA_VERSION = 1;

const ROUND_PHASES = new Set<RoundPhase>([
  "implement_pending",
  "gate1_pending",
  "review_pending",
  "gate2_pending",
  "candidate_verified",
  "push_pending",
  "push_verified",
  "pr_pending",
  "pr_verified",
  "report_pending",
  "approval_pending",
  "ready_pending",
  "merge_pending",
  "merge_verified",
  "waiting",
  "failed",
  "blocked",
]);

export type RoundPhase =
  | "implement_pending"
  | "gate1_pending"
  | "review_pending"
  | "gate2_pending"
  | "candidate_verified"
  | "push_pending"
  | "push_verified"
  | "pr_pending"
  | "pr_verified"
  | "report_pending"
  | "approval_pending"
  | "ready_pending"
  | "merge_pending"
  | "merge_verified"
  | "waiting"
  | "failed"
  | "blocked";

export interface RoundTrigger {
  kind: "initial" | "revision";
  commentIds: string[];
  snapshotCommentIds: string[];
}

export interface QualificationRecord {
  sha: string;
  outcome: string;
}

export interface DurableRoundState {
  number: number;
  phase: RoundPhase;
  trigger: RoundTrigger;
  startSha: string;
  statusMarker: string;
  instructionSnapshot?: {
    title: string;
    body: string;
    url: string;
    comments: Array<{
      id: string;
      body: string;
      createdAt: string;
      updatedAt: string;
    }>;
  };
  implementation?: QualificationRecord;
  gate1?: QualificationRecord;
  review?: QualificationRecord;
  gate2?: QualificationRecord;
  candidateSha?: string;
  expectedRemoteSha?: string | null;
  pushedSha?: string;
  statusCommentId?: string;
  approvalCommentId?: string;
  approvalCommentIds?: string[];
  approvalUpdatedAt?: string;
  mergeEvidenceFingerprint?: string;
  mergeBaseSha?: string;
  failureCode?: string;
}

export interface DurablePullRequestState {
  nodeId: string;
  number: number;
  headRef: string;
  baseRef: string;
  url: string;
  headSha?: string;
}

export interface DurableIssueState {
  issueId: string;
  number: number;
  branch: string;
  baseRef: string;
  baseSha: string;
  lastDeliveredSha?: string;
  consumedCommentIds: string[];
  nextRound: number;
  pullRequest?: DurablePullRequestState;
  round?: DurableRoundState;
}

export interface WorkerState {
  schemaVersion: typeof WORKER_STATE_SCHEMA_VERSION;
  generation: number;
  repoId: string;
  issues: Record<string, DurableIssueState>;
}

export function emptyWorkerState(repoId: string): WorkerState {
  return {
    schemaVersion: WORKER_STATE_SCHEMA_VERSION,
    generation: 0,
    repoId,
    issues: {},
  };
}

export interface AtomicStateStore {
  load(): Promise<WorkerState>;
  commit(
    expectedGeneration: number,
    next: WorkerState,
  ): Promise<WorkerState>;
}

interface AtomicStateHooks {
  beforeRename?(): void | Promise<void>;
  afterRename?(): void | Promise<void>;
}

export function createAtomicStateStore(options: {
  filePath: string;
  repoId: string;
  hooks?: AtomicStateHooks;
}): AtomicStateStore {
  const { filePath, repoId, hooks = {} } = options;

  async function load(): Promise<WorkerState> {
    let raw: string;
    try {
      raw = await readFile(filePath, "utf8");
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") {
        return emptyWorkerState(repoId);
      }
      throw error;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw new Error("Sandcastle durable state is corrupt JSON; refusing host actions.");
    }
    return validateWorkerState(parsed, repoId);
  }

  return {
    load,
    async commit(expectedGeneration, next) {
      const current = await load();
      if (current.generation !== expectedGeneration) {
        throw new Error(
          `Sandcastle durable state generation changed: expected ${expectedGeneration}, found ${current.generation}.`,
        );
      }
      if (next.repoId !== repoId) {
        throw new Error("Sandcastle durable state repository identity mismatch.");
      }
      const committed: WorkerState = {
        ...next,
        schemaVersion: WORKER_STATE_SCHEMA_VERSION,
        generation: expectedGeneration + 1,
        repoId,
      };
      validateWorkerState(committed, repoId);
      await atomicWrite(filePath, committed, hooks);
      return committed;
    },
  };
}

async function atomicWrite(
  filePath: string,
  value: WorkerState,
  hooks: AtomicStateHooks,
): Promise<void> {
  const directory = dirname(filePath);
  await mkdir(directory, { recursive: true, mode: 0o700 });
  await chmod(directory, 0o700);
  const temporary = join(
    directory,
    `.${filePath.slice(filePath.lastIndexOf("/") + 1)}.${process.pid}.${randomUUID()}.tmp`,
  );
  let renamed = false;
  const handle = await open(temporary, "wx", 0o600);
  try {
    await handle.writeFile(`${JSON.stringify(value)}\n`, "utf8");
    await handle.sync();
    await handle.close();
    await hooks.beforeRename?.();
    await rename(temporary, filePath);
    renamed = true;
    await chmod(filePath, 0o600);
    await hooks.afterRename?.();
    const directoryHandle = await open(directory, "r");
    try {
      await directoryHandle.sync();
    } finally {
      await directoryHandle.close();
    }
  } finally {
    try {
      await handle.close();
    } catch {
      // The handle was already closed before rename.
    }
    if (!renamed) await rm(temporary, { force: true });
  }
}

function validateWorkerState(value: unknown, repoId: string): WorkerState {
  if (!isRecord(value)) {
    throw new Error("Sandcastle durable state must be a JSON object.");
  }
  if (value.schemaVersion !== WORKER_STATE_SCHEMA_VERSION) {
    throw new Error(
      `Unsupported Sandcastle durable state schema version: ${String(value.schemaVersion)}.`,
    );
  }
  if (value.repoId !== repoId) {
    throw new Error("Sandcastle durable state repository identity mismatch.");
  }
  if (!Number.isSafeInteger(value.generation) || Number(value.generation) < 0) {
    throw new Error("Sandcastle durable state has an invalid generation.");
  }
  if (!isRecord(value.issues)) {
    throw new Error("Sandcastle durable state has an invalid issues map.");
  }
  for (const [key, issue] of Object.entries(value.issues)) {
    validateIssueState(key, issue);
  }
  return value as unknown as WorkerState;
}

function validateIssueState(key: string, value: unknown): void {
  if (!isRecord(value)) throw new Error(`Invalid durable Issue state: ${key}.`);
  if (!Number.isSafeInteger(value.number) || Number(value.number) < 1 ||
      String(value.number) !== key) {
    throw new Error(`Durable Issue key/number mismatch: ${key}.`);
  }
  if (!isNonemptyString(value.issueId)) {
    throw new Error(`Durable Issue ${key} has invalid issueId.`);
  }
  if (value.branch !== `sandcastle/issue-${key}`) {
    throw new Error(`Durable Issue ${key} has an invalid owned branch.`);
  }
  if (typeof value.baseRef !== "string" ||
      !/^(?!.*(?:\.\.|@\{|[~^:?*\[\\]))(?!\/)(?!.*\/$)[A-Za-z0-9._/-]+$/.test(value.baseRef)) {
    throw new Error(`Durable Issue ${key} has an invalid baseRef.`);
  }
  assertExactCommitSha(value.baseSha, `Durable Issue ${key} base SHA`);
  if (value.lastDeliveredSha !== undefined) {
    assertExactCommitSha(
      value.lastDeliveredSha,
      `Durable Issue ${key} last delivered SHA`,
    );
  }
  if (!Array.isArray(value.consumedCommentIds) ||
      !value.consumedCommentIds.every(isNonemptyString) ||
      new Set(value.consumedCommentIds).size !== value.consumedCommentIds.length) {
    throw new Error(`Durable Issue ${key} has invalid consumed comments.`);
  }
  if (!Number.isSafeInteger(value.nextRound) || Number(value.nextRound) < 1) {
    throw new Error(`Durable Issue ${key} has invalid next round.`);
  }
  if (value.pullRequest !== undefined) {
    validatePullRequestState(key, value.pullRequest, value.branch, value.baseRef);
  }
  if (value.round !== undefined) {
    validateRoundState(key, value.round, value);
  }
}

function validatePullRequestState(
  key: string,
  value: unknown,
  branch: unknown,
  baseRef: unknown,
): void {
  if (!isRecord(value) || !isNonemptyString(value.nodeId) ||
      !Number.isSafeInteger(value.number) || Number(value.number) < 1 ||
      value.headRef !== branch || value.baseRef !== baseRef ||
      typeof value.url !== "string" || !value.url.startsWith("https://github.com/")) {
    throw new Error(`Durable Issue ${key} has an invalid pull request.`);
  }
  assertExactCommitSha(
    value.headSha,
    `Durable Issue ${key} pull request head SHA`,
  );
}

function validateRoundState(
  key: string,
  value: unknown,
  issue: Record<string, unknown>,
): void {
  if (!isRecord(value) || !Number.isSafeInteger(value.number) ||
      Number(value.number) < 1 || Number(value.number) > Number(issue.nextRound)) {
    throw new Error(`Durable Issue ${key} has an invalid round.`);
  }
  if (typeof value.phase !== "string" ||
      !ROUND_PHASES.has(value.phase as RoundPhase)) {
    throw new Error(`Durable Issue ${key} has an invalid round phase.`);
  }
  assertExactCommitSha(value.startSha, `Durable Issue ${key} round start SHA`);
  if (value.statusMarker !== "<!-- sandcastle-status:v1 -->") {
    throw new Error(`Durable Issue ${key} has an invalid status marker.`);
  }
  validateRoundTrigger(key, value.trigger);
  validateInstructionSnapshot(key, value.instructionSnapshot);

  for (const property of ["implementation", "gate1", "review", "gate2"] as const) {
    const record = value[property];
    if (record === undefined) continue;
    if (!isRecord(record) || !isNonemptyString(record.outcome)) {
      throw new Error(`Durable Issue ${key} has an invalid ${property} record.`);
    }
    assertExactCommitSha(record.sha, `Durable Issue ${key} ${property} SHA`);
  }
  for (const property of ["candidateSha", "pushedSha", "mergeBaseSha"] as const) {
    if (value[property] !== undefined) {
      assertExactCommitSha(
        value[property],
        `Durable Issue ${key} ${property}`,
      );
    }
  }
  if (value.expectedRemoteSha !== undefined &&
      value.expectedRemoteSha !== null) {
    assertExactCommitSha(
      value.expectedRemoteSha,
      `Durable Issue ${key} expected remote SHA`,
    );
  }
  for (const property of [
    "statusCommentId",
    "approvalCommentId",
    "approvalUpdatedAt",
    "mergeEvidenceFingerprint",
    "failureCode",
  ] as const) {
    if (value[property] !== undefined && !isNonemptyString(value[property])) {
      throw new Error(`Durable Issue ${key} has an invalid ${property}.`);
    }
  }
  if (value.approvalCommentIds !== undefined) {
    validateStringIds(
      value.approvalCommentIds,
      `Durable Issue ${key} approval comments`,
    );
  }

  validateRoundPhaseEvidence(key, value, issue);
}

function validateRoundTrigger(key: string, value: unknown): void {
  if (!isRecord(value) || !["initial", "revision"].includes(String(value.kind))) {
    throw new Error(`Durable Issue ${key} has an invalid round trigger.`);
  }
  validateStringIds(value.commentIds, `Durable Issue ${key} trigger comments`);
  validateStringIds(
    value.snapshotCommentIds,
    `Durable Issue ${key} snapshot comments`,
  );
}

function validateInstructionSnapshot(key: string, value: unknown): void {
  if (!isRecord(value) || typeof value.title !== "string" ||
      typeof value.body !== "string" || typeof value.url !== "string" ||
      !value.url.startsWith("https://github.com/") ||
      !Array.isArray(value.comments)) {
    throw new Error(`Durable Issue ${key} has an invalid instruction snapshot.`);
  }
  for (const comment of value.comments) {
    if (!isRecord(comment) || !isNonemptyString(comment.id) ||
        typeof comment.body !== "string" ||
        !isNonemptyString(comment.createdAt) ||
        !isNonemptyString(comment.updatedAt)) {
      throw new Error(`Durable Issue ${key} has an invalid instruction comment.`);
    }
  }
}

function validateRoundPhaseEvidence(
  key: string,
  round: Record<string, unknown>,
  issue: Record<string, unknown>,
): void {
  const phase = round.phase as RoundPhase;
  const afterImplementation = phase !== "implement_pending" &&
    !["failed", "blocked"].includes(phase);
  const afterGate1 = !["implement_pending", "gate1_pending", "failed", "blocked"].includes(phase);
  const afterReview = ![
    "implement_pending",
    "gate1_pending",
    "review_pending",
    "failed",
    "blocked",
  ].includes(phase);
  if (afterImplementation && round.implementation === undefined) {
    throw new Error(`Durable Issue ${key} phase lacks implementation evidence.`);
  }
  if (afterGate1 && round.gate1 === undefined) {
    throw new Error(`Durable Issue ${key} phase lacks gate1 evidence.`);
  }
  if (afterReview && round.review === undefined) {
    throw new Error(`Durable Issue ${key} phase lacks review evidence.`);
  }
  if (phase === "gate2_pending" &&
      (round.review as Record<string, unknown> | undefined)?.outcome !== "corrected") {
    throw new Error(`Durable Issue ${key} gate2 lacks reviewer corrections.`);
  }
  const candidatePhases: RoundPhase[] = [
    "candidate_verified",
    "push_pending",
    "push_verified",
    "pr_pending",
    "pr_verified",
    "report_pending",
    "approval_pending",
    "ready_pending",
    "merge_pending",
    "merge_verified",
    "waiting",
  ];
  if (candidatePhases.includes(phase)) {
    assertExactCommitSha(
      round.candidateSha,
      `Durable Issue ${key} candidate SHA`,
    );
  }
  const pushedPhases: RoundPhase[] = [
    "push_verified",
    "pr_pending",
    "pr_verified",
    "report_pending",
    "approval_pending",
    "ready_pending",
    "merge_pending",
    "merge_verified",
    "waiting",
  ];
  if (pushedPhases.includes(phase)) {
    assertExactCommitSha(round.pushedSha, `Durable Issue ${key} pushed SHA`);
  }
  const prPhases: RoundPhase[] = [
    "pr_verified",
    "report_pending",
    "approval_pending",
    "ready_pending",
    "merge_pending",
    "merge_verified",
    "waiting",
  ];
  if (prPhases.includes(phase) && issue.pullRequest === undefined) {
    throw new Error(`Durable Issue ${key} phase lacks pull request evidence.`);
  }
  if (["approval_pending", "ready_pending", "merge_pending", "merge_verified"].includes(phase) &&
      (!isNonemptyString(round.approvalCommentId) || !Array.isArray(round.approvalCommentIds))) {
    throw new Error(`Durable Issue ${key} approval phase lacks approval evidence.`);
  }
  if (["ready_pending", "merge_pending"].includes(phase) &&
      !isNonemptyString(round.mergeEvidenceFingerprint)) {
    throw new Error(`Durable Issue ${key} merge phase lacks evidence fingerprint.`);
  }
  if (["merge_pending", "merge_verified"].includes(phase)) {
    assertExactCommitSha(
      round.mergeBaseSha,
      `Durable Issue ${key} pre-merge base SHA`,
    );
  }
  if (["waiting", "approval_pending", "ready_pending", "merge_pending", "merge_verified"].includes(phase)) {
    assertExactCommitSha(
      issue.lastDeliveredSha,
      `Durable Issue ${key} last delivered SHA`,
    );
    if (issue.lastDeliveredSha !== round.candidateSha ||
        round.pushedSha !== round.candidateSha) {
      throw new Error(`Durable Issue ${key} delivered SHA evidence is inconsistent.`);
    }
  }
}

function validateStringIds(value: unknown, label: string): void {
  if (!Array.isArray(value) || !value.every(isNonemptyString) ||
      new Set(value).size !== value.length) {
    throw new Error(`${label} are invalid.`);
  }
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && !value.includes("\0");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
