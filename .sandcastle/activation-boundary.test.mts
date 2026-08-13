import assert from "node:assert/strict";
import test from "node:test";

import { assertOwnerControlledCheckout } from "./activation-boundary.mts";

const BASE = "a".repeat(40);

test("active delivery requires the clean host checkout to be the exact owner base", () => {
  assert.doesNotThrow(() => assertOwnerControlledCheckout({
    localBranch: "main",
    localHeadSha: BASE,
    expectedBaseRef: "main",
    githubBaseSha: BASE,
  }));
  assert.throws(() => assertOwnerControlledCheckout({
    localBranch: "collaborator/enabled",
    localHeadSha: BASE,
    expectedBaseRef: "main",
    githubBaseSha: BASE,
  }), /owner-controlled main branch/);
  assert.throws(() => assertOwnerControlledCheckout({
    localBranch: "main",
    localHeadSha: "b".repeat(40),
    expectedBaseRef: "main",
    githubBaseSha: BASE,
  }), /exactly equal.*GitHub base SHA/);
});
