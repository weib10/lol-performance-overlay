#!/usr/bin/env bash
set -euo pipefail
umask 077

repository_url="https://github.com/weib10/lol-performance-overlay.git"
base_ref="agent/linux-usability-release"
deployment_root="${XDG_DATA_HOME:-${HOME}/.local/share}/lol-performance-overlay-sandcastle"
repository_root="${deployment_root}/repository"
unit_root="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"

unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN
unset COPILOT_GITHUB_TOKEN GITHUB_APP_ID GITHUB_APP_PRIVATE_KEY
unset GITHUB_APP_INSTALLATION_ID GITHUB_APP_CLIENT_ID GITHUB_APP_CLIENT_SECRET
unset GH_HOST GH_REPO GH_CONFIG_DIR GH_PATH
export GH_PROMPT_DISABLED=1
export GH_NO_UPDATE_NOTIFIER=1
export GIT_TERMINAL_PROMPT=0
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_NOSYSTEM=1
export GIT_NO_REPLACE_OBJECTS=1

if [[ ! -d "${repository_root}/.git" ]]; then
  if [[ -e "${repository_root}" ]]; then
    echo "Deployment path exists but is not a Git checkout: ${repository_root}" >&2
    exit 1
  fi
  /usr/bin/install -d -m 700 "${deployment_root}"
  /usr/bin/git \
    -c core.hooksPath=/dev/null \
    -c core.askPass= \
    -c credential.helper= \
    -c credential.https://github.com.helper='!/usr/bin/gh auth git-credential' \
    -c credential.useHttpPath=true \
    clone --branch "${base_ref}" --single-branch --no-tags \
    "${repository_url}" "${repository_root}"
fi

cd "${repository_root}"
if [[ "$(/usr/bin/git remote get-url origin)" != "${repository_url}" ||
      "$(/usr/bin/git remote get-url --push origin)" != "${repository_url}" ||
      "$(/usr/bin/git branch --show-current)" != "${base_ref}" ||
      -n "$(/usr/bin/git status --porcelain=v1 --untracked-files=all)" ]]; then
  echo "Deployment checkout does not match the clean pinned repository/base." >&2
  exit 1
fi

"${HOME}/.volta/bin/volta" run --node 22.23.2 --npm 10.9.8 \
  npm ci --ignore-scripts --no-audit --no-fund
"${HOME}/.volta/bin/volta" run --node 22.23.2 --npm 10.9.8 \
  npm run sandcastle:build
"${HOME}/.volta/bin/volta" run --node 22.23.2 --npm 10.9.8 \
  npm run sandcastle:verify

/usr/bin/install -d -m 700 "${unit_root}"
/usr/bin/install -m 600 \
  .sandcastle/systemd/lol-performance-overlay-sandcastle.service \
  "${unit_root}/lol-performance-overlay-sandcastle.service"
/usr/bin/install -m 600 \
  .sandcastle/systemd/lol-performance-overlay-sandcastle.timer \
  "${unit_root}/lol-performance-overlay-sandcastle.timer"
/usr/bin/systemctl --user daemon-reload
/usr/bin/systemctl --user enable --now lol-performance-overlay-sandcastle.timer
/usr/bin/systemctl --user start --no-block lol-performance-overlay-sandcastle.service

echo "Installed and started the LoL Sandcastle Issue worker timer."
