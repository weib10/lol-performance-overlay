[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetHost = $env:PACKAGE_DOTNET_HOST

if ([string]::IsNullOrWhiteSpace($dotnetHost)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '.NET 8 SDK was not found. Install it from an official Microsoft source and try again.'
    }

    $dotnetHost = $dotnetCommand.Source
}

$env:PACKAGE_REPOSITORY_ROOT = $repositoryRoot
$env:PACKAGE_DOTNET_HOST = $dotnetHost
$env:DOTNET_CLI_HOME = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
    Join-Path $repositoryRoot 'artifacts/dotnet-cli-home'
} else {
    $env:DOTNET_CLI_HOME
}
$env:NUGET_PACKAGES = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $repositoryRoot 'artifacts/nuget-packages'
} else {
    $env:NUGET_PACKAGES
}
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

& $dotnetHost run `
    --project (Join-Path $repositoryRoot 'eng/PackageBuilder/PackageBuilder.csproj') `
    --configuration Release `
    -- `
    --root $repositoryRoot

if ($LASTEXITCODE -ne 0) {
    throw "PackageBuilder failed with exit code $LASTEXITCODE."
}
