#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
dotnet_host="${PACKAGE_DOTNET_HOST:-${DOTNET_HOST_PATH:-}}"

if [[ -z "$dotnet_host" ]]; then
  dotnet_host="$(command -v dotnet || true)"
fi

if [[ -z "$dotnet_host" || ! -x "$dotnet_host" ]]; then
  echo "錯誤：找不到 .NET 8 SDK。請從 Microsoft 官方來源安裝後重試。" >&2
  exit 1
fi

export PACKAGE_REPOSITORY_ROOT="$repository_root"
export PACKAGE_DOTNET_HOST="$dotnet_host"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$repository_root/artifacts/dotnet-cli-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$repository_root/artifacts/nuget-packages}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

exec "$dotnet_host" run \
  --project "$repository_root/eng/PackageBuilder/PackageBuilder.csproj" \
  --configuration Release \
  -- \
  --root "$repository_root"
