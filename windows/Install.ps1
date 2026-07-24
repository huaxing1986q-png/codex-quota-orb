[CmdletBinding()]
param(
    [switch]$NoAutoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'This installer requires Windows.'
}

$packageRoot = Split-Path -Parent $PSCommandPath
$sourceScripts = Join-Path $packageRoot 'scripts'
if (-not (Test-Path -LiteralPath (Join-Path $sourceScripts 'CodexMonitor.ps1'))) {
    throw "Package scripts are missing from: $sourceScripts"
}

$installRoot = Join-Path $env:LOCALAPPDATA 'CodexQuotaOrb'
$installedScripts = Join-Path $installRoot 'scripts'
New-Item -ItemType Directory -Path $installedScripts -Force | Out-Null

Get-ChildItem -LiteralPath $sourceScripts -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installedScripts $_.Name) -Force
}

foreach ($name in @('Start Codex Quota Orb.vbs', 'Uninstall.ps1', 'README-Windows.md')) {
    $source = Join-Path $packageRoot $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $installRoot $name) -Force
    }
}

$shell = New-Object -ComObject WScript.Shell
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'Codex Quota Orb.lnk'
$watcherPath = Join-Path $installedScripts 'StartCodexMonitorWatcher.vbs'
$launcherPath = Join-Path $installRoot 'Start Codex Quota Orb.vbs'
$wscriptPath = Join-Path $env:WINDIR 'System32\wscript.exe'

if (-not $NoAutoStart) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $wscriptPath
    $shortcut.Arguments = '"' + $watcherPath + '"'
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Description = 'Start Codex Quota Orb when you sign in'
    $shortcut.Save()
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $wscriptPath
$startInfo.Arguments = '"' + $launcherPath + '"'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
[Diagnostics.Process]::Start($startInfo) | Out-Null

[pscustomobject]@{
    installed = $true
    install_root = $installRoot
    auto_start = -not $NoAutoStart
    shortcut = if ($NoAutoStart) { $null } else { $shortcutPath }
} | ConvertTo-Json
