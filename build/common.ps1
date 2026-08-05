<#
    Shared helpers for the build/ scripts: locating the CLI and running it with the arguments
    echoed, so a failing pipeline step shows exactly which command was executed.
#>

Set-StrictMode -Version Latest

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:ProjectRoot = Join-Path $script:RepoRoot 'VendorMatching'
$script:ProjectJson = Join-Path $script:ProjectRoot 'project.json'

function Get-UiPathCli {
    <#
        Returns the path to uipcli, installing it on first use. An explicit path wins, then
        UIPATH_CLI_PATH, then a cached install under .uipcli/.
    #>
    param([string]$CliPath = '')

    if ($CliPath) {
        if (-not (Test-Path $CliPath)) { throw "uipcli was not found at '$CliPath'." }
        return (Resolve-Path $CliPath).Path
    }

    if ($env:UIPATH_CLI_PATH -and (Test-Path $env:UIPATH_CLI_PATH)) {
        return (Resolve-Path $env:UIPATH_CLI_PATH).Path
    }

    $cache = Join-Path $script:RepoRoot '.uipcli/uipcli.path'
    if (Test-Path $cache) {
        $cached = (Get-Content -Path $cache -Raw).Trim()
        if ($cached -and (Test-Path $cached)) { return $cached }
    }

    Write-Host 'uipcli not found; installing it now.'
    # $env:OS is set on Windows under both PowerShell editions; $IsWindows does not exist in 5.1.
    $packageId = if ($env:OS -eq 'Windows_NT') { 'UiPath.CLI.Windows' } else { 'UiPath.CLI' }
    return & (Join-Path $PSScriptRoot 'install-uipcli.ps1') -PackageId $packageId
}

function Invoke-UiPathCli {
    <#
        Runs uipcli and throws on a non-zero exit code. Arguments are printed first, with any
        value following -S/--applicationSecret or -P/--password masked.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Cli,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $display = @()
    $maskNext = $false
    foreach ($argument in $Arguments) {
        if ($maskNext) {
            $display += '***'
            $maskNext = $false
            continue
        }
        $display += $argument
        if ($argument -in @('-S', '--applicationSecret', '-P', '--password', '-t', '--token')) {
            $maskNext = $true
        }
    }

    Write-Host ''
    Write-Host "> uipcli $($display -join ' ')" -ForegroundColor Cyan

    # The cross-platform package ships a framework-dependent uipcli.dll rather than an executable.
    if ($Cli.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $Cli @Arguments
    }
    else {
        & $Cli @Arguments
    }
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "uipcli exited with code $exitCode. Run '$Cli $($display[0]) $($display[1]) --help' to check the flags for your CLI version."
    }
}

function Assert-Project {
    if (-not (Test-Path $script:ProjectJson)) {
        throw "project.json was not found at $script:ProjectJson."
    }
}
