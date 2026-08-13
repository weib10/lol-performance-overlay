import type { SandcastleProjectConfig } from "./project-config.mts";

export interface WorkerCliOptions {
  issueNumber?: number;
  label?: string;
  allowDelivery: boolean;
  allowMerge: boolean;
}

export interface ActivationDecision {
  active: boolean;
  allowMerge: boolean;
  reason?: string;
}

export function parseWorkerCli(args: readonly string[]): WorkerCliOptions {
  let issueNumber: number | undefined;
  let label: string | undefined;
  let allowDelivery = false;
  let allowMerge = false;

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]!;
    if (argument === "--issue") {
      const value = args[++index];
      if (!value) throw new Error("--issue requires a positive Issue number.");
      issueNumber = positiveInteger(value, "--issue");
    } else if (argument.startsWith("--issue=")) {
      issueNumber = positiveInteger(argument.slice("--issue=".length), "--issue");
    } else if (argument === "--label") {
      const value = args[++index];
      if (!value?.trim()) throw new Error("--label requires a non-empty value.");
      label = value.trim();
    } else if (argument.startsWith("--label=")) {
      label = argument.slice("--label=".length).trim();
      if (!label) throw new Error("--label requires a non-empty value.");
    } else if (argument === "--allow-delivery") {
      allowDelivery = true;
    } else if (argument === "--allow-merge") {
      allowMerge = true;
    } else {
      throw new Error(`Unknown Sandcastle option: ${argument}`);
    }
  }
  if (allowMerge && !allowDelivery) {
    throw new Error("--allow-merge requires --allow-delivery in the same invocation.");
  }
  return { issueNumber, label, allowDelivery, allowMerge };
}

export function decideActivation(
  config: SandcastleProjectConfig,
  options: WorkerCliOptions,
): ActivationDecision {
  if (!options.allowDelivery) {
    return {
      active: false,
      allowMerge: false,
      reason: "Host delivery is inert until --allow-delivery is supplied.",
    };
  }
  if (!config.delivery.enabled) {
    throw new Error(
      "Host delivery is disabled in checked-in project.json; repository-owner authorization is required before this runtime flag can take effect.",
    );
  }
  if (options.allowMerge && !config.merge.enabled) {
    throw new Error(
      "Merge is disabled in checked-in project.json; --allow-merge cannot enable it by itself.",
    );
  }
  return {
    active: true,
    allowMerge: config.merge.enabled && options.allowMerge,
  };
}

function positiveInteger(value: string, option: string): number {
  if (!/^[1-9][0-9]*$/.test(value)) {
    throw new Error(`${option} requires a positive Issue number.`);
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`${option} is outside the safe integer range.`);
  }
  return parsed;
}
