import assert from "node:assert/strict";
import test from "node:test";

import { FORBIDDEN_HOST_ENVIRONMENT_KEYS } from "./host-environment.mts";
import {
  assertNoGithubCredentialEnv,
  codexAgent,
  dockerSandbox,
} from "./runtime.mts";

const ACCESS_TOKEN_KEY = ["CODEX", "ACCESS", "TOKEN"].join("_");
const ACCESS_TOKEN_VALUE = ["minimum", "test", "token"].join("-");

test("sandbox env rejects every forbidden host override without inspecting values", () => {
  for (const key of FORBIDDEN_HOST_ENVIRONMENT_KEYS) {
    assert.throws(
      () => assertNoGithubCredentialEnv(`${key}=anything`),
      new RegExp(key),
    );
  }
  for (const key of [
    "GH_FUTURE_ROUTER",
    "GITHUB_FUTURE_CONFIG",
    "COPILOT_FUTURE_ROUTE",
    "GIT_FUTURE_CONFIG",
    "CODEX_FUTURE_TOKEN",
    "OPENAI_FUTURE_PROJECT",
  ]) {
    assert.throws(() => assertNoGithubCredentialEnv(`${key}=anything`));
  }
  assert.throws(
    () => assertNoGithubCredentialEnv("\uFEFFexport GH_TOKEN=anything"),
    /GH_TOKEN/,
  );
});

test("sandbox env permits comments and unrelated declarations", () => {
  assert.doesNotThrow(() =>
    assertNoGithubCredentialEnv(
      "# GH_TOKEN is intentionally host-only\nUNRELATED_SETTING=value\n",
    ),
  );
});

test("reusable sandbox places the access token before provider creation and nowhere in its agent", async () => {
  const previous = process.env[ACCESS_TOKEN_KEY];
  process.env[ACCESS_TOKEN_KEY] = ACCESS_TOKEN_VALUE;
  let providerCreateEnvironment: Record<string, string> | undefined;
  try {
    const provider = await dockerSandbox("sandbox", {
      makeDocker(options) {
        return {
          tag: "bind-mount" as const,
          name: "fake-docker",
          env: options.env,
          async create(createOptions: { env: Record<string, string> }) {
            providerCreateEnvironment = createOptions.env;
            return {};
          },
        };
      },
      protect(value) {
        return value;
      },
    });
    await provider.create({ env: { ...provider.env } });
    const agent = await codexAgent("high", "sandbox");

    assert.equal(providerCreateEnvironment?.[ACCESS_TOKEN_KEY], ACCESS_TOKEN_VALUE);
    assert.equal(agent.env[ACCESS_TOKEN_KEY], undefined);
  } finally {
    restoreToken(previous);
  }
});

test("one-shot mode keeps the token on the agent and out of provider creation", async () => {
  const previous = process.env[ACCESS_TOKEN_KEY];
  process.env[ACCESS_TOKEN_KEY] = ACCESS_TOKEN_VALUE;
  let providerCreateEnvironment: Record<string, string> | undefined;
  try {
    const provider = await dockerSandbox("agent", {
      makeDocker(options) {
        return {
          tag: "bind-mount" as const,
          name: "fake-docker",
          env: options.env,
          async create(createOptions: { env: Record<string, string> }) {
            providerCreateEnvironment = createOptions.env;
            return {};
          },
        };
      },
      protect(value) {
        return value;
      },
    });
    await provider.create({ env: { ...provider.env } });
    const agent = await codexAgent("low", "agent");

    assert.equal(providerCreateEnvironment?.[ACCESS_TOKEN_KEY], undefined);
    assert.equal(agent.env[ACCESS_TOKEN_KEY], ACCESS_TOKEN_VALUE);
  } finally {
    restoreToken(previous);
  }
});

function restoreToken(previous: string | undefined): void {
  if (previous === undefined) delete process.env[ACCESS_TOKEN_KEY];
  else process.env[ACCESS_TOKEN_KEY] = previous;
}
