const EXACT_COMMIT_SHA_PATTERN = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/;

/**
 * Accept only a full lowercase Git object ID. The all-zero value is a ref
 * deletion sentinel, not a commit identity, so it is never valid here.
 */
export function assertExactCommitSha(
  value: unknown,
  label = "commit SHA",
): asserts value is string {
  if (typeof value !== "string" ||
      !EXACT_COMMIT_SHA_PATTERN.test(value) ||
      /^0+$/.test(value)) {
    throw new Error(`${label} must be an exact full lowercase commit SHA.`);
  }
}
