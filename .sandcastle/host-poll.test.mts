import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import { planWorkerNpmArguments } from "./host-poll.mts";
import { loadProjectConfig } from "./project-config.mts";

test("host poll translates checked-in activation into both runtime keys", async () => {
  const config = await loadProjectConfig();
  assert.deepEqual(planWorkerNpmArguments(config), ["run", "sandcastle"]);
  assert.deepEqual(planWorkerNpmArguments({
    ...config,
    delivery: { enabled: true },
    merge: { ...config.merge, enabled: true, requiredChecks: ["package"] },
  }), [
    "run",
    "sandcastle",
    "--",
    "--allow-delivery",
    "--allow-merge",
  ]);
});

test("systemd deployment continuously polls a dedicated clean checkout", async () => {
  const [service, timer, installer] = await Promise.all([
    readFile(new URL("./systemd/lol-performance-overlay-sandcastle.service", import.meta.url), "utf8"),
    readFile(new URL("./systemd/lol-performance-overlay-sandcastle.timer", import.meta.url), "utf8"),
    readFile(new URL("./install-host-service.sh", import.meta.url), "utf8"),
  ]);

  assert.match(service, /\.local\/share\/lol-performance-overlay-sandcastle\/repository/);
  assert.match(service, /npm run sandcastle:host-poll/);
  assert.match(service, /UnsetEnvironment=.*GH_TOKEN/);
  assert.match(service, /UnsetEnvironment=.*GITHUB_APP_PRIVATE_KEY/);
  assert.match(service, /UMask=0077/);
  assert.match(timer, /OnUnitInactiveSec=2min/);
  assert.match(timer, /Persistent=true/);
  assert.match(installer, /\/usr\/bin\/git[\s\S]*clone --branch/);
  assert.match(installer, /enable --now lol-performance-overlay-sandcastle\.timer/);
});

test("workflow runs the package check for Issue PRs targeting the deployed base", async () => {
  const workflow = await readFile(
    new URL("../.github/workflows/windows-package.yml", import.meta.url),
    "utf8",
  );
  assert.match(
    workflow,
    /pull_request:\s*\n\s*branches:\s*\n(?:\s*- .+\n)*\s*- agent\/linux-usability-release/m,
  );
});
