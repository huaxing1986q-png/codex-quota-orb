[CmdletBinding()]
param(
    [ValidateRange(1, 30)]
    [int]$PollIntervalSeconds = 2,
    [string]$MonitorScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($MonitorScriptPath)) {
    $MonitorScriptPath = Join-Path (Split-Path -Parent $PSCommandPath) 'CodexMonitor.ps1'
}

if (-not (Test-Path -LiteralPath $MonitorScriptPath)) {
    throw "Codex monitor script was not found: $MonitorScriptPath"
}

function Test-CodexDesktopRunning {
    foreach ($processName in @('ChatGPT', 'codex')) {
        if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
            return $true
        }
    }
    return $false
}

function Start-CodexMonitor {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' +
        $MonitorScriptPath.Replace('"', '\"') + '" -Mode Start'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    [Diagnostics.Process]::Start($startInfo) | Out-Null
}

# Launch once when the Codex desktop app appears. Deliberately do not relaunch
# after the user dismisses the overlay with Esc during the same app session.
$wasCodexRunning = $false
while ($true) {
    $isCodexRunning = Test-CodexDesktopRunning
    if ($isCodexRunning -and -not $wasCodexRunning) {
        Start-CodexMonitor
    }
    $wasCodexRunning = $isCodexRunning
    Start-Sleep -Seconds $PollIntervalSeconds
}
