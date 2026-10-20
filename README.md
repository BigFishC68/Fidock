# MyDock

仿 macOS 风格的 Windows 桌面 Dock（WPF / .NET 10）。左键管窗口、右键管程序，支持拖拽排序、文件夹上拉面板、运行状态小圆点、多窗口选择面板、开机自启。

## 目录结构

```
MyDock/
├─ MyDock.csproj                    项目文件（源码在 src/ 下，App.xaml 显式声明为 ApplicationDefinition）
├─ MyDock.slnx                      解决方案（VS / dotnet CLI 直接打开）
├─ Properties/PublishProfiles/
│  └─ win-x64.pubxml                发布配置：win-x64 / 自包含 / 单文件
├─ src/
│  ├─ App.xaml(.cs)                 应用入口：单实例互斥 + 生命周期
│  ├─ SingleInstance.cs             已有实例时唤出 Dock 并退出
│  ├─ MainWindow.xaml(.cs)          主窗口：布局、图标加载、拖拽、左/中/右键交互、运行状态轮询
│  ├─ MainWindow.Native.cs          Win32 互操作集中声明（P/Invoke、结构体、消息常量）
│  ├─ MainWindow.SelfTest.cs        DEBUG-only 自测钩子（Release 不编译）
│  ├─ DockApp.cs                    Dock 条目模型（含运行检测用的进程名 / 目录）
│  ├─ BatchRemoveWindow.xaml(.cs)   批量移除窗口
│  ├─ AssemblyInfo.cs               ThemeInfo
│  └─ Interop/DwmBlur.cs            亚克力模糊 + 深色标题栏
├─ assets/
│  ├─ Fidock.ico                    程序图标（16/24/32/48/64/128/256 多尺寸，构建用）
│  └─ source/                       图标源文件（256 PNG 版 ico + PSD）
├─ docs/进度汇总.md                  功能演进与逐轮 Bug 修复记录
└─ MyDock_v1.0/                     发布产物（单文件 MyDock.exe，开箱即用）
```

## 环境要求

- 构建：.NET 10 SDK（Windows）。
- 运行：`MyDock_v1.0\MyDock.exe` 为自包含单文件发布，**目标机无需安装 .NET 运行时**。

## 常用命令

```powershell
# 编译（Debug 版带自测钩子）
dotnet build -c Debug

# 编译（Release）
dotnet build -c Release

# 发布到 MyDock_v1.0\（自包含单文件，不压缩）
dotnet publish -p:PublishProfile=win-x64

# 结束正在运行的实例（发布前若占用 exe 会导致 MSB3027）
Stop-Process -Name MyDock -Force
```

> 构建前若 Dock 正在运行，exe 会被占用；先退出 Dock 或重新发布到独立目录。

## 内存与发布形态

常驻内存的大头不是托管堆（实测构造全部窗口后仅 1.5 MB），而是：
1. 自包含单文件的**压缩**包会把 WPF 程序集解压进进程私有内存；
2. 运行状态轮询（每 2 秒）过去会为系统里每个进程创建 `Process` 对象并读 `MainModule`。

对应处理：发布关闭 `EnableCompressionInSingleFile`；轮询改用一次 Toolhelp 快照取进程名、
`QueryFullProcessImageName` 取路径并缓存 5 秒；路径索引在程序启停时主动失效。

| 发布形态 | exe | 私有内存 | 工作集 |
|---|---|---|---|
| 自包含单文件 + 压缩（旧） | 59 MB | 157 MB | 244 MB |
| 自包含单文件，不压缩（当前） | 126 MB | 102 MB | 152 MB |
| 依赖 .NET 10 运行时 | 0.2 MB | 100 MB | 151 MB |

轮询开销（同一进程内实测 5 次取平均）：分配 1266 KB/次 → 60 KB/次，耗时 81 ms → 6 ms。

## 使用

| 操作 | 行为 |
|------|------|
| 左键 | 未运行→启动；运行中→激活最顶层窗口；已在顶层→最小化其全部窗口；多窗口→图标上方呼出选择面板（行尾 × 关闭单个窗口） |
| 中键 | 强制新开一个窗口 |
| 右键 | 结束进程 / 打开文件位置 / 移除（删除只走这里；固定项与文件夹项不弹此菜单） |
| 拖拽 | 图标拖动排序（拖出栏外只是取消，不会删除）；文件拖到图标上用该程序打开；文件拖到回收站即删除；文件夹图标与 Dock 栏可互拖 |
| 栏空白处右键 | 开机启动 / 添加程序… / 批量移除 / 打开配置文件夹 / 退出 |

主界面被程序自己藏起来时（贴边收起、关闭到托盘、最小化），左键只做还原：把窗口拉回屏内、显示出来、取消最小化，
而不是新开一个实例。旧版 QQ 把主面板拖到屏幕边缘后收起就是这个形态——面板保持 300×600 尺寸，被整体平移出屏幕只留几像素，
同时清掉 `WS_VISIBLE`，因此 `WindowReveal`（`src/Interop/WindowReveal.cs`）按"沿被隐藏的那条边平移回屏内"还原。

配置保存在 `%APPDATA%\MyDock\apps.json`（损坏时先备份为 `apps.json.corrupt`）。`.lnk` 的源程序路径会持久化，删掉快捷方式后仍可启动与定位。

## 验证纪律

不在真实屏幕上注入鼠标/键盘事件。GUI 行为通过 DEBUG-only 自测钩子（环境变量 `MYDOCK_SELFTEST` + `#if DEBUG`）走真实代码路径，
并用离屏渲染采样核对弹层配色；Release 产物不含这些钩子。明细见 `docs/进度汇总.md`。
