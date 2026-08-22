# 强制停止 dsh-tray 管理的一切：托盘、应用窗口、以及所有 dsh web 后端。
#
# 正常情况用托盘菜单的「退出」即可；这个脚本是给「托盘本身已经没了、但后台还在跑」
# 的兜底场景用的。
param([int]$Port = 3080)

$ErrorActionPreference = 'SilentlyContinue'

$fingerprint = '@deepseek-ai\dsh\lib\bin.js'
$appProfile  = 'app-window-profile'

function Stop-Tree([int]$ProcessId) {
  # 先杀子进程，避免中途被重新挂到别的父进程下
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" |
    ForEach-Object { Stop-Tree $_.ProcessId }
  Stop-Process -Id $ProcessId -Force
}

$killed = 0

# 1. 托盘控制器
Get-Process -Name 'DeepSeekTray' | ForEach-Object {
  Write-Host "停止托盘 pid $($_.Id)"
  Stop-Tree $_.Id; $killed++
}

# 2. 应用窗口 —— 只认带我们私有浏览器配置目录的进程，绝不碰你自己的 Edge/Chrome
Get-CimInstance Win32_Process -Filter "Name='msedge.exe' OR Name='chrome.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine -like "*$appProfile*" } |
  ForEach-Object {
    Write-Host "关闭应用窗口 pid $($_.ProcessId)"
    Stop-Tree $_.ProcessId; $killed++
  }

# 3. 后端 —— 端口占用者 + 命令行特征扫描；只针对 web profile，
#    你手动跑的 dsh --profile headless / tui 不受影响
Get-NetTCPConnection -LocalPort $Port -State Listen |
  ForEach-Object {
    Write-Host "停止端口 $Port 占用者 pid $($_.OwningProcess)"
    Stop-Tree $_.OwningProcess; $killed++
  }

Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Where-Object {
    $_.CommandLine -and $_.CommandLine -like "*$fingerprint*" -and
    ($_.CommandLine -match 'bin\.js"?\s+web' -or $_.CommandLine -match '--profile\s+web')
  } |
  ForEach-Object {
    Write-Host "停止后端 pid $($_.ProcessId)"
    Stop-Tree $_.ProcessId; $killed++
  }

Start-Sleep -Milliseconds 400
$still = Get-NetTCPConnection -LocalPort $Port -State Listen
if ($still) {
  Write-Host "端口 $Port 仍被 pid $($still[0].OwningProcess) 占用" -ForegroundColor Red
} elseif ($killed -gt 0) {
  Write-Host "已全部停止（$killed 棵进程树）。" -ForegroundColor Green
} else {
  Write-Host "本来就没有在运行。" -ForegroundColor Yellow
}
