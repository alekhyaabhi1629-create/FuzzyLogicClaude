<#
.SYNOPSIS
    Runs the engine self-tests.

.DESCRIPTION
    -Mode Harness (default) runs the suite through the .NET console harness. This is the fast
    path and the one CI uses: no Studio, no Orchestrator, works on any platform.

    -Mode Orchestrator runs the RunSelfTests workflow as an Orchestrator job through the
    UiPath CLI, which is how you verify the packaged process on a real robot.

.EXAMPLE
    ./build/test.ps1
    ./build/test.ps1 -Mode Orchestrator -OrchestratorUrl https://... -Tenant DefaultTenant `
                     -AccountName acme -OrganizationUnit 'Finance/AccountsPayable'
#>
[CmdletBinding()]
param(
    [ValidateSet('Harness', 'Orchestrator')]
    [string]$Mode = 'Harness',
    [string]$OrchestratorUrl = '',
    [string]$Tenant = '',
    [string]$AccountName = '',
    [string]$ApplicationId = $env:UIPATH_APP_ID,
    [string]$ApplicationSecret = $env:UIPATH_APP_SECRET,
    [string]$ApplicationScope = 'OR.Folders OR.Execution OR.Jobs OR.Monitoring OR.Robots',
    [string]$OrganizationUnit = '',
    [string]$ProcessName = 'VendorMatching',
    [string]$CliPath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if ($Mode -eq 'Harness') {
    $harness = Join-Path $script:RepoRoot 'tools/EngineHarness/EngineHarness.csproj'
    $sampleData = Join-Path $script:ProjectRoot 'Data'

    Write-Host "> dotnet run --project $harness -- test --data $sampleData" -ForegroundColor Cyan
    & dotnet run --project $harness -- test --data $sampleData
    if ($LASTEXITCODE -ne 0) { throw "Self-tests failed (exit code $LASTEXITCODE)." }

    Write-Host ''
    Write-Host 'All self-tests passed.' -ForegroundColor Green
    return
}

foreach ($required in @('OrchestratorUrl', 'Tenant')) {
    if (-not (Get-Variable -Name $required -ValueOnly)) {
        throw "-$required is required when -Mode Orchestrator is used."
    }
}
if (-not $ApplicationId) { throw 'ApplicationId is required (pass -ApplicationId or set UIPATH_APP_ID).' }
if (-not $ApplicationSecret) { throw 'ApplicationSecret is required (pass -ApplicationSecret or set UIPATH_APP_SECRET).' }

$cli = Get-UiPathCli -CliPath $CliPath

# Runs the already-deployed process; RunSelfTests throws on failure, which fails the job and
# therefore this script.
$jobArgs = @(
    'job', 'run',
    $OrchestratorUrl,
    $Tenant,
    '-I', $ApplicationId,
    '-S', $ApplicationSecret,
    '--applicationScope', $ApplicationScope,
    '-p', $ProcessName,
    '--entryPointPath', 'Tests/RunSelfTests.cs',
    '--timeout', '600',
    '--fail_when_job_fails', 'true',
    '--traceLevel', 'Information'
)
if ($AccountName) { $jobArgs += @('-A', $AccountName) }
if ($OrganizationUnit) { $jobArgs += @('-o', $OrganizationUnit) }

Invoke-UiPathCli -Cli $cli -Arguments $jobArgs

Write-Host ''
Write-Host 'Self-tests passed on the robot.' -ForegroundColor Green
