import { assertExactCommitSha } from "./exact-commit-sha.mts";

/**
 * Bind delivery authority to the configured base branch as GitHub currently
 * reports it, so a local-only commit cannot activate delivery.
 *
 * This is not proof of owner authorship. Nothing here identifies who wrote the
 * base commit, and host-poll fast-forwards the checkout to whatever the base ref
 * points at before reading `delivery.enabled` from it. Anyone who can push to
 * the base ref therefore controls both the code that runs and that boolean, so
 * the base ref must be protected on GitHub before delivery is enabled. The
 * worker cannot verify that itself: reading protection rules needs admin, and
 * the delivery actor holds exact WRITE. See README "啟用前的前提".
 */
export function assertOwnerControlledCheckout(input: {
  localBranch: string;
  localHeadSha: string;
  expectedBaseRef: string;
  githubBaseSha: string;
}): void {
  assertExactCommitSha(input.localHeadSha, "trusted host HEAD");
  assertExactCommitSha(input.githubBaseSha, "immutable GitHub base SHA");
  if (input.localBranch !== input.expectedBaseRef) {
    throw new Error(
      `Active Sandcastle delivery must run from the owner-controlled ${input.expectedBaseRef} branch.`,
    );
  }
  if (input.localHeadSha !== input.githubBaseSha) {
    throw new Error(
      "Trusted host HEAD must exactly equal the immutable GitHub base SHA; a collaborator-only commit cannot activate delivery.",
    );
  }
}
