# syntax=docker/dockerfile:1.7

# linux/amd64 manifests resolved from the official images on 2026-08-12.
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:8.0.423-bookworm-slim@sha256:17207af6fa36f4378153327cf4737722e02e622b5d0ad9ab6ce62238a41016c0 AS dotnet
FROM --platform=linux/amd64 node:22-bookworm@sha256:673fce836d5a9185da33352682bfedb17c174d016370d08616748dff76fda862

COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=true \
    NUGET_XMLDOC_MODE=skip

RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && npm install --global --omit=dev @openai/codex@0.147.0 \
    && npm cache clean --force \
    && test "$(node --version)" = "v22.23.2" \
    && test "$(npm --version)" = "10.9.8" \
    && test "$(dotnet --version)" = "8.0.423" \
    && test "$(codex --version)" = "codex-cli 0.147.0"

# Sandcastle supplies the host UID/GID so bind-mounted files remain owned by
# the developer while the agent itself stays non-root.
ARG AGENT_UID=1000
ARG AGENT_GID=1000
RUN groupmod -o -g "${AGENT_GID}" node \
    && usermod -o -u "${AGENT_UID}" -g "${AGENT_GID}" -d /home/agent -m -l agent node \
    && mkdir -p /home/agent/.codex \
    && chown -R "${AGENT_UID}:${AGENT_GID}" /home/agent

ENV HOME=/home/agent \
    CODEX_HOME=/home/agent/.codex

USER ${AGENT_UID}:${AGENT_GID}
WORKDIR /home/agent

# Sandcastle bind-mounts its Git worktree at runtime, matching the official
# Docker provider contract.
ENTRYPOINT ["sleep", "infinity"]
