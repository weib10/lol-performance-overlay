import assert from "node:assert/strict";
import test from "node:test";

import {
  loadProjectConfig,
  parseProjectConfig,
  type SandcastleProjectConfig,
} from "./project-config.mts";

test("checked-in config pins the deployment branch and keeps owner-gated delivery inert", async () => {
  const config = await loadProjectConfig();
  assert.deepEqual(config.repository, {
    host: "github.com",
    nameWithOwner: "weib10/lol-performance-overlay",
    nodeId: "R_kgDOTnQLIg",
    owner: { login: "weib10", nodeId: "U_kgDOBZXTGw" },
    remote: "origin",
    fetchUrl: "https://github.com/weib10/lol-performance-overlay.git",
    pushUrl: "https://github.com/weib10/lol-performance-overlay.git",
    baseRef: "agent/linux-usability-release",
  });
  assert.deepEqual(config.trustedActor, {
    login: "weib10",
    nodeId: "U_kgDOBZXTGw",
  });
  assert.deepEqual(config.deliveryActor, {
    login: "brant92good",
    nodeId: "MDQ6VXNlcjc2ODg0MTc3",
    minimumPermission: "WRITE",
  });
  assert.equal(config.delivery.enabled, false);
  assert.equal(config.merge.enabled, false);
  assert.deepEqual(config.merge.requiredChecks, []);
  assert.equal(config.projectGate, "./scripts/package.sh");
});

test("parser rejects repository URL drift and unknown fields", async () => {
  const config = await loadProjectConfig();
  assert.throws(
    () => parseProjectConfig({
      ...config,
      repository: { ...config.repository, pushUrl: "https://github.com/example/wrong.git" },
    }),
    /repository\.pushUrl/,
  );
  assert.throws(
    () => parseProjectConfig({ ...config, typoEnabled: true }),
    /missing or unknown fields/,
  );
  assert.throws(
    () => parseProjectConfig({
      ...config,
      tools: { ...config.tools, gh: "/tmp/collaborator-gh" },
    }),
    /tools\.gh must be "\/usr\/bin\/gh"/,
  );
  assert.throws(
    () => parseProjectConfig({
      ...config,
      repository: { ...config.repository, baseRef: "../other" },
    }),
    /safe literal branch name/,
  );
  assert.throws(
    () => parseProjectConfig({ ...config, stateNamespace: "../shared" }),
    /safe lowercase directory name/,
  );
  assert.throws(
    () => parseProjectConfig({
      ...config,
      comments: { maxUtf8Bytes: 10_000 },
    }),
    /256 through 4096/,
  );
});

test("owner can explicitly enable delivery and merge, but merge cannot stand alone", async () => {
  const config = await loadProjectConfig();
  const enabled: SandcastleProjectConfig = {
    ...config,
    delivery: { enabled: true },
    merge: {
      enabled: true,
      method: "SQUASH",
      requiredChecks: ["package"],
    },
  };
  assert.equal(parseProjectConfig(enabled).merge.enabled, true);
  assert.throws(
    () => parseProjectConfig({
      ...enabled,
      delivery: { enabled: false },
    }),
    /merge\.enabled requires delivery\.enabled/,
  );
  assert.throws(
    () => parseProjectConfig({
      ...enabled,
      merge: {
        enabled: true,
        method: "SQUASH",
        requiredChecks: [],
      },
    }),
    /requires explicit merge\.requiredChecks/,
  );
});
