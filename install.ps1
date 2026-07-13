$ErrorActionPreference = 'Stop'

$sourceExe = Join-Path $PSScriptRoot 'CodexQuotaFloat.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw 'CodexQuotaFloat.exe must be in the same folder as install.ps1.'
}

$installDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaFloat'
$targetExe = Join-Path $installDir 'CodexQuotaFloat.exe'
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir 'Codex Quota Float.lnk'

Get-Process -Name 'CodexQuotaFloat' -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Codex quota floating window background watcher'
$shortcut.Save()

Start-Process -FilePath $targetExe
Write-Host "Installed: $targetExe"
Write-Host 'The floating window will appear with Codex and hide when Codex exits.'
