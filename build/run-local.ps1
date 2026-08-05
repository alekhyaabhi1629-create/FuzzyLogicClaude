<#
.SYNOPSIS
    Runs the matcher locally against two CSV files, without Orchestrator.

.DESCRIPTION
    Two execution modes:
      -Mode Robot    executes the packed process with UiRobot.exe (needs UiPath Studio/Robot).
      -Mode Harness  executes the same engine through the .NET console harness (needs the
                     .NET SDK only, and runs on Linux and macOS too).

    Both produce the identical three output files, because they call the same MatchRunner.

.EXAMPLE
    ./build/run-local.ps1 -Invoices .\data\invoices.csv -SapVendors .\data\lfa1.csv -Output .\out
    ./build/run-local.ps1 -Mode Harness
#>
[CmdletBinding()]
param(
    [ValidateSet('Robot', 'Harness')]
    [string]$Mode = 'Harness',
    [string]$Invoices = '',
    [string]$SapVendors = '',
    [string]$Output = '',
    [string]$Config = '',
    [double]$AutoMatchThreshold = 0,
    [double]$ReviewThreshold = 0,
    [string]$UiRobotPath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Project

if (-not $Invoices) { $Invoices = Join-Path $script:ProjectRoot 'Data/invoices.sample.csv' }
if (-not $SapVendors) { $SapVendors = Join-Path $script:ProjectRoot 'Data/sap_vendors.sample.csv' }
if (-not $Output) { $Output = Join-Path $script:RepoRoot 'artifacts/output' }
if (-not $Config) { $Config = Join-Path $script:ProjectRoot 'Config/matching-config.json' }

New-Item -ItemType Directory -Path $Output -Force | Out-Null

if ($Mode -eq 'Harness') {
    $harness = Join-Path $script:RepoRoot 'tools/EngineHarness/EngineHarness.csproj'
    $arguments = @(
        'run', '--project', $harness, '--',
        'match',
        '--invoices', $Invoices,
        '--sap', $SapVendors,
        '--output', $Output,
        '--config', $Config
    )
    if ($AutoMatchThreshold -gt 0) { $arguments += @('--auto-match', $AutoMatchThreshold) }
    if ($ReviewThreshold -gt 0) { $arguments += @('--review', $ReviewThreshold) }

    Write-Host "> dotnet $($arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "The harness exited with code $LASTEXITCODE." }
}
else {
    if (-not $UiRobotPath) {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\UiPath\Studio\UiRobot.exe",
            "$env:ProgramFiles\UiPath\Studio\UiRobot.exe",
            "${env:ProgramFiles(x86)}\UiPath\Studio\UiRobot.exe"
        )
        $UiRobotPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $UiRobotPath) {
        throw 'UiRobot.exe was not found. Pass -UiRobotPath, or use -Mode Harness to run without the robot.'
    }

    $package = Get-ChildItem -Path (Join-Path $script:RepoRoot 'artifacts') -Filter '*.nupkg' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $package) { throw 'No .nupkg found in artifacts/. Run ./build/build.ps1 first.' }

    # Not named $input: that is an automatic variable in PowerShell.
    $jobInput = @{
        invoicesCsvPath    = $Invoices
        sapVendorsCsvPath  = $SapVendors
        outputFolder       = $Output
        configPath         = $Config
        autoMatchThreshold = $AutoMatchThreshold
        reviewThreshold    = $ReviewThreshold
    } | ConvertTo-Json -Compress

    Write-Host "> $UiRobotPath execute --file $($package.FullName)" -ForegroundColor Cyan
    & $UiRobotPath execute --file $package.FullName --input $jobInput
    if ($LASTEXITCODE -ne 0) { throw "UiRobot exited with code $LASTEXITCODE." }
}

Write-Host ''
Write-Host "Output written to $Output" -ForegroundColor Green
Get-ChildItem -Path $Output -File | Select-Object Name, Length | Format-Table
