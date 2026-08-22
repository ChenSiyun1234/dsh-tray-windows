# 卸载 dsh-tray-windows：停掉所有进程，删掉快捷方式、自启项和安装目录。
# 只删这个工具自己的东西 —— 你的 ~\.dsh 数据目录和 dsh 本体不会被动。
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\Programs\dsh-tray",
  [switch]$KeepInstallDir
)

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "正在停止…"
& "$PSScriptRoot\stop.ps1" | Out-Null

$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "DeepSeek Harness.lnk"
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "已删除桌面快捷方式" }

$runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty $runKey -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue)) {
  Remove-ItemProperty $runKey -Name 'DeepSeek Harness' -Force
  Write-Host "已移除开机自启项"
}

if (-not $KeepInstallDir -and (Test-Path $InstallDir)) {
  Start-Sleep -Milliseconds 500
  Remove-Item $InstallDir -Recurse -Force
  Write-Host "已删除安装目录 $InstallDir"
}

Write-Host "卸载完成。你的 ~\.dsh 数据和 @deepseek-ai/dsh 本体未被改动。" -ForegroundColor Green
