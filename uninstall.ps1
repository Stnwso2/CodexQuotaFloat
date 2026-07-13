$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'CodexQuotaFloat'
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

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

Write-Host 'Codex Quota Float has been uninstalled.'
