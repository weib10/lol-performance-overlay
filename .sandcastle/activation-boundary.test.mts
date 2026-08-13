import assert from "node:assert/strict";
import test from "node:test";

import { assertOwnerControlledCheckout } from "./activation-boundary.mts";

const BASE = "a".repeat(40);

test("active delivery requires the clean host checkout to be the exact owner base", () => {
  assert.doesNotThrow(() => assertOwnerControlledCheckout({
    localBranch: "agent/linux-usability-release",
    localHeadSha: BASE,
    expectedBaseRef: "agent/linux-usability-release",
    githubBaseSha: BASE,
  }));
  assert.throws(() => assertOwnerControlledCheckout({
    localBranch: "collaborator/enabled",
    localHeadSha: BASE,
    expectedBaseRef: "agent/linux-usability-release",
    githubBaseSha: BASE,
  }), /owner-controlled agent\/linux-usability-release branch/);
  assert.throws(() => assertOwnerControlledCheckout({
    localBranch: "agent/linux-usability-release",
    localHeadSha: "b".repeat(40),
    expectedBaseRef: "agent/linux-usability-release",
    githubBaseSha: BASE,
  }), /exactly equal.*GitHub base SHA/);
});
