$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaFloat'
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir 'Codex Quota Float.lnk'

Get-Process -Name 'CodexQuotaFloat' -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

Write-Host 'Codex Quota Float has been uninstalled.'
