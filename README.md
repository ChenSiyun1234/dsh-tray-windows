# dsh-tray-windows

把 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh web`）变成一个**真正的 Windows 桌面应用**：一个托盘图标、一个独立窗口、一个后台进程 —— 退出就是真的退出。

> **非官方项目**，与 DeepSeek 官方无隶属关系，也未获其背书。图标为本项目原创，不含任何 DeepSeek 商标元素。

---

## 为什么需要它

`dsh web` 本身是个命令行启动的 Node 服务。直接用当然没问题，但当它变成你每天都要开的工具，就会撞上这几件事：

### 痛点 1 · 关掉界面 ≠ 关掉后台

这是最要命的一个。`dsh web` 起来之后，**界面只是浏览器里的一个网页，真正在跑的是后台那个 node 进程**。

你把浏览器标签一关，看着像是"关掉了"，实际上：

- node 进程还在，常驻内存（实测空闲状态就有几百 MB，跑过长会话之后更多）
- 3080 端口一直被占着
- 会话状态、投影缓存、子进程（工具用的 shell、Playwright 起的浏览器）全都还挂着
- 一直到你下次重启电脑，它都在那儿

更隐蔽的是：**就算你去任务管理器把那个 node 结束掉，它派生的子进程也未必跟着死** —— 工具调用起的 PowerShell、`node-pty` 的终端、插件起的无头 Chromium，都会变成孤儿进程留在系统里。

本项目的做法：后台进程从诞生起就被放进一个 Windows **Job Object（`KILL_ON_JOB_CLOSE`）**。托盘退出 → 整个 job 关闭 → 后端连同它所有子孙进程一起被内核回收。哪怕托盘是被任务管理器强杀的，这条保证依然成立。

```powershell
# 强杀托盘（最坏情况，完全不走清理逻辑）
Stop-Process -Name DeepSeekTray -Force

# 3 秒后检查
托盘:        已退出
后端 node:   已退出
端口 3080:   已释放
应用窗口:    已关闭
你自己的 Edge: 14 个进程，未受影响
```

### 痛点 2 · 一不小心就开出好几个

快捷方式多点两下，或者你在终端里又敲了一次 `dsh web`，于是：多个 node 进程、多个端口、内存翻倍，而且你根本不知道哪个是哪个。

本项目的做法是三道锁：

1. **托盘互斥锁** —— 再次点击快捷方式不会启动任何东西，只会把已有窗口调到最前面
2. **启动时收敛** —— 启动时扫描所有 `dsh web` 后端，只留持有目标端口的那一个，其余关闭
3. **60 秒守护** —— 之后如果又冒出第二个（哪怕在别的端口），一分钟内自动关闭并气泡提示

只认 `web` 这个 profile。你手动跑的 `dsh --profile headless` 之类**不会**被误伤。

```
连点桌面快捷方式 6 次   →  托盘 1 个 · 后端 1 个 · 窗口 1 个 · 端口 3080
终端偷起 --port 3099    →  60 秒内自动关闭，只剩 3080
```

### 痛点 3 · 每次都往浏览器里弹一个标签页

`dsh web` 默认会调用系统默认浏览器打开 URL。结果就是它混在你几十个标签里找不着，还和你的日常浏览器会话、扩展、翻译弹窗搅在一起。

本项目的做法：后端一律以 `--no-open` 启动，界面由托盘用 **Edge/Chrome 的 `--app` 模式**打开 —— 没有标签栏、没有地址栏，有自己的任务栏图标，就是一个普通桌面窗口。而且它跑在**独立的浏览器配置目录**里，和你自己的浏览器完全隔离（也因此，关闭它永远不会误关你的浏览器）。

### 痛点 4 · 托盘图标糊成一团 / 不知道后台是死是活

单尺寸的 `.ico` 在 125% / 150% 缩放下会被重采样，糊掉。而且图标是静态的，后台挂没挂你看不出来。

本项目提供 16/20/24/32/40/48/64 **每个尺寸单独绘制**的图标集，并按 Windows 的小图标度量精确取帧；图标本身还会跟随服务状态变化。

![图标尺寸](docs/icon-sizes.png)

![运行状态](docs/icon-states.png)

---

## 效果

托盘右键菜单 —— 顶部是带状态点的实时状态行：

![托盘菜单](docs/tray-menu.png)

| 菜单项 | 作用 |
| --- | --- |
| 🐳 打开 DeepSeek 界面 | 独立应用窗口；已经开着就把它调到最前（不会开第二个） |
| 🌐 在浏览器中打开 | 需要用浏览器的调试工具时用 |
| 🔄 重启后台服务 | 改完 `cordis.patch.yml` 重载插件用 |
| 🚀 开机自动启动 | 勾选即写入当前用户的启动项 |
| ⚙️ 插件配置 | 直接打开 `~\.dsh\profiles\web\cordis.patch.yml` |
| 📁 数据目录 | 打开 `~\.dsh` |
| 📝 运行日志 | 后端 stdout/stderr 都被接管写入 `logs\harness.log` |
| ⏻ 退出 | 关闭窗口 + 停止后端 + 清理所有子进程 |

左键单击图标 = 打开/聚焦窗口。如果那会儿后台是停的，会先把它拉起来。

---

## 安装

**前置**：Windows 10/11、Node.js、已装 `@deepseek-ai/dsh`、Edge 或 Chrome。
编译用的是系统自带的 .NET Framework 编译器，**不需要装 Visual Studio 或 .NET SDK**。

```powershell
git clone https://github.com/ChenSiyun1234/dsh-tray-windows.git
cd dsh-tray-windows
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

装到 `%LOCALAPPDATA%\Programs\dsh-tray`，建好桌面快捷方式并启动。

自定义：

```powershell
# 换端口 / 换安装目录 / 不建快捷方式
scripts\install.ps1 -Port 8080 -InstallDir D:\Tools\dsh-tray -NoShortcut
```

**想用官方那个鲸鱼图标？** 本仓库出于商标考虑只附带原创图标，但提供了一个脚本，从**你本机已安装的 dsh** 里现取 favicon 生成（需要 Python + Pillow）：

```powershell
python build\make-icons.py --out "$env:LOCALAPPDATA\Programs\dsh-tray" --from-dsh
```

卸载：

```powershell
scripts\uninstall.ps1
```

只删这个工具自己的东西 —— 你的 `~\.dsh` 数据和 dsh 本体不会被动。

---

## 工作原理

```
DeepSeekTray.exe  （托盘，单实例互斥锁）
   │
   ├── Job Object (KILL_ON_JOB_CLOSE)
   │     ├── node dsh/lib/bin.js web --no-open --port 3080
   │     │     └── 后端派生的一切（工具 shell、无头浏览器、worker…）
   │     └── msedge --app=http://127.0.0.1:3080 --user-data-dir=<私有目录>
   │           └── 渲染进程若干
   │
   └── 退出时：关窗口 → 按端口占用者 + 命令行特征清扫进程树 → 关闭 job
```

几个关键点：

- **进程归属靠 Job Object**，不是靠"记住 PID 然后杀"。PID 会变、进程会重新挂父，job 不会。
- **只按命令行特征认自己人**：`@deepseek-ai\dsh\lib\bin.js` + `web` profile。系统里别的 node 进程（编辑器、MCP server、构建工具）永远不会被碰。
- **应用窗口靠私有 `--user-data-dir` 识别**，所以能精确关掉自己的窗口而不动你的浏览器。
- **收养机制**：托盘启动时如果发现已有一个后端在跑（比如上次崩溃留下的），会用 `AssignProcessToJobObject` 把它收进 job，这样退出时照样清得掉。
- 退出、注销、关机（`SessionEnding`）、进程被强杀，走的是同一条清理保证。

---

## 配置

安装目录下的 `tray.settings`（没有就是用默认值）：

```ini
# dsh web 的端口，默认 3080
port=3080
```

安装目录下还会有：

- `logs\harness.log` —— 后端输出，超过 4 MB 自动轮转
- `app-window-profile\` —— 应用窗口的私有浏览器配置

---

## 兼容性

在 Windows 11（26100，150% 缩放）+ Node.js + `@deepseek-ai/dsh` `0.1.1-rc.2` + Microsoft Edge 上开发和测试。

Windows 10 理论上可用（Job Object 嵌套需要 Win8+，DPI 感知会自动降级），但没有实测过。欢迎反馈。

---

## 许可

MIT，见 [LICENSE](LICENSE)。

本项目与 DeepSeek 无隶属关系。"DeepSeek" 及其相关标识归其各自所有者所有；本仓库不分发任何 DeepSeek 商标资产。
