import { assertExactCommitSha } from "./exact-commit-sha.mts";

/**
 * Delivery authority must come from the owner-controlled base checkout, not
 * from a boolean or SHA copied into an arbitrary collaborator commit.
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
