[CmdletBinding()]
param(
    [ValidateSet('Start', 'Details', 'History', 'Usage', 'Schema', 'SelfTest')]
    [string]$Mode = 'Start',
    [string]$StatePath,
    [string]$SessionsRoot,
    [ValidateRange(0, 60)]
    [int]$AutoCloseSeconds = 0,
    [switch]$ForceVisible
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-MonitorStatePath {
    if (-not [string]::IsNullOrWhiteSpace($StatePath)) {
        return [IO.Path]::GetFullPath($StatePath)
    }
    return (Join-Path (Join-Path $env:LOCALAPPDATA 'CodexMonitorOverlay') 'state.json')
}

$monitorStatePath = Get-MonitorStatePath

if ([string]::IsNullOrWhiteSpace($SessionsRoot)) {
    $SessionsRoot = Join-Path (Join-Path $HOME '.codex') 'sessions'
}

$dataSource = Join-Path $PSScriptRoot 'CodexMonitor.Data.cs'
$historySource = Join-Path $PSScriptRoot 'CodexMonitor.History.cs'
$detailsSource = Join-Path $PSScriptRoot 'CodexMonitor.Details.cs'
$uiSource = Join-Path $PSScriptRoot 'CodexMonitor.UI.cs'
if (-not (Test-Path -LiteralPath $dataSource) -or
    -not (Test-Path -LiteralPath $historySource) -or
    -not (Test-Path -LiteralPath $detailsSource) -or
    -not (Test-Path -LiteralPath $uiSource)) {
    throw "Monitor source is incomplete in: $PSScriptRoot"
}

Add-Type -Path @($dataSource, $historySource, $detailsSource, $uiSource) -ReferencedAssemblies @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll'
)

if ($Mode -eq 'Usage') {
    [CodexMonitor.QuotaServiceReader]::ReadUsageJson()
    return
}

if ($Mode -eq 'SelfTest') {
    [CodexMonitor.QuotaServiceReader]::RunSelfTestJson()
    return
}

if ($Mode -eq 'Schema') {
    [CodexMonitor.QuotaServiceReader]::ReadSanitizedSchemaJson()
    return
}

if ($Mode -eq 'History') {
    [CodexMonitor.TokenHistoryReader]::ReadLatestJson(
        [IO.Path]::GetFullPath($SessionsRoot),
        (Join-Path (Split-Path -Parent $monitorStatePath) 'token-history-cache.json')
    )
    return
}

if ($Mode -eq 'Details') {
    [CodexMonitor.Runtime]::RunDetails(
        [IO.Path]::GetFullPath($SessionsRoot),
        [IO.Path]::GetFullPath($monitorStatePath),
        $AutoCloseSeconds
    )
    return
}

$created = $false
$mutex = New-Object Threading.Mutex($true, 'Local\CodexMonitorOverlay.Window.V9', [ref]$created)
if (-not $created) {
    $mutex.Dispose()
    return
}

try {
    [CodexMonitor.Runtime]::Run(
        [IO.Path]::GetFullPath($SessionsRoot),
        [IO.Path]::GetFullPath($monitorStatePath),
        $AutoCloseSeconds,
        $ForceVisible.IsPresent
    )
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
