import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { chmod, mkdir, readFile, realpath, rename, writeFile } from "node:fs/promises";
import { userInfo } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

import { sanitizeHostEnvironment } from "./host-environment.mts";
import { createHostGit } from "./host-git.mts";
import { loadProjectConfig, type SandcastleProjectConfig } from "./project-config.mts";
import { IMAGE_NAME } from "./runtime.mts";

const RUNTIME_INPUTS = ["package.json", "package-lock.json", ".sandcastle/Dockerfile"] as const;

export function planWorkerNpmArguments(config: SandcastleProjectConfig): string[] {
  if (!config.delivery.enabled) return ["run", "sandcastle"];
  const args = ["run", "sandcastle", "--", "--allow-delivery"];
  if (config.merge.enabled) args.push("--allow-merge");
  return args;
}

async function main(): Promise<void> {
  const root = await realpath(process.cwd());
  const osHome = userInfo().homedir;
  const initialConfig = await loadProjectConfig();
  const git = createHostGit({
    root,
    gitPath: initialConfig.tools.git,
    remote: initialConfig.repository.remote,
    expectedFetchUrl: initialConfig.repository.fetchUrl,
    expectedPushUrl: initialConfig.repository.pushUrl,
    osHome,
  });
  const synchronization = await git.synchronizeBase(initialConfig.repository.baseRef);
  const config = await loadProjectConfig();
  if (
    config.repository.nameWithOwner !== initialConfig.repository.nameWithOwner ||
    config.repository.nodeId !== initialConfig.repository.nodeId ||
    config.repository.baseRef !== initialConfig.repository.baseRef
  ) {
    throw new Error("Repository identity or configured base changed during synchronization.");
  }
  const environment = sanitizeHostEnvironment(process.env, osHome);
  await prepareRuntime({ root, osHome, environment, config });
  console.log(
    `LoL Sandcastle base: ${synchronization.changed ? "fast-forwarded" : "current"} at ${synchronization.afterSha}`,
  );
  execFileSync("npm", planWorkerNpmArguments(config), {
    cwd: root,
    env: environment,
    stdio: "inherit",
  });
}

async function prepareRuntime(input: {
  root: string;
  osHome: string;
  environment: Record<string, string>;
  config: SandcastleProjectConfig;
}): Promise<void> {
  const signature = await runtimeSignature(input.root);
  const stateDirectory = join(input.osHome, ".local", "state", input.config.stateNamespace);
  const markerPath = join(stateDirectory, "host-runtime.json");
  if (
    (await readRuntimeMarker(markerPath)) === signature &&
    commandSucceeds("npm", ["ls", "--depth=0"], input) &&
    commandSucceeds("docker", ["image", "inspect", IMAGE_NAME], input)
  ) return;

  execFileSync("npm", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], {
    cwd: input.root,
    env: input.environment,
    stdio: "inherit",
  });
  execFileSync("npm", ["run", "sandcastle:build"], {
    cwd: input.root,
    env: input.environment,
    stdio: "inherit",
  });
  execFileSync("npm", ["run", "sandcastle:verify"], {
    cwd: input.root,
    env: input.environment,
    stdio: "inherit",
  });
  await mkdir(stateDirectory, { recursive: true, mode: 0o700 });
  await chmod(stateDirectory, 0o700);
  const temporary = `${markerPath}.tmp.${process.pid}`;
  await writeFile(temporary, `${JSON.stringify({ schemaVersion: 1, signature })}\n`, {
    encoding: "utf8",
    mode: 0o600,
  });
  await rename(temporary, markerPath);
  await chmod(markerPath, 0o600);
}

function commandSucceeds(
  command: string,
  args: string[],
  input: { root: string; environment: Record<string, string> },
): boolean {
  try {
    execFileSync(command, args, { cwd: input.root, env: input.environment, stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

async function runtimeSignature(root: string): Promise<string> {
  const hash = createHash("sha256");
  for (const path of RUNTIME_INPUTS) {
    hash.update(path);
    hash.update("\0");
    hash.update(await readFile(join(root, path)));
    hash.update("\0");
  }
  return hash.digest("hex");
}

async function readRuntimeMarker(path: string): Promise<string | undefined> {
  try {
    const parsed = JSON.parse(await readFile(path, "utf8")) as {
      schemaVersion?: unknown;
      signature?: unknown;
    };
    if (
      parsed.schemaVersion === 1 &&
      typeof parsed.signature === "string" &&
      /^[0-9a-f]{64}$/.test(parsed.signature)
    ) return parsed.signature;
  } catch {
    // Missing or malformed state forces a verified runtime refresh.
  }
  return undefined;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await main();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error(`LoL Sandcastle host poll failed: ${message}`);
    process.exitCode = 1;
  }
}
