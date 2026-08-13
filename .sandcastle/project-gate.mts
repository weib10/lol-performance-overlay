import { execFileSync } from "node:child_process";
import { realpath, rm } from "node:fs/promises";
import { homedir } from "node:os";

import { sanitizeHostEnvironment } from "./host-environment.mts";
import { IMAGE_NAME } from "./runtime.mts";
import { createDisposableGitSnapshot } from "./smoke-snapshot.mts";

const sourceRepository = await realpath(process.cwd());
let snapshotRepository: string | undefined;

try {
  snapshotRepository = await createDisposableGitSnapshot(sourceRepository);
  execFileSync(
    "/usr/bin/docker",
    [
      "run",
      "--rm",
      "--name",
      `lol-overlay-sandcastle-gate-${process.pid}`,
      "--volume",
      `${snapshotRepository}:/home/agent/workspace`,
      "--workdir",
      "/home/agent/workspace",
      "--entrypoint",
      "/bin/sh",
      IMAGE_NAME,
      "-lc",
      "./scripts/package.sh",
    ],
    {
      cwd: sourceRepository,
      env: sanitizeHostEnvironment(process.env, homedir()),
      stdio: "inherit",
    },
  );
  console.log("Sandcastle disposable project gate: PASS");
} finally {
  if (snapshotRepository) {
    await rm(snapshotRepository, { recursive: true, force: true });
  }
}
