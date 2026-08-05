<#
.SYNOPSIS
    Publishes the packed process to Orchestrator with the UiPath CLI.

.DESCRIPTION
    Authenticates with an External Application (client credentials). Never pass the secret on
    the command line in CI - set UIPATH_APP_SECRET from a pipeline secret instead.

.PARAMETER OrchestratorUrl
    e.g. https://cloud.uipath.com/<org>/<tenant>/orchestrator_ or your on-prem URL.

.PARAMETER Tenant
    Orchestrator tenant name.

.PARAMETER OrganizationUnit
    Target folder, e.g. "Finance/AccountsPayable".

.PARAMETER CreateProcess
    Also creates or updates the process in the folder, not just the package feed entry.

.EXAMPLE
    $env:UIPATH_APP_SECRET = '...'
    ./build/deploy.ps1 -OrchestratorUrl https://cloud.uipath.com/acme/DefaultTenant/orchestrator_ `
                       -Tenant DefaultTenant -AccountName acme -ApplicationId $id `
                       -OrganizationUnit 'Finance/AccountsPayable' -CreateProcess
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OrchestratorUrl,
    [Parameter(Mandatory = $true)][string]$Tenant,
    [string]$AccountName = '',
    [string]$ApplicationId = $env:UIPATH_APP_ID,
    [string]$ApplicationSecret = $env:UIPATH_APP_SECRET,
    [string]$ApplicationScope = 'OR.Folders OR.Execution OR.Assets OR.Jobs OR.Machines OR.Monitoring OR.Robots OR.Settings OR.Queues',
    [string]$OrganizationUnit = '',
    [string]$PackagesFolder = '',
    [string]$CliPath = '',
    [switch]$CreateProcess
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $ApplicationId) { throw 'ApplicationId is required (pass -ApplicationId or set UIPATH_APP_ID).' }
if (-not $ApplicationSecret) { throw 'ApplicationSecret is required (pass -ApplicationSecret or set UIPATH_APP_SECRET).' }

$cli = Get-UiPathCli -CliPath $CliPath

if (-not $PackagesFolder) { $PackagesFolder = Join-Path $script:RepoRoot 'artifacts' }
if (-not (Test-Path $PackagesFolder)) {
    throw "No packages folder at $PackagesFolder. Run ./build/build.ps1 first."
}
$PackagesFolder = (Resolve-Path $PackagesFolder).Path

if (-not (Get-ChildItem -Path $PackagesFolder -Filter '*.nupkg' -File)) {
    throw "No .nupkg found in $PackagesFolder. Run ./build/build.ps1 first."
}

$deployArgs = @(
    'package', 'deploy',
    $PackagesFolder,
    $OrchestratorUrl,
    $Tenant,
    '-I', $ApplicationId,
    '-S', $ApplicationSecret,
    '--applicationScope', $ApplicationScope,
    '--traceLevel', 'Information'
)

if ($AccountName) { $deployArgs += @('-A', $AccountName) }
if ($OrganizationUnit) { $deployArgs += @('-o', $OrganizationUnit) }
if ($CreateProcess) { $deployArgs += @('--createProcess', 'true', '--entryPointsPath', 'Main.cs') }

Invoke-UiPathCli -Cli $cli -Arguments $deployArgs

Write-Host ''
Write-Host "Deployed the packages in $PackagesFolder to $OrchestratorUrl ($Tenant)." -ForegroundColor Green
