export const FORBIDDEN_HOST_ENVIRONMENT_KEYS = [
  "GH_TOKEN",
  "GITHUB_TOKEN",
  "GH_ENTERPRISE_TOKEN",
  "GITHUB_ENTERPRISE_TOKEN",
  "COPILOT_GITHUB_TOKEN",
  "GITHUB_APP_ID",
  "GITHUB_APP_PRIVATE_KEY",
  "GITHUB_APP_INSTALLATION_ID",
  "GITHUB_APP_CLIENT_ID",
  "GITHUB_APP_CLIENT_SECRET",
  "GH_HOST",
  "GH_REPO",
  "GH_CONFIG_DIR",
  "GH_PATH",
  "XDG_CONFIG_HOME",
  "GITHUB_SERVER_URL",
  "GITHUB_API_URL",
  "GITHUB_GRAPHQL_URL",
  "GITHUB_REPOSITORY",
  "GITHUB_ACTOR",
  "GITHUB_ACTOR_ID",
  "GITHUB_TRIGGERING_ACTOR",
  "GITHUB_REPOSITORY_ID",
  "GITHUB_REPOSITORY_OWNER",
  "GITHUB_REPOSITORY_OWNER_ID",
  "GIT_DIR",
  "GIT_WORK_TREE",
  "GIT_COMMON_DIR",
  "GIT_INDEX_FILE",
  "GIT_OBJECT_DIRECTORY",
  "GIT_ALTERNATE_OBJECT_DIRECTORIES",
  "GIT_CONFIG",
  "GIT_CONFIG_GLOBAL",
  "GIT_CONFIG_SYSTEM",
  "GIT_CONFIG_NOSYSTEM",
  "GIT_CONFIG_COUNT",
  "GIT_ASKPASS",
  "SSH_ASKPASS",
  "GIT_SSH",
  "GIT_SSH_COMMAND",
  "CODEX_ACCESS_TOKEN",
  "OPENAI_API_KEY",
  "HTTP_PROXY",
  "HTTPS_PROXY",
  "ALL_PROXY",
  "NO_PROXY",
  "http_proxy",
  "https_proxy",
  "all_proxy",
  "no_proxy",
  "SSL_CERT_FILE",
  "SSL_CERT_DIR",
  "CURL_CA_BUNDLE",
  "GIT_SSL_CAINFO",
  "GIT_SSL_CAPATH",
  "LD_PRELOAD",
  "LD_LIBRARY_PATH",
  "DYLD_INSERT_LIBRARIES",
  "DYLD_LIBRARY_PATH",
  "DYLD_FRAMEWORK_PATH",
  "DYLD_FALLBACK_LIBRARY_PATH",
  "DYLD_FALLBACK_FRAMEWORK_PATH",
] as const;

const FORBIDDEN_EXACT_KEYS = new Set<string>(FORBIDDEN_HOST_ENVIRONMENT_KEYS);

function isForbiddenHostKey(key: string): boolean {
  if (FORBIDDEN_EXACT_KEYS.has(key)) return true;
  // Do not let a future gh/git/Codex environment knob silently weaken the
  // trusted-host boundary. Controlled noninteractive values are added below.
  return /^(?:GH|GITHUB|COPILOT|GIT|CODEX|OPENAI)_/.test(key) ||
    /^(?:LD_|DYLD_|_RLD)/.test(key);
}

export function sanitizeHostEnvironment(
  source: Record<string, string | undefined>,
  osHome: string,
): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(source)) {
    if (value === undefined || isForbiddenHostKey(key)) continue;
    result[key] = value;
  }
  result.HOME = osHome;
  result.GH_PROMPT_DISABLED = "1";
  result.GH_NO_UPDATE_NOTIFIER = "1";
  result.GIT_TERMINAL_PROMPT = "0";
  result.GIT_NO_REPLACE_OBJECTS = "1";
  return result;
}

export function assertNoForbiddenHostEnvironment(content: string): void {
  const withoutBom = content.replace(/^\uFEFF/, "");
  for (const line of withoutBom.split(/\r?\n/)) {
    const trimmed = line.trim().replace(/^export\s+/, "");
    if (!trimmed || trimmed.startsWith("#")) continue;
    const equals = trimmed.indexOf("=");
    if (equals < 0) continue;
    const key = trimmed.slice(0, equals).trim();
    if (isForbiddenHostKey(key)) {
      throw new Error(
        `.sandcastle/.env must not declare ${key}; host routing and credentials stay outside the sandbox.`,
      );
    }
  }
}
