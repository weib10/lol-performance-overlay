# Sandcastle setup research

Checked 2026-08-12 against the public npm registry and the official
[`mattpocock/sandcastle`](https://github.com/mattpocock/sandcastle) repository.
The published npm artifact, not an unreleased branch, is the setup authority.

## Published baseline

- The npm `latest` dist-tag is **`@ai-hero/sandcastle@0.12.0`**, published from
  Git commit `e99f832f26dc9d245c019a9ddd19fa5dee792427`; the tarball integrity is
  `sha512-kdQ414rM8t1QiWeqZ3Klz4KSd0PqQG4bRVuqGpRDUomWhojSZkEAc1tbcEcThVmBEaHkCt8LmYR49vqEPNIoYQ==`.
  Sources: [registry metadata](https://registry.npmjs.org/@ai-hero%2Fsandcastle),
  [v0.12.0 package metadata](https://github.com/mattpocock/sandcastle/blob/v0.12.0/package.json).
- Install the library and the TypeScript runner as exact local development
  dependencies, then commit `package.json` and `package-lock.json`:

  ```bash
  npm install --save-dev --save-exact @ai-hero/sandcastle@0.12.0 tsx@4.23.12
  npm ci
  ```

  Sandcastle's quick start invokes `npx tsx`, but `tsx` is not a runtime
  dependency of the published package; pinning it prevents `npx` from fetching
  a changing version. The current `tsx` package requires Node `>=18`.
  Sources: [Sandcastle quick start](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#quick-start),
  [Sandcastle dependencies](https://github.com/mattpocock/sandcastle/blob/v0.12.0/package.json#L65-L99),
  [tsx registry metadata](https://registry.npmjs.org/tsx/latest).
- Sandcastle itself declares no Node `engines` field. Its official Codex image
  template uses `node:22-bookworm`, and its repository pins npm `10.9.2` for
  development. Therefore Node 22 is the best supported project baseline, but it
  is an upstream convention rather than a declared package minimum. Git and a
  sandbox provider (Docker, Podman, or an isolated provider) are also required.
  Sources: [prerequisites](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#prerequisites),
  [Codex Dockerfile template](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/InitService.ts#L275-L305).
- `@daytona/sdk` and `@vercel/sandbox` are optional peers and are unnecessary
  for the Docker setup.

## Official shape and project-specific choice

The official `sandcastle init` flow scaffolds `.sandcastle/Dockerfile`,
`.sandcastle/main.mts` (or `main.ts` for ESM projects), `prompt.md`,
`.env.example`, and a local `.gitignore` that ignores `.env`, `logs/`, and
`worktrees/`. The prompt is used only when `run()` explicitly names it through
`promptFile`; `.sandcastle/prompt.md` is a convention, not an implicit fallback.
`!` command expressions in a prompt file execute inside the sandbox.
Sources: [init and generated files](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#sandcastle-init),
[prompt resolution](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#prompt-resolution),
[blank template](https://github.com/mattpocock/sandcastle/tree/v0.12.0/src/templates/blank).

For this repository, use the official **sequential-reviewer/simple-loop issue
worker shape** as a dependency-free behavioral port around the published
package. The stock GitHub-Issues scaffold runs `gh` inside Docker, closes
completed issues, uses `merge-to-head`, and assumes generic npm gates. This
project instead keeps GitHub and delivery Git on the trusted host, passes Issue
data through inert `promptArgs`, uses the fixed `sandcastle/issue-N` branch,
runs the repository's `./scripts/package.sh`, and invokes a fresh reviewer on
the same branch. After final-SHA verification, an owner-activated host seam can
non-force push that exact SHA, create or reconcile exactly one draft PR, and
update a bounded harness status comment. Sources: [simple-loop template](https://github.com/mattpocock/sandcastle/tree/v0.12.0/src/templates/simple-loop),
[sequential-reviewer template](https://github.com/mattpocock/sandcastle/tree/v0.12.0/src/templates/sequential-reviewer),
[reusable sandbox API](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#createsandbox--reusable-sandbox).

Keeping GitHub on the host preserves the user's machine-local `gh` requirement
without discarding the official issue-worker harness. The Docker image still
contains no `gh`, no GitHub token placeholder is declared in `.sandcastle/.env`,
and the runtime rejects GitHub/Copilot credential declarations in any local
`.sandcastle/.env` before creating a container. Issue-authored shell-looking text
remains data because Sandcastle protects values inserted through `promptArgs`
from prompt command expansion. Host subprocesses strip credential, application,
routing, configuration and Git repository overrides before using absolute
executables. Public status contains only bounded harness-derived outcomes rather
than agent-authored prose. Checked-in delivery and merge switches are false;
host mutation requires an owner-reviewed config change plus an explicit runtime
flag. Revision comments from the immutable trusted actor do not require a new
label. Merge additionally requires a new byte-exact `/sandcastle approve`,
stable rereads, and a head-SHA compare-and-swap. Issue close, deployment,
release, publishing, branch deletion, force/admin merge and auto-merge remain
outside the worker contract.

The reproducible image should replace the two moving references in the official
template. The Node image's multi-platform index was
`sha256:0557ac14e0d45d02ed563067b82856ca5e7aa3437fa28d98d4350ea9c3d9494a`;
this x64 project pins its resolved `linux/amd64` manifest:

```dockerfile
FROM --platform=linux/amd64 node:22-bookworm@sha256:673fce836d5a9185da33352682bfedb17c174d016370d08616748dff76fda862
# ...official UID/GID alignment and git/curl/jq setup...
RUN npm install --global --omit=dev @openai/codex@0.147.0
```

The index and platform-manifest digests were resolved from the official Node
image on 2026-08-12; Codex `0.147.0` was the npm `latest` release. Record both in the
Dockerfile instead of using the upstream template's moving `node:22-bookworm`
and unversioned `@openai/codex`. Sources: [official template](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/InitService.ts#L275-L305),
[Codex npm metadata](https://registry.npmjs.org/@openai%2Fcodex/latest),
[official Node image](https://hub.docker.com/_/node). The `apt-get update`
repositories still move, so this is reproducible at the base-image, npm package,
and agent-CLI level, not a claim of bit-for-bit Debian package reproducibility.

Build and run through checked-in npm scripts (the exact names may be
project-specific):

```json
{
  "scripts": {
    "sandcastle": "tsx .sandcastle/main.mts",
    "sandcastle:build": "sandcastle docker build-image --image-name lol-performance-overlay-sandcastle:sc0.12.0-codex0.147.0-dotnet8.0.423 --dockerfile .sandcastle/Dockerfile",
    "sandcastle:smoke": "tsx .sandcastle/smoke.mts"
  }
}
```

```bash
npm run sandcastle:build
npm run sandcastle:smoke
```

Version 0.12.0 has CLI commands for `init`, `docker build-image`,
`docker remove-image`, and Podman equivalents; there is no `sandcastle run`
command. A real agent run executes the checked-in TypeScript file with `tsx`.
Source: [CLI source](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/cli.ts#L545-L694).

## Credential and sandbox boundary

- Do **not** put GitHub credentials in `.sandcastle/.env`, mount
  `~/.config/gh`, or mount the home directory. Sandcastle injects only keys
  declared in `.sandcastle/.env` (with `process.env` as a fallback for those
  keys), and ignores the repository-root `.env`; an absent `GH_TOKEN` entry is
  therefore not inherited accidentally. Source: [environment resolver](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/EnvResolver.ts#L49-L73).
- Sandcastle 0.12.0's Codex scaffold says `OPENAI_KEY`, but official OpenAI
  documentation uses `OPENAI_API_KEY`. Do not copy the scaffolded name. Codex
  supports ChatGPT login or API-key login and caches file-backed credentials at
  `~/.codex/auth.json`; OpenAI says to treat that file like a password.
  Sources: [Sandcastle scaffold](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/InitService.ts#L433-L441),
  [official Codex authentication](https://learn.chatgpt.com/docs/auth#openai-authentication),
  [credential storage](https://learn.chatgpt.com/docs/auth#credential-storage).
- Prefer a caller-supplied `CODEX_ACCESS_TOKEN`. For the user's existing
  same-machine ChatGPT login, the real smoke test showed that a copied access
  token can expire and return HTTP 401. The functional official-CLI fallback is
  therefore a read-write mount of the **single** host `~/.codex/auth.json`, so
  Codex can refresh and persist the login. Set `captureSessions: false`; never
  mount all of `~/.codex` or the home directory, which would additionally
  expose settings, sessions, rules, and integration secrets.
- Docker is a bind-mount provider. It mounts the project worktree at
  `/home/agent/workspace` and also mounts Git metadata; for a worktree this
  includes the worktree `.git` file and the parent repository's `.git`
  directory. These internal mounts are writable. Sandcastle's Codex provider
  normally invokes `codex exec --dangerously-bypass-approvals-and-sandbox`, so
  the container/mount boundary is the effective filesystem boundary.
  Sources: [Docker provider](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/sandboxes/docker.ts#L126-L167),
  [Git mounts](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/SandboxFactory.ts#L259-L288),
  [Codex command](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/AgentProvider.ts#L773-L813).
- Docker uses its default bridge network unless `docker({ network: ... })` is
  set. A real hosted Codex run needs OpenAI egress, and Sandcastle has no
  hostname allowlist. Do not attach host devices, the Docker socket, extra
  groups, or cache/home mounts. No GitHub credential means any accidental
  GitHub request remains unauthenticated. Source: [Docker options](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/sandboxes/docker.ts#L37-L123).

## Local no-change and restart qualification contract

Use one iteration, `logging: { type: "stdout" }`, session capture disabled, the
minimal credential mechanism above, and an explicit unique named-branch
strategy. First copy the source repository's current tracked and nonignored
untracked files into an OS temporary directory and commit that working-tree
view as a clean, disposable repository. Pre-create the named smoke branch and
run Sandcastle only after changing into this disposable repository. Docker's
default is `head`, which bind-mounts the active checkout directly; this named
branch instead uses a worktree whose Git metadata also belongs only to the
temporary snapshot.
Sources: [branch defaults](https://github.com/mattpocock/sandcastle/blob/v0.12.0/src/run.ts#L500-L530),
[branch strategies](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#branch-strategies).

The no-change prompt should require the agent to read `AGENTS.md`,
`docs/PRODUCT_HANDOFF.md`, `README.md`, and `SECURITY.md`; inspect only; make no
file, Git, GitHub, dependency, push, or deployment changes; summarize the repo;
and finish with `<promise>COMPLETE</promise>`. The runner must fail unless:

1. `result.iterations.length === 1`;
2. `result.completionSignal === "<promise>COMPLETE</promise>"`;
3. `result.commits` is empty; and
4. no preserved dirty worktree remains;
5. the disposable repository's branch, `HEAD`, complete porcelain status, all
   refs, and worktree list are byte-for-byte identical before and after; and
6. the source repository's same five observations are also byte-for-byte
   identical, proving the smoke did not write its `.git` or working tree.

The runner removes only the disposable temporary directory in `finally`; it
never creates and later deletes a ref in the source repository. Ignored files
such as `.sandcastle/.env` are deliberately absent from the snapshot.

The Issue-worker qualification is local and uses fakes for GitHub mutations.
It must exercise failure after an externally visible effect but before durable
commit, then rerun the same command and prove reconciliation: no duplicate
push, PR, or status comment; remote drift and multiple matching PRs fail
loudly; one repository-wide lock prevents concurrent clones/worktrees from
acting. Durable intent/state is written atomically at
`${OS_HOME}/.local/state/lol-performance-overlay-sandcastle/state.json` with
mode `0600`; its sibling `worker.lock/` is `0700`. Neither enters the sandbox,
and qualification output does not expand the user's home path.

The durable round-start commit is validated as an exact local commit, then
exported once as `SANDCASTLE_ROUND_START_SHA` before the complete project gate
shell command. Every clause, pipeline, subshell, and child therefore sees the
same immutable start SHA even after the Issue branch has advanced.

Sandcastle returns the matched signal and commit list, and documents the
completion convention. Sources: [run result](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#api),
[completion signal](https://github.com/mattpocock/sandcastle/blob/v0.12.0/README.md#early-termination-with-promisecompletepromise).

Remaining qualification caveats: a successful Linux container smoke test does
not validate this product's Windows WPF, DPI, overlay-focus, packaging, or real
LoL lifecycle gates, and Sandcastle's writable parent `.git` mount prevents
describing the container as a hermetic untrusted-code sandbox.
