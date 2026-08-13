import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const PROJECT_CONFIG_PATH = join(
  dirname(fileURLToPath(import.meta.url)),
  "project.json",
);

export interface ActorIdentity {
  login: string;
  nodeId: string;
}

export interface SandcastleProjectConfig {
  schemaVersion: 1;
  repository: {
    host: "github.com";
    nameWithOwner: string;
    nodeId: string;
    owner: ActorIdentity;
    remote: "origin";
    fetchUrl: string;
    pushUrl: string;
    baseRef: string;
  };
  trustedActor: ActorIdentity;
  deliveryActor: ActorIdentity & { minimumPermission: "WRITE" };
  queueLabel: string;
  branchPrefix: "sandcastle/issue-";
  tools: {
    gh: string;
    git: string;
  };
  projectGate: "./scripts/package.sh";
  delivery: {
    enabled: boolean;
  };
  merge: {
    enabled: boolean;
    method: "SQUASH";
    requiredChecks: string[];
  };
  comments: {
    maxUtf8Bytes: number;
  };
  stateNamespace: string;
}

export async function loadProjectConfig(
  filePath = PROJECT_CONFIG_PATH,
): Promise<SandcastleProjectConfig> {
  let raw: string;
  try {
    raw = await readFile(filePath, "utf8");
  } catch (error) {
    throw new Error(`Cannot read Sandcastle project config at ${filePath}.`, {
      cause: error,
    });
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    throw new Error(`Sandcastle project config at ${filePath} is not valid JSON.`, {
      cause: error,
    });
  }
  return parseProjectConfig(parsed);
}

export function parseProjectConfig(value: unknown): SandcastleProjectConfig {
  const config = record(value, "project config");
  exactKeys(config, "project config", [
    "schemaVersion",
    "repository",
    "trustedActor",
    "deliveryActor",
    "queueLabel",
    "branchPrefix",
    "tools",
    "projectGate",
    "delivery",
    "merge",
    "comments",
    "stateNamespace",
  ]);
  literal(config.schemaVersion, 1, "schemaVersion");

  const repository = record(config.repository, "repository");
  exactKeys(repository, "repository", [
    "host",
    "nameWithOwner",
    "nodeId",
    "owner",
    "remote",
    "fetchUrl",
    "pushUrl",
    "baseRef",
  ]);
  literal(repository.host, "github.com", "repository.host");
  const nameWithOwner = nonEmpty(repository.nameWithOwner, "repository.nameWithOwner");
  if (!/^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\/[A-Za-z0-9._-]+$/.test(nameWithOwner)) {
    throw new Error("repository.nameWithOwner must be exactly owner/repository.");
  }
  nonEmpty(repository.nodeId, "repository.nodeId");
  const owner = actor(repository.owner, "repository.owner");
  if (nameWithOwner.split("/", 1)[0] !== owner.login) {
    throw new Error("repository.owner.login must match repository.nameWithOwner.");
  }
  literal(repository.remote, "origin", "repository.remote");
  const expectedUrl = `https://github.com/${nameWithOwner}.git`;
  literal(repository.fetchUrl, expectedUrl, "repository.fetchUrl");
  literal(repository.pushUrl, expectedUrl, "repository.pushUrl");
  const baseRef = nonEmpty(repository.baseRef, "repository.baseRef");
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(baseRef) ||
      baseRef.includes("..") || baseRef.endsWith("/")) {
    throw new Error("repository.baseRef must be a safe literal branch name.");
  }

  const trustedActor = actor(config.trustedActor, "trustedActor");
  const deliveryActorRecord = record(config.deliveryActor, "deliveryActor");
  exactKeys(deliveryActorRecord, "deliveryActor", [
    "login",
    "nodeId",
    "minimumPermission",
  ]);
  const deliveryActor = {
    login: nonEmpty(deliveryActorRecord.login, "deliveryActor.login"),
    nodeId: nonEmpty(deliveryActorRecord.nodeId, "deliveryActor.nodeId"),
    minimumPermission: literal(
      deliveryActorRecord.minimumPermission,
      "WRITE",
      "deliveryActor.minimumPermission",
    ),
  };

  nonEmpty(config.queueLabel, "queueLabel");
  literal(config.branchPrefix, "sandcastle/issue-", "branchPrefix");
  const tools = record(config.tools, "tools");
  exactKeys(tools, "tools", ["gh", "git"]);
  literal(tools.gh, "/usr/bin/gh", "tools.gh");
  literal(tools.git, "/usr/bin/git", "tools.git");
  literal(config.projectGate, "./scripts/package.sh", "projectGate");

  const deliveryRecord = record(config.delivery, "delivery");
  exactKeys(deliveryRecord, "delivery", ["enabled"]);
  const delivery = {
    enabled: boolean(deliveryRecord.enabled, "delivery.enabled"),
  };
  const mergeRecord = record(config.merge, "merge");
  exactKeys(mergeRecord, "merge", [
    "enabled",
    "method",
    "requiredChecks",
  ]);
  if (!Array.isArray(mergeRecord.requiredChecks) ||
      !mergeRecord.requiredChecks.every((item) =>
        typeof item === "string" && item.length > 0 && item.trim() === item
      ) || new Set(mergeRecord.requiredChecks).size !== mergeRecord.requiredChecks.length) {
    throw new Error("merge.requiredChecks must contain unique non-empty check names.");
  }
  const merge = {
    enabled: boolean(mergeRecord.enabled, "merge.enabled"),
    method: literal(mergeRecord.method, "SQUASH", "merge.method"),
    requiredChecks: [...mergeRecord.requiredChecks] as string[],
  };
  if (merge.enabled && !delivery.enabled) {
    throw new Error("merge.enabled requires delivery.enabled.");
  }
  if (merge.enabled && merge.requiredChecks.length === 0) {
    throw new Error("merge.enabled requires explicit merge.requiredChecks.");
  }
  const comments = record(config.comments, "comments");
  exactKeys(comments, "comments", ["maxUtf8Bytes"]);
  if (
    !Number.isSafeInteger(comments.maxUtf8Bytes) ||
    Number(comments.maxUtf8Bytes) < 256 ||
    Number(comments.maxUtf8Bytes) > 4096
  ) {
    throw new Error("comments.maxUtf8Bytes must be a safe integer from 256 through 4096.");
  }
  const stateNamespace = nonEmpty(config.stateNamespace, "stateNamespace");
  if (!/^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(stateNamespace)) {
    throw new Error("stateNamespace must be a safe lowercase directory name.");
  }

  return {
    schemaVersion: 1,
    repository: {
      host: "github.com",
      nameWithOwner,
      nodeId: repository.nodeId as string,
      owner,
      remote: "origin",
      fetchUrl: expectedUrl,
      pushUrl: expectedUrl,
      baseRef,
    },
    trustedActor,
    deliveryActor,
    queueLabel: config.queueLabel as string,
    branchPrefix: "sandcastle/issue-",
    tools: {
      gh: tools.gh as string,
      git: tools.git as string,
    },
    projectGate: "./scripts/package.sh",
    delivery,
    merge,
    comments: { maxUtf8Bytes: comments.maxUtf8Bytes as number },
    stateNamespace,
  };
}

function actor(value: unknown, name: string): ActorIdentity {
  const candidate = record(value, name);
  exactKeys(candidate, name, ["login", "nodeId"]);
  return {
    login: nonEmpty(candidate.login, `${name}.login`),
    nodeId: nonEmpty(candidate.nodeId, `${name}.nodeId`),
  };
}

function record(value: unknown, name: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function exactKeys(
  value: Record<string, unknown>,
  name: string,
  expected: readonly string[],
): void {
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(wanted)) {
    throw new Error(`${name} has missing or unknown fields.`);
  }
}

function nonEmpty(value: unknown, name: string): string {
  if (typeof value !== "string" || value.length === 0 || value.trim() !== value) {
    throw new Error(`${name} must be a non-empty, unpadded string.`);
  }
  return value;
}

function boolean(value: unknown, name: string): boolean {
  if (typeof value !== "boolean") throw new Error(`${name} must be a boolean.`);
  return value;
}

function literal<const T extends string | number>(
  value: unknown,
  expected: T,
  name: string,
): T {
  if (value !== expected) throw new Error(`${name} must be ${JSON.stringify(expected)}.`);
  return expected;
}
