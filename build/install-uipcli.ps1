<#
.SYNOPSIS
    Downloads the UiPath CLI (uipcli) from the official public feed into .uipcli/.

.DESCRIPTION
    Every other script in build/ calls this first, so a clean machine or a fresh CI agent
    needs no manual setup. The CLI is cached: re-running is a no-op unless -Force is passed.

.PARAMETER Version
    Exact package version to install. Defaults to the latest stable version on the feed.
    To see what is available, open the feed index the script queries:
      https://uipath.pkgs.visualstudio.com/Public.Feeds/_packaging/UiPath-Official/nuget/v3/flat2/uipath.cli.windows/index.json
    Pin a version here once you have picked one, so builds stay reproducible.

.PARAMETER PackageId
    UiPath.CLI.Windows for Windows agents, UiPath.CLI for the cross-platform build.

.EXAMPLE
    ./build/install-uipcli.ps1
    ./build/install-uipcli.ps1 -Version <version-from-the-feed-index> -Force
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [ValidateSet('UiPath.CLI.Windows', 'UiPath.CLI')]
    [string]$PackageId = 'UiPath.CLI.Windows',
    [string]$InstallRoot = (Join-Path $PSScriptRoot '..' | Join-Path -ChildPath '.uipcli'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Feed = 'https://uipath.pkgs.visualstudio.com/Public.Feeds/_packaging/UiPath-Official/nuget/v3'

function Get-LatestVersion {
    param([string]$Id)

    $url = "$Feed/flat2/$($Id.ToLowerInvariant())/index.json"
    Write-Host "Querying $url for the latest version..."
    $index = Invoke-RestMethod -Uri $url -UseBasicParsing

    # The feed lists versions oldest-first; skip pre-release builds.
    $stable = $index.versions | Where-Object { $_ -notmatch '-' }
    if (-not $stable) { $stable = $index.versions }
    return $stable[-1]
}

if (-not $Version) {
    $Version = Get-LatestVersion -Id $PackageId
}

$installDir = Join-Path $InstallRoot "$PackageId.$Version"

function Find-Cli {
    <#
        UiPath.CLI.Windows ships uipcli.exe; the cross-platform UiPath.CLI package ships a
        framework-dependent uipcli.dll that callers launch through `dotnet`. Look for both,
        preferring a directly executable file.
    #>
    param([string]$Root)

    foreach ($name in @('uipcli.exe', 'uipcli', 'uipcli.dll')) {
        $found = Get-ChildItem -Path $Root -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

if ((Test-Path $installDir) -and -not $Force) {
    $existing = Find-Cli -Root $installDir
    if ($existing) {
        Write-Host "UiPath CLI $Version already installed at $existing"
        $existing | Out-File -FilePath (Join-Path $InstallRoot 'uipcli.path') -Encoding utf8 -NoNewline
        return $existing
    }
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$nupkg = Join-Path $InstallRoot "$PackageId.$Version.nupkg"
$downloadUrl = "$Feed/flat2/$($PackageId.ToLowerInvariant())/$Version/$($PackageId.ToLowerInvariant()).$Version.nupkg"

Write-Host "Downloading $PackageId $Version"
Invoke-WebRequest -Uri $downloadUrl -OutFile $nupkg -UseBasicParsing

if (Test-Path $installDir) { Remove-Item -Path $installDir -Recurse -Force }
Write-Host "Extracting to $installDir"
Expand-Archive -Path $nupkg -DestinationPath $installDir -Force
Remove-Item -Path $nupkg -Force

$cli = Find-Cli -Root $installDir
if (-not $cli) {
    throw "Could not find uipcli under $installDir after extracting $PackageId $Version."
}

Write-Host "UiPath CLI installed: $cli"
$cli | Out-File -FilePath (Join-Path $InstallRoot 'uipcli.path') -Encoding utf8 -NoNewline
return $cli
