[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'dist'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$packageRoot = Join-Path $OutputDirectory 'Codex Quota Orb Windows'
$packageScripts = Join-Path $packageRoot 'scripts'
$archivePath = Join-Path $OutputDirectory 'Codex-Quota-Orb-Windows.zip'

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

New-Item -ItemType Directory -Path $packageScripts -Force | Out-Null

foreach ($name in @(
    'CodexMonitor.Data.cs',
    'CodexMonitor.ContextDetails.cs',
    'CodexMonitor.Details.cs',
    'CodexMonitor.History.cs',
    'CodexMonitor.UI.cs',
    'CodexMonitor.ps1',
    'CodexMonitorAutoStart.ps1',
    'StartCodexMonitorWatcher.vbs'
)) {
    $source = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required Windows source is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageScripts $name) -Force
}

foreach ($name in @('Start Codex Quota Orb.vbs', 'Install.ps1', 'Uninstall.ps1', 'README-Windows.md')) {
    $source = Join-Path (Join-Path $repositoryRoot 'windows') $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required Windows package file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) -Force
}

Compress-Archive -LiteralPath $packageRoot -DestinationPath $archivePath -CompressionLevel Optimal

[pscustomobject]@{
    archive = $archivePath
    bytes = (Get-Item -LiteralPath $archivePath).Length
    files = (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Measure-Object).Count
} | ConvertTo-Json
