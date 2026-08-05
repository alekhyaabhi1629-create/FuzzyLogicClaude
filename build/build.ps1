<#
.SYNOPSIS
    Restores dependencies, runs Workflow Analyzer, and packs the VendorMatching project
    into a .nupkg using the UiPath CLI.

.PARAMETER OutputFolder
    Where the .nupkg is written. Defaults to <repo>/artifacts.

.PARAMETER PackageVersion
    Explicit package version. Omit to let the CLI derive one with --autoVersion.

.PARAMETER SkipAnalyze
    Skips Workflow Analyzer. Use only for a local smoke build; CI should always analyze.

.PARAMETER TreatWarningsAsErrors
    Fails the build on analyzer warnings as well as errors.

.EXAMPLE
    ./build/build.ps1
    ./build/build.ps1 -PackageVersion 1.2.3 -TreatWarningsAsErrors
#>
[CmdletBinding()]
param(
    [string]$OutputFolder = '',
    [string]$PackageVersion = '',
    [string]$CliPath = '',
    [switch]$SkipAnalyze,
    [switch]$TreatWarningsAsErrors
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Project
$cli = Get-UiPathCli -CliPath $CliPath

if (-not $OutputFolder) { $OutputFolder = Join-Path $script:RepoRoot 'artifacts' }
New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
$OutputFolder = (Resolve-Path $OutputFolder).Path

# --- restore --------------------------------------------------------------------------------
# Pulls the activity packages listed in project.json so the analyzer and the pack step both
# work against resolved dependencies rather than failing halfway through.
Invoke-UiPathCli -Cli $cli -Arguments @(
    'package', 'restore',
    $script:ProjectJson,
    '-o', (Join-Path $OutputFolder 'packages'),
    '--restoreType', 'Force',
    '--traceLevel', 'Information'
)

# --- analyze --------------------------------------------------------------------------------
if (-not $SkipAnalyze) {
    $analyzeArgs = @(
        'package', 'analyze',
        $script:ProjectJson,
        '--analyzerTraceLevel', 'Warning',
        '--resultPath', (Join-Path $OutputFolder 'analyzer-results.json'),
        '--stopOnRuleViolation'
    )
    if ($TreatWarningsAsErrors) { $analyzeArgs += '--treatWarningsAsErrors' }

    Invoke-UiPathCli -Cli $cli -Arguments $analyzeArgs
}

# --- pack -----------------------------------------------------------------------------------
$packArgs = @(
    'package', 'pack',
    $script:ProjectJson,
    '-o', $OutputFolder,
    '--outputType', 'Process',
    '--traceLevel', 'Information'
)
if ($PackageVersion) { $packArgs += @('--version', $PackageVersion) } else { $packArgs += '--autoVersion' }

Invoke-UiPathCli -Cli $cli -Arguments $packArgs

$packages = Get-ChildItem -Path $OutputFolder -Filter '*.nupkg' -File | Sort-Object LastWriteTime -Descending
if (-not $packages) { throw "No .nupkg was produced in $OutputFolder." }

Write-Host ''
Write-Host "Packed: $($packages[0].FullName)" -ForegroundColor Green
return $packages[0].FullName
