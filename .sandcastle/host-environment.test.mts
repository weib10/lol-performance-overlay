import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import test from "node:test";

import {
  FORBIDDEN_HOST_ENVIRONMENT_KEYS,
  sanitizeHostEnvironment,
} from "./host-environment.mts";

test("sanitized host environment removes credential, routing, and config overrides in a real child", () => {
  const source: Record<string, string | undefined> = {
    PATH: process.env.PATH,
    LANG: "C.UTF-8",
    SAFE_MARKER: "preserved",
    HOME: "/tmp/untrusted-home",
  };
  for (const key of FORBIDDEN_HOST_ENVIRONMENT_KEYS) {
    source[key] = "must-not-reach-child";
  }
  const futureCopilotSecretKey = ["COPILOT", "FUTURE", "SECRET"].join("_");
  source.GITHUB_FUTURE_PRIVATE_KEY = "future-secret";
  source.GH_FUTURE_TOKEN = "future-token";
  source[futureCopilotSecretKey] = ["future", "copilot", "value"].join("-");
  source.GH_FUTURE_ROUTER = "enterprise.invalid";
  source.GITHUB_FUTURE_CONFIG = "/hostile/github-config";
  source.COPILOT_FUTURE_ROUTE = "hostile";
  source.GIT_CONFIG_KEY_0 = "url.example.invalid.insteadOf";
  source.GIT_CONFIG_VALUE_0 = "https://github.com/";
  source.GIT_TRACE = "1";
  source.OPENAI_PROJECT = "must-not-reach-child";
  source.HTTPS_PROXY = ["https:/", "/intercept.invalid"].join("");
  source.http_proxy = ["http:/", "/intercept.invalid"].join("");
  source.ALL_PROXY = ["socks5:/", "/intercept.invalid"].join("");
  source.CURL_CA_BUNDLE = "/untrusted/ca.pem";
  source.SSL_CERT_FILE = "/untrusted/cert.pem";
  source.LD_PRELOAD = "/untrusted/interpose.so";
  source.LD_LIBRARY_PATH = "/untrusted/lib";
  source.LD_AUDIT = "/untrusted/audit.so";
  source.DYLD_INSERT_LIBRARIES = "/untrusted/interpose.dylib";

  const sanitized = sanitizeHostEnvironment(source, "/trusted/os/home");
  const child = JSON.parse(
    execFileSync(
      process.execPath,
      ["-e", "process.stdout.write(JSON.stringify(process.env))"],
      { encoding: "utf8", env: sanitized },
    ),
  ) as Record<string, string | undefined>;

  assert.equal(child.HOME, "/trusted/os/home");
  assert.equal(child.SAFE_MARKER, "preserved");
  assert.equal(child.LANG, "C.UTF-8");
  assert.equal(child.GIT_NO_REPLACE_OBJECTS, "1");
  for (const key of FORBIDDEN_HOST_ENVIRONMENT_KEYS) {
    assert.equal(child[key], undefined, key);
  }
  assert.equal(child.GITHUB_FUTURE_PRIVATE_KEY, undefined);
  assert.equal(child.GH_FUTURE_TOKEN, undefined);
  assert.equal(child[futureCopilotSecretKey], undefined);
  assert.equal(child.GH_FUTURE_ROUTER, undefined);
  assert.equal(child.GITHUB_FUTURE_CONFIG, undefined);
  assert.equal(child.COPILOT_FUTURE_ROUTE, undefined);
  assert.equal(child.GIT_CONFIG_KEY_0, undefined);
  assert.equal(child.GIT_CONFIG_VALUE_0, undefined);
  assert.equal(child.GIT_TRACE, undefined);
  assert.equal(child.OPENAI_PROJECT, undefined);
  assert.equal(source.HOME, "/tmp/untrusted-home");
  assert.equal(source.GH_TOKEN, "must-not-reach-child");
});
