$ErrorActionPreference = 'Stop'

$sourceExe = Join-Path $PSScriptRoot 'CodexQuotaFloat.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw 'CodexQuotaFloat.exe must be in the same folder as install.ps1.'
}

$installDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaFloat'
$targetExe = Join-Path $installDir 'CodexQuotaFloat.exe'
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir 'Codex Quota Float.lnk'

foreach ($process in @(Get-Process -Name 'CodexQuotaFloat' -ErrorAction SilentlyContinue)) {
    try {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        if (-not $process.WaitForExit(10000)) {
            throw "CodexQuotaFloat process $($process.Id) did not exit within 10 seconds."
        }
    }
    finally {
        $process.Dispose()
    }
}

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
