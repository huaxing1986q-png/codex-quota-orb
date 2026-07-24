[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'CodexQuotaOrb'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex Quota Orb.lnk'

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

Write-Host 'Auto-start has been removed.'
Write-Host 'Press Esc on the collapsed orb to close it, then delete this folder if desired:'
Write-Host $installRoot
