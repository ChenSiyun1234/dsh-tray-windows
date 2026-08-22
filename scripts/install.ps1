# 安装 dsh-tray-windows。
#
#   编译 → 安装到 %LOCALAPPDATA%\Programs\dsh-tray → 建桌面快捷方式 → 启动托盘
#
# 参数：
#   -InstallDir <路径>   自定义安装目录
#   -Port <端口>         dsh web 的端口（默认 3080）
#   -NoShortcut          不创建桌面快捷方式
#   -NoStart             安装完不自动启动
[CmdletBinding()]
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\Programs\dsh-tray",
  [int]$Port = 3080,
  [switch]$NoShortcut,
  [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Fail($message) { Write-Host $message -ForegroundColor Red; exit 1 }

# --- 前置检查 --------------------------------------------------------------
$node = @(
  "$env:ProgramFiles\nodejs\node.exe",
  "${env:ProgramFiles(x86)}\nodejs\node.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $node) { $node = (Get-Command node -ErrorAction SilentlyContinue).Source }
if (-not $node) { Fail "找不到 node.exe。请先安装 Node.js。" }

$dshBin = "$env:APPDATA\npm\node_modules\@deepseek-ai\dsh\lib\bin.js"
if (-not (Test-Path $dshBin)) {
  Write-Host "警告：未在默认位置找到 dsh（$dshBin）。" -ForegroundColor Yellow
  Write-Host "      请先执行： npm install -g @deepseek-ai/dsh" -ForegroundColor Yellow
}

# --- 编译 ------------------------------------------------------------------
Write-Host "正在编译…" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
& cmd /c "`"$root\build\build.cmd`" `"$InstallDir`""
if ($LASTEXITCODE -ne 0) { Fail "编译失败。" }

$exe = Join-Path $InstallDir "DeepSeekTray.exe"
if (-not (Test-Path $exe)) { Fail "编译产物缺失：$exe" }

# --- 端口设置 --------------------------------------------------------------
if ($Port -ne 3080) {
  Set-Content -Path (Join-Path $InstallDir "tray.settings") -Encoding utf8 -Value @(
    "# dsh-tray 配置",
    "port=$Port"
  )
  Write-Host "已写入 tray.settings：port=$Port"
}

# --- 桌面快捷方式 ----------------------------------------------------------
if (-not $NoShortcut) {
  $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "DeepSeek Harness.lnk"
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut($lnk)
  $shortcut.TargetPath = $exe
  $shortcut.WorkingDirectory = $InstallDir
  $shortcut.IconLocation = (Join-Path $InstallDir "app.ico") + ",0"
  $shortcut.Description = "DeepSeek Harness — 托盘控制器（后台服务 + 独立应用窗口）"
  $shortcut.Save()
  Write-Host "已创建桌面快捷方式：$lnk"
}

Write-Host ""
Write-Host "安装完成：$InstallDir" -ForegroundColor Green
Write-Host "  想换回官方鲸鱼图标： python build\make-icons.py --out `"$InstallDir`" --from-dsh"
Write-Host "  卸载：               scripts\uninstall.ps1"

if (-not $NoStart) {
  Write-Host "正在启动托盘…" -ForegroundColor Cyan
  Start-Process -FilePath $exe
}
