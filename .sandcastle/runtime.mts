import { lstat, readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

import { codex } from "@ai-hero/sandcastle";
import { docker } from "@ai-hero/sandcastle/sandboxes/docker";

import { protectGitControlPlane } from "./git-control-plane.mts";
import { assertNoForbiddenHostEnvironment } from "./host-environment.mts";

export const IMAGE_NAME =
  "lol-performance-overlay-sandcastle:sc0.12.0-codex0.147.0-dotnet8.0.423";
export const COMPLETION_SIGNAL = "<promise>COMPLETE</promise>";
export type CodexCredentialPlacement = "agent" | "sandbox";

export interface DockerSandboxBindings {
  makeDocker(options: {
    imageName: string;
    cpus: number;
    env: Record<string, string>;
    mounts: Array<{ hostPath: string; sandboxPath: string }>;
  }): any;
  protect<T>(provider: T): T;
}

const defaultDockerSandboxBindings: DockerSandboxBindings = {
  makeDocker: docker,
  protect: protectGitControlPlane,
};

export function assertNoGithubCredentialEnv(content: string): void {
  assertNoForbiddenHostEnvironment(content);
}

async function assertSandboxEnvHasNoGithubCredentials(): Promise<void> {
  const envPath = join(process.cwd(), ".sandcastle", ".env");
  let content: string;
  try {
    content = await readFile(envPath, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
    throw error;
  }
  assertNoGithubCredentialEnv(content);
}

async function currentCodexAuthPath(): Promise<string | undefined> {
  if (process.env.CODEX_ACCESS_TOKEN) return undefined;
  // Do not let an ambient CODEX_HOME redirect the privileged host mount to an
  // arbitrary file tree. The only fallback is the OS account's one auth file.
  const codexHome = join(homedir(), ".codex");
  const authPath = join(codexHome, "auth.json");
  const metadata = await lstat(authPath);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    throw new Error(`${authPath} must be a regular, non-symlink credential file.`);
  }
  if ((metadata.mode & 0o077) !== 0) {
    throw new Error(`${authPath} must not be readable by group or other users.`);
  }

  return authPath;
}

export async function codexAgent(
  effort: "low" | "high" = "high",
  credentialPlacement: CodexCredentialPlacement = "agent",
) {
  return codex("gpt-5.4", {
    effort,
    captureSessions: false,
    env: credentialPlacement === "agent" && process.env.CODEX_ACCESS_TOKEN
      ? { CODEX_ACCESS_TOKEN: process.env.CODEX_ACCESS_TOKEN }
      : {},
  });
}

export async function dockerSandbox(
  credentialPlacement: CodexCredentialPlacement = "agent",
  bindings: DockerSandboxBindings = defaultDockerSandboxBindings,
) {
  await assertSandboxEnvHasNoGithubCredentials();
  const authPath = await currentCodexAuthPath();
  const sandboxEnvironment =
    credentialPlacement === "sandbox" && process.env.CODEX_ACCESS_TOKEN
      ? { CODEX_ACCESS_TOKEN: process.env.CODEX_ACCESS_TOKEN }
      : {};
  return bindings.protect(bindings.makeDocker({
    imageName: IMAGE_NAME,
    cpus: 2,
    env: sandboxEnvironment,
    mounts: authPath
      ? [
          {
            hostPath: authPath,
            sandboxPath: "/home/agent/.codex/auth.json",
          },
        ]
      : [],
  }));
}
