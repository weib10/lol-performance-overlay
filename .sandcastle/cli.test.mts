import assert from "node:assert/strict";
import test from "node:test";

import { decideActivation, parseWorkerCli } from "./cli.mts";
import { loadProjectConfig } from "./project-config.mts";

test("CLI accepts Issue selection and requires the delivery key for merge", () => {
  assert.deepEqual(
    parseWorkerCli([
      "--issue=42",
      "--label",
      "Sandcastle",
      "--allow-delivery",
      "--allow-merge",
    ]),
    {
      issueNumber: 42,
      label: "Sandcastle",
      allowDelivery: true,
      allowMerge: true,
    },
  );
  assert.throws(() => parseWorkerCli(["--issue", "0"]), /positive/);
  assert.throws(() => parseWorkerCli(["--allow-merge"]), /requires --allow-delivery/);
  assert.throws(() => parseWorkerCli(["--unknown"]), /Unknown/);
});

test("checked-in unowned-repository config cannot be activated by runtime flags", async () => {
  const config = await loadProjectConfig();
  assert.deepEqual(decideActivation(config, parseWorkerCli([])), {
    active: false,
    allowMerge: false,
    reason: "Host delivery is inert until --allow-delivery is supplied.",
  });
  assert.throws(
    () => decideActivation(config, parseWorkerCli(["--allow-delivery"])),
    /disabled.*repository-owner authorization/i,
  );
  assert.throws(
    () => decideActivation(
      {
        ...config,
        delivery: { enabled: true },
      },
      parseWorkerCli(["--allow-delivery", "--allow-merge"]),
    ),
    /Merge is disabled/,
  );
});

test("delivery and merge each require their checked-in and runtime keys", async () => {
  const config = await loadProjectConfig();
  const enabled = {
    ...config,
    delivery: { enabled: true },
    merge: {
      enabled: true,
      method: "SQUASH" as const,
      requiredChecks: ["package"],
    },
  };
  assert.deepEqual(
    decideActivation(enabled, parseWorkerCli(["--allow-delivery"])),
    { active: true, allowMerge: false },
  );
  assert.deepEqual(
    decideActivation(
      enabled,
      parseWorkerCli(["--allow-delivery", "--allow-merge"]),
    ),
    { active: true, allowMerge: true },
  );
});
