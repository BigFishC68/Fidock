using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MyDock;

public partial class MainWindow : Window
{
    // 仅用户项（不含固定项）；跨栏移动时在两个集合之间迁移
    public ObservableCollection<DockApp> Apps { get; } = new();
    public ObservableCollection<DockApp> FolderApps { get; } = new();

    // 文件夹面板视图：只显示未运行的文件夹项
    public ICollectionView FolderPopupView { get; }

    // Dock 栏“运行中”区域视图：只显示运行中的文件夹项（位于回收站左侧）
    public ICollectionView RunningFolderView { get; }

    // 左键多窗口选择面板的数据源
    public ObservableCollection<WindowEntry> WindowListItems { get; } = new();

    // 固定项单独持有（不参与 Apps 集合），便于布局与分隔
    public DockApp? ExplorerApp { get; set; }
    public DockApp? FolderApp { get; set; }
    public DockApp? RecycleApp { get; set; }

    private readonly string _configDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyDock");

    private string ConfigFile => Path.Combine(_configDirectory, "apps.json");

    // 内部拖拽状态（不用 DoDragDrop：OLE 会吞掉松手事件导致单击失效）
    private DockApp? _dragApp;
    private Button? _dragButton;
    private ObservableCollection<DockApp>? _dragSourceList;
    private Point _dragStartPoint;
    private POINT _dragStartScreen; // 按下时的光标屏幕坐标（用于点击/拖拽判定）
    private bool _internalDragging;
    private bool _dragCancelled;    // Esc 取消了拖拽

    // 最近一个非本进程的前台窗口：点击 Dock 时 Dock 自身成为前台，
    // 靠它还原点击前的顶层程序，才能判断「再左键 = 最小化」还是「切换」
    private IntPtr _lastForegroundHwnd;

    // 多窗口选择面板当前对应的程序（关闭单项后刷新面板用）
    private DockApp? _chooserApp;
    private bool _targetBackfilled; // 加载时补齐了 TargetPath，需要写回配置

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // 运行指示轮询（轻量）
    private readonly DispatcherTimer _runningTimer = new() { Interval = TimeSpan.FromMilliseconds(2000) };

    // 前台窗口轮询：维护“最近一个非本进程的前台窗口”（点击 Dock 时 Dock 自身成前台，需还原点击前的顶层程序）
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private const int SM_CXDRAG = 36;
    private const int SM_CYDRAG = 37;

    // 按下到松手的位移小于该值按点击处理（指针带加速，真实点击手抖可达 ~20px；图标间距约 46px）
    private const double ClickSlopPx = 30;

    public MainWindow()
    {
        InitializeComponent();

        MaxWidth = SystemParameters.WorkArea.Width * 0.95;

        // 文件夹项的两个视图：面板只显示未运行项；Dock 栏运行区只显示运行中项
        FolderPopupView = new ListCollectionView(FolderApps) { Filter = o => o is DockApp d && !d.IsRunning };
        RunningFolderView = new ListCollectionView(FolderApps) { Filter = o => o is DockApp d && d.IsRunning };

        LoadApps();
        DataContext = this;

        Apps.CollectionChanged += (_, _) => ScheduleRecenter();
        FolderApps.CollectionChanged += (_, _) => ScheduleRecenter();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded 可能因可见性切换再次触发；只绑定一次，避免事件重复订阅
        Loaded -= OnLoaded;

        // 先强制布局，确保 ActualWidth 就绪，再居中，避免重启后偏右下
        UpdateLayout();
        Recenter();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Recenter));
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Recenter));

        ApplyTheme();
        DwmBlur.Enable(this);

        AutoStartMenu.IsChecked = IsAutoStartEnabled();
        InitTrayIcon();

        // 系统主题 / 分辨率 / 任务栏变化时自动适配（事件在非 UI 线程抛出，需转回）
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnClosed;

        // 记录点击前的顶层窗口（Dock 自己会成为前台，见 _lastForegroundHwnd）
        _foregroundTimer.Tick += (_, _) =>
        {
            IntPtr fore = GetForegroundWindow();
            if (fore == IntPtr.Zero) return;
            GetWindowThreadProcessId(fore, out uint pid);
            if (pid != Environment.ProcessId) _lastForegroundHwnd = fore;
        };
        _foregroundTimer.Start();

        _runningTimer.Tick += (_, _) => UpdateRunningStates();
        _runningTimer.Start();
        UpdateRunningStates();

#if DEBUG
        MaybeRunSelfTest();
#endif
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RemoveTrayIcon();

        // 两个轮询定时器都要停：窗口已销毁，继续 Tick 只会访问失效的视觉树
        _foregroundTimer.Stop();
        _runningTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyTheme();
            Recenter();
        }));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MaxWidth = SystemParameters.WorkArea.Width * 0.95;
            Recenter();
        }));
    }

    // ---------- 居中（向两侧延伸） ----------
    private void Recenter()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 8;
    }

    private void ScheduleRecenter()
        => Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Recenter));

    // ---------- 主题与背景 ----------
    // 统一黑色风格：Dock 栏与文件夹上拉面板使用相同的深色背景 / 边框 / 分隔线
    private void ApplyTheme()
    {
        DockArea.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F));
        DockArea.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        DockArea.BorderThickness = new Thickness(1);

        var sep = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        FolderSep.Fill = new SolidColorBrush(sep);
        RecycleSep.Fill = new SolidColorBrush(sep);
    }

    // ---------- 图标加载 ----------
    private void LoadAppIcon(DockApp app)
    {
        if (app.IsFolder) { app.IconImage = GetStockIcon(SIID_FOLDER); app.RunningKey = null; return; }
        if (app.IsRecycleBin) { app.IconImage = GetStockIcon(SIID_RECYCLER); app.RunningKey = null; return; }

        // .lnk 只解析一次，图标与运行检测共用结果（避免重复创建 COM 对象拖慢启动）
        bool isLnk = app.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        string target = app.Path;
        if (isLnk)
        {
            // 优先使用已持久化的源程序路径（快捷方式可能已被删除）；
            // 首次加载则现场解析一次，写回配置——之后删掉快捷方式也不受影响
            var t = !string.IsNullOrWhiteSpace(app.TargetPath)
                ? app.TargetPath
                : ResolveLnkTarget(app.Path);
            if (!string.IsNullOrWhiteSpace(t))
            {
                target = t;
                if (app.TargetPath != t) _targetBackfilled = true;
                app.TargetPath = t;
            }
        }

        // 图标：源程序存在 → 用源程序图标；源失效（程序被移动/卸载）→ 退回快捷方式自身图标
        bool targetAlive = !isLnk || File.Exists(target) || Directory.Exists(target);
        string iconSource = targetAlive ? target : app.Path;
        app.IconImage = GetShellIcon(ResolveExecutablePath(iconSource) ?? iconSource)
                     ?? GetShellIcon(app.Path)
                     ?? GetStockIcon(SIID_FOLDER);

        // 运行检测：取目标 exe 的进程名；目标不是 exe（目录 / UWP / 已失效）则不检测
        app.RunningKey = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(target)
            : null;

        // 目录兜底：启动器型快捷方式（WPS 的 ksolaunch、QQ 的 QQScLauncher）启动后即退出，
        // 真正的主程序在同目录或版本子目录下，进程名匹配不到时按目录前缀识别
        app.RunningDir = null;
        try
        {
            if (Path.IsPathRooted(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string full = Path.GetFullPath(target);
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(dir) &&
                    !Path.GetPathRoot(full)!.Equals(dir, StringComparison.OrdinalIgnoreCase))
                    app.RunningDir = dir;
            }
        }
        catch { }
    }

    private static ImageSource? GetShellIcon(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            IntPtr r = SHGetFileInfo(
                path, 0, out SHFILEINFO info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON);
            hIcon = info.hIcon;
            if (r != IntPtr.Zero && hIcon != IntPtr.Zero)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bmp.Freeze();
                return bmp;
            }
        }
        catch { }
        finally { if (hIcon != IntPtr.Zero) DestroyIcon(hIcon); }
        return null;
    }

    private static ImageSource? GetStockIcon(int siid)
    {
        var info = new SHSTOCKICONINFO { cbSize = Marshal.SizeOf<SHSTOCKICONINFO>() };
        try
        {
            int hr = SHGetStockIconInfo(siid, SHGSI_ICON | SHGSI_LARGEICON, ref info);
            if (hr == 0 && info.hIcon != IntPtr.Zero)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bmp.Freeze();
                return bmp;
            }
        }
        catch { }
        finally { if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon); }
        return null;
    }

    private static string? ResolveExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return Path.GetFullPath(path);

        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), path);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    // 复用的 WScript.Shell COM 实例：每个快捷方式都新建一次会明显拖慢启动
    private object? _wshShell;

    /// <summary>解析 .lnk 快捷方式指向的真实目标路径（使用 WSH 晚绑定，无需 COM 引用）。</summary>
    private string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            if (_wshShell == null)
            {
                var progId = Type.GetTypeFromProgID("WScript.Shell");
                if (progId == null) return null;
                _wshShell = Activator.CreateInstance(progId);
                if (_wshShell == null) return null;
            }

            object? shortcut = _wshShell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, _wshShell,
                new object[] { lnkPath });
            if (shortcut == null) return null;

            string? target = shortcut.GetType().InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;

            if (shortcut is IDisposable d) d.Dispose();

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    // ---------- 固定项 / 文件夹 构造 ----------
    private DockApp CreateFileExplorer()
    {
        var app = new DockApp { Name = "资源管理器", Path = "explorer.exe", IsFixed = true };
        LoadAppIcon(app);
        return app;
    }

    private DockApp CreateRecycleBin()
    {
        var app = new DockApp
        {
            Name = "回收站",
            Path = "::{645FF040-5081-101B-9F08-00AA002F954E}",
            IsFixed = true,
            IsRecycleBin = true
        };
        app.IconImage = GetStockIcon(SIID_RECYCLER);
        return app;
    }

    private DockApp CreateFolder()
    {
        var app = new DockApp { Name = "文件夹", IsFolder = true };
        app.IconImage = GetStockIcon(SIID_FOLDER);
        return app;
    }

    // ---------- 配置读写 ----------
    private sealed class DockConfig
    {
        public List<DockApp>? Apps { get; set; }
        public List<DockApp>? FolderApps { get; set; }
    }

    private void LoadApps()
    {
        bool loaded = false;
        _targetBackfilled = false;
        try
        {
            if (File.Exists(ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(ConfigFile));
                if (cfg != null)
                {
                    loaded = true;
                    // 加载时跨两栏去重，兼容历史配置里可能存在的重复项
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in cfg.Apps ?? new List<DockApp>())
                        if (!string.IsNullOrWhiteSpace(a.Path) && !a.IsFixed && !a.IsFolder && seen.Add(a.Path))
                        { LoadAppIcon(a); Apps.Add(a); }
                    foreach (var a in cfg.FolderApps ?? new List<DockApp>())
                        if (!string.IsNullOrWhiteSpace(a.Path) && seen.Add(a.Path))
                        { LoadAppIcon(a); FolderApps.Add(a); }
                }
            }
        }
        catch
        {
            // 配置损坏：先备份原文件，避免之后一次保存就把用户数据覆盖丢失
            try { File.Copy(ConfigFile, ConfigFile + ".corrupt", overwrite: true); }
            catch { }
        }

        if (!loaded)
        {
            Apps.Add(new DockApp { Name = "记事本", Path = "notepad.exe" });
            Apps.Add(new DockApp { Name = "计算器", Path = "calc.exe" });
            Apps.Add(new DockApp { Name = "画图", Path = "mspaint.exe" });
            FolderApps.Add(new DockApp { Name = "控制面板", Path = "control.exe" });
            FolderApps.Add(new DockApp { Name = "命令提示符", Path = "cmd.exe" });
            foreach (var a in Apps) LoadAppIcon(a);
            foreach (var a in FolderApps) LoadAppIcon(a);
        }

        // 固定项单独构造（不进入 Apps 集合）
        ExplorerApp = CreateFileExplorer();
        FolderApp = CreateFolder();
        RecycleApp = CreateRecycleBin();

        // 懒迁移：把解析到的源程序路径写回配置，之后删掉快捷方式也不受影响
        if (_targetBackfilled) SaveApps();

        ReleaseLnkShell();
    }

    /// <summary>释放加载期复用的 WScript.Shell COM 对象（之后新增条目时会按需重建）。</summary>
    private void ReleaseLnkShell()
    {
        var shell = _wshShell;
        _wshShell = null;
        if (shell == null) return;
        try { Marshal.FinalReleaseComObject(shell); } catch { }
    }

    private void SaveApps()
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var cfg = new DockConfig
            {
                Apps = Apps.ToList(),
                FolderApps = FolderApps.ToList()
            };
            File.WriteAllText(ConfigFile,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------- 构建应用（去重 + 解析名称） ----------
    private DockApp? BuildApp(string path, IEnumerable<DockApp> existing)
    {
        if (existing.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            return null;

        string name = Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var t = ResolveLnkTarget(path);
            if (!string.IsNullOrWhiteSpace(t))
            {
                name = Path.GetFileNameWithoutExtension(t);
                name = ReadFileDescription(t) ?? name;
            }
        }
        else
        {
            name = ReadFileDescription(path) ?? name;
        }

        var app = new DockApp { Name = name, Path = path };
        LoadAppIcon(app);
        return app;
    }

    private bool AddAppByPath(string path)
    {
        // 跨 Dock 与文件夹两个列表去重，避免重复图标
        var app = BuildApp(path, Apps.Concat(FolderApps));
        if (app == null) return false;
        Apps.Insert(0, app); // 紧贴文件夹分隔符之后
        return true;
    }

    private bool AddFolderAppByPath(string path)
    {
        var app = BuildApp(path, Apps.Concat(FolderApps));
        if (app == null) return false;
        FolderApps.Add(app);
        return true;
    }

    private static string? ReadFileDescription(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(vi.FileDescription) ? null : vi.FileDescription.Trim();
        }
        catch { return null; }
    }

    // ---------- 启动应用 ----------

    /// <summary>
    /// 取实际启动 / 定位用的路径：
    /// ① 已持久化且存在的源程序（.lnk 解析结果，删了快捷方式也能用）；
    /// ② 现场解析 .lnk；③ 退回原始路径。
    /// </summary>
    private string EffectiveTarget(DockApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.TargetPath) &&
            (File.Exists(app.TargetPath) || Directory.Exists(app.TargetPath)))
            return app.TargetPath;

        if (app.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var t = ResolveLnkTarget(app.Path);
            if (!string.IsNullOrWhiteSpace(t))
            {
                app.TargetPath = t;
                return t;
            }
        }
        return app.Path;
    }

    /// <summary>
    /// 启动用路径：快捷方式还在就用 .lnk 本身，交给 shell 还原它的参数和工作目录
    /// （Ansys 的快捷方式带 -shortcut，直接跑 exe 会静默退出）；快捷方式已删才退回源程序。
    /// </summary>
    private string LaunchPath(DockApp app)
        => app.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(app.Path)
            ? app.Path
            : EffectiveTarget(app);

    private void LaunchApp(DockApp app)
    {
        try
        {
            if (app.Path.StartsWith("::", StringComparison.OrdinalIgnoreCase))
            {
                // 回收站等 shell CLSID
                Process.Start("explorer.exe", app.Path);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = LaunchPath(app),
                UseShellExecute = true
            });
            BeginStarting(app);
            // 刚启动的程序可能只能靠目录兜底识别，立刻刷新（RefreshRunningSoon 会让路径索引失效）
            RefreshRunningSoon();
        }
        catch (Exception ex)
        {
            ShowLaunchError(app, ex);
        }
    }

    // 启动中：图标灰显 + 忽略重复点击，避免"点了没反应又点一次"开出两个实例
    private void BeginStarting(DockApp app)
    {
        app.IsStarting = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            app.IsStarting = false;
        };
        timer.Start();
    }

    // 启动失败：在图标提示上临时显示原因，3 秒后恢复
    private void ShowLaunchError(DockApp app, Exception ex)
    {
        app.IsStarting = false;
        app.LaunchHint = $"启动失败：{ex.Message}";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            app.LaunchHint = "";
            timer.Stop();
        };
        timer.Start();
    }

    // ---------- 图标按下 / 拖拽 / 抬起 ----------
    // 说明：不再使用 DragDrop.DoDragDrop。OLE 拖拽循环会吞掉松手事件，
    // 点击时鼠标稍一抖动（>4px）就会进入拖拽、点击被"吃掉"——这正是单击打不开的原因。
    // 现在改为：按下即捕获鼠标（Element 模式），移动/松手都在窗口级处理器中完成，
    // 松手事件稳定到达，点击与拖拽的判定完全确定。
    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 上一次手势可能残留的状态先清掉
        if (_dragApp != null) _dragApp.IsDragging = false;

        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not Button { DataContext: DockApp app }) return;

        _dragApp = app;
        _dragButton = sender as Button;
        _dragStartPoint = e.GetPosition(this);
        GetCursorPos(out _dragStartScreen);
        _internalDragging = false;
        _dragCancelled = false;

        var ic = FindAncestor<ItemsControl>((DependencyObject)sender);
        // 运行中区域与文件夹面板里的项都属于 FolderApps（支持跨区拖回文件夹）
        _dragSourceList = (ic == FolderItems || ic == RunningFolderItems) ? FolderApps : Apps;

        // 按下即捕获（默认 Element 模式：鼠标移出图标/窗口也不丢捕获），
        // 保证之后的移动与松手事件一定经过窗口级处理器
        Mouse.Capture(_dragButton);
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragApp == null || _dragCancelled) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_internalDragging) return; // 拖拽中不需要处理，松手时才计算落点

        var delta = e.GetPosition(this) - _dragStartPoint;
        int dx = GetSystemMetrics(SM_CXDRAG);
        int dy = GetSystemMetrics(SM_CYDRAG);
        if (Math.Abs(delta.X) < Math.Max(4, dx) && Math.Abs(delta.Y) < Math.Max(4, dy)) return;

        // 固定项 / 文件夹不参与拖动（仍可点击）
        if (_dragApp.IsFixed || _dragApp.IsFolder) return;

        // 超过系统拖拽阈值 → 进入拖拽状态
        _internalDragging = true;
        _dragApp.IsDragging = true;
        if (_dragButton != null)
        {
            _dragButton.Cursor = Cursors.SizeAll;
            Mouse.Capture(_dragButton); // 再次确保 Element 模式捕获
        }
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragApp == null) return;

        if (Mouse.Captured != null) Mouse.Capture(null);

        var app = _dragApp;
        var btn = _dragButton;
        var sourceList = _dragSourceList ?? Apps;

        GetCursorPos(out POINT up);
        double travel = Math.Sqrt(
            Math.Pow(up.X - _dragStartScreen.X, 2) +
            Math.Pow(up.Y - _dragStartScreen.Y, 2));

        // 位移在容差内 → 视为点击。不能附加 "!_internalDragging"：固定项不参与拖拽，
        // 该标志恒为 false，拖动固定项后会误判成点击；位移超容差且未进入拖拽则什么都不做
        bool treatAsClick = !_dragCancelled && travel <= ClickSlopPx;
        bool wasDrag = _internalDragging && !_dragCancelled;

        // 复位视觉状态
        app.IsDragging = false;
        if (btn != null) btn.Cursor = Cursors.Hand;

        if (treatAsClick)
        {
            ActivateApp(app, btn);
        }
        else if (wasDrag && !app.IsFixed && !app.IsFolder)
        {
            // 只做排序/跨栏移动。拖到栏外松手 = 取消，删除只走右键菜单
            var target = ResolveDropTarget(app);
            if (target != null) MoveItem(app, sourceList, target.Value.list, target.Value.index);
        }

        _dragApp = null;
        _dragButton = null;
        _dragSourceList = null;
        _internalDragging = false;
        _dragCancelled = false;

        // 抑制 Button.Click：启动已在本方法中处理，避免重复
        e.Handled = true;
    }

    // Esc 取消当前拖拽（还原位置，不启动不删除）
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_dragApp == null || e.Key != Key.Escape) return;

        _dragCancelled = true;
        _internalDragging = false;
        _dragApp.IsDragging = false;
        if (_dragButton != null) _dragButton.Cursor = Cursors.Hand;
        if (Mouse.Captured != null) Mouse.Capture(null);
        e.Handled = true;
    }

    // 解析松手落点：返回 (目标列表, 插入索引)；null 表示栏外
    private (ObservableCollection<DockApp> list, int index)? ResolveDropTarget(DockApp source)
    {
        var mouseWin = Mouse.GetPosition(this);

        // 1) 文件夹上拉面板（Popup 是独立 HwndSource，用自身命中测试 + 屏幕矩形兜底）
        if (FolderPopup.IsOpen)
        {
            try
            {
                var pop = VisualTreeHelper.HitTest(FolderPopupBorder, Mouse.GetPosition(FolderPopupBorder));
                var popBtn = FindAncestor<Button>(pop?.VisualHit);
                if (popBtn?.DataContext is DockApp popOwner && popOwner != source)
                {
                    int pi = FolderApps.IndexOf(popOwner);
                    bool pBefore = Mouse.GetPosition(popBtn).X < popBtn.ActualWidth / 2;
                    return (FolderApps, pBefore ? pi : pi + 1);
                }
            }
            catch { }
            if (IsOverElementRect(FolderPopupBorder))
                return (FolderApps, FolderApps.Count);
        }

        // 2) 窗口内命中测试：主栏图标 / 固定项
        var hit = VisualTreeHelper.HitTest(this, mouseWin);
        var hitBtn = FindAncestor<Button>(hit?.VisualHit);
        if (hitBtn?.DataContext is DockApp owner && owner != source)
        {
            if (owner.IsFolder) return (FolderApps, FolderApps.Count);       // 拖到文件夹图标 → 进面板
            if (owner.IsRecycleBin) return (Apps, Apps.Count);               // 拖到回收站 → 主栏末尾
            if (owner.IsFixed) return (Apps, 0);                             // 拖到资源管理器 → 主栏开头
            var list = Apps.Contains(owner) ? Apps : FolderApps;
            int idx = list.IndexOf(owner);
            bool before = Mouse.GetPosition(hitBtn).X < hitBtn.ActualWidth / 2;
            return (list, before ? idx : idx + 1);
        }

        // 3) Dock 栏空白处：按光标横向位置估算插入点
        if (IsPointerOverDockArea(hit?.VisualHit))
        {
            var panel = GetItemsPanel(AppItems);
            int idx = panel != null ? ComputeDropIndex(panel, Mouse.GetPosition(panel)) : Apps.Count;
            return (Apps, idx);
        }

        // 4) 栏外
        return null;
    }

    // 光标是否落在指定元素的屏幕矩形内（用于跨 HwndSource 的弹出面板）
    private static bool IsOverElementRect(FrameworkElement el)
    {
        try
        {
            GetCursorPos(out POINT p);
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            return p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y;
        }
        catch { return false; }
    }

    // 从命中结果沿视觉树向上找 DockArea（判断是否落在栏内空白处）
    private bool IsPointerOverDockArea(DependencyObject? hit)
    {
        var el = hit;
        while (el != null)
        {
            if (ReferenceEquals(el, DockArea)) return true;
            el = el is Visual v ? VisualTreeHelper.GetParent(v) : LogicalTreeHelper.GetParent(el);
        }
        return false;
    }

    // 图标激活（左键）——窗口管理：
    //   文件夹     → 展开 / 收起上拉面板
    //   未运行     → 启动程序
    //   已在顶层   → 最小化其所有窗口（任务栏行为）
    //   单窗口     → 切换到该窗口
    //   多窗口     → 在图标上方呼出选择面板，由用户挑选切换到哪个窗口
    private void ActivateApp(DockApp app, Button? btn)
    {
        if (app.IsFolder)
        {
            ToggleFolderPopup(btn);
            return;
        }

        if (app.IsRecycleBin) // 回收站：直接打开
        {
            LaunchApp(app);
            return;
        }

        if (app.IsStarting) return; // 启动中，忽略重复点击

        var appPids = GetAppProcessIds(app);
        var windows = EnumAppWindows(app);

        if (windows.Count == 0)
        {
            // 看不见主界面有四种情况：贴边收起（旧版 QQ 把主面板平移出屏幕、只留几像素）、
            // 关闭到托盘（窗口还在，只清掉 WS_VISIBLE）、最小化、真的没启动。
            // 前三种都按"拉回屏内 + 显示 + 取消最小化"处理，只有第四种才重新启动。
            IntPtr panel = FindAppMainPanel(app);
            if (panel != IntPtr.Zero)
            {
                if (FolderPopup.IsOpen && FolderApps.Contains(app)) FolderPopup.IsOpen = false;

                // 已完整在屏且就是顶层 → 与任务栏一致：再按一次是最小化
                if (IsWindowVisible(panel) && !IsIconic(panel) && !WindowReveal.IsSlidOut(panel) &&
                    (panel == GetForegroundWindow() || panel == _lastForegroundHwnd))
                {
                    ShowWindow(panel, SW_MINIMIZE);
                    return;
                }

                RevealAppPanel(panel);
                return;
            }
            LaunchApp(app);
            if (FolderPopup.IsOpen && FolderApps.Contains(app)) FolderPopup.IsOpen = false;
            return;
        }

        IntPtr fore = GetForegroundWindow();
        GetWindowThreadProcessId(_lastForegroundHwnd, out uint lastFgPid);
        // 已在顶层 → 再左键即最小化。Dock 自身成了前台，所以同时认 _lastForegroundHwnd（排除已最小化的）
        bool onTop = windows.Any(w => !IsIconic(w.HWnd) && (w.HWnd == fore || w.HWnd == _lastForegroundHwnd));
        // 兜底：前台窗口可能不在枚举结果里（被父窗口拥有等），但其进程确属本程序 → 仍判定在顶层
        if (!onTop && _lastForegroundHwnd != IntPtr.Zero && !IsIconic(_lastForegroundHwnd) && appPids.Contains((int)lastFgPid))
            onTop = true;
        if (onTop)
        {
            var toMin = windows.Where(w => !IsIconic(w.HWnd)).Select(w => w.HWnd).ToList();
            if (_lastForegroundHwnd != IntPtr.Zero && !IsIconic(_lastForegroundHwnd) && appPids.Contains((int)lastFgPid) && !toMin.Contains(_lastForegroundHwnd))
                toMin.Add(_lastForegroundHwnd);
            foreach (var h in toMin) ShowWindow(h, SW_MINIMIZE);
            return;
        }

        if (FolderPopup.IsOpen && FolderApps.Contains(app)) FolderPopup.IsOpen = false;

        if (windows.Count == 1)
        {
            // 贴边收起的窗口在部分版本里仍保持 WS_VISIBLE（只是移出了屏幕），
            // 这里顺手夹回屏内，避免"点了有响应但屏幕上什么都没出现"
            WindowReveal.ShowAndBringOnScreen(windows[0].HWnd);
            ForceForegroundWindow(windows[0].HWnd);
            return;
        }

        ShowWindowChooser(app, btn, windows);
    }

    /// <summary>
    /// 把收起 / 隐藏 / 最小化的主面板拉出来。收起是"移出屏幕"而非改变尺寸，
    /// 所以还原只动位置，面板回到收起前所在的那一侧。
    /// </summary>
    private void RevealAppPanel(IntPtr hWnd)
    {
        WindowReveal.ShowAndBringOnScreen(hWnd);
        ForceForegroundWindow(hWnd);
        RecheckReveal(hWnd, 2);
    }

    /// <summary>
    /// 短时复检：程序自己的"鼠标离开就收起"定时器可能立刻又把面板滑回屏幕外，
    /// 所以隔一会儿再拉一次，给用户把鼠标移过去的时间。
    /// 只在 Dock 仍持有前台时补一次置前，避免用户已经点开别的窗口后被抢焦点。
    /// </summary>
    private void RecheckReveal(IntPtr hWnd, int attempts)
    {
        if (attempts <= 0) return;
        IntPtr dock = new WindowInteropHelper(this).Handle;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
            if (IsIconic(hWnd) || !IsWindowVisible(hWnd) || WindowReveal.IsSlidOut(hWnd))
            {
                WindowReveal.ShowAndBringOnScreen(hWnd);
                RecheckReveal(hWnd, attempts - 1);
            }
            else if (dock != IntPtr.Zero && GetForegroundWindow() == dock)
            {
                ForceForegroundWindow(hWnd);
            }
        };
        t.Start();
    }

    /// <summary>在图标上方呼出多窗口选择面板。</summary>
    private void ShowWindowChooser(DockApp app, Button? btn, List<AppWindow> windows)
    {
        _chooserApp = app;
        WindowListItems.Clear();
        IntPtr fore = GetForegroundWindow();
        foreach (var w in windows)
        {
            WindowListItems.Add(new WindowEntry
            {
                HWnd = w.HWnd,
                Title = w.Title,
                Icon = app.IconImage,
                IsActive = w.HWnd == fore
            });
        }
        if (WindowListItems.Count == 0) return;
        if (btn != null) WindowListPopup.PlacementTarget = btn;
        WindowListPopup.IsOpen = true;
    }

    private void WindowListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowEntry entry })
        {
            WindowListPopup.IsOpen = false;
            WindowReveal.ShowAndBringOnScreen(entry.HWnd);
            ForceForegroundWindow(entry.HWnd);
        }
    }

    // 选择面板中单项右侧的关闭按钮：关闭该窗口并刷新面板，全部关完则收起面板
    private void WindowItemClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WindowEntry entry }) return;
        CloseWindowGraceful(entry.HWnd);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { t.Stop(); RefreshChooser(); };
        t.Start();
    }

    private void RefreshChooser()
    {
        if (_chooserApp == null) { WindowListPopup.IsOpen = false; return; }
        var windows = EnumAppWindows(_chooserApp);
        if (windows.Count == 0)
        {
            WindowListItems.Clear();
            WindowListPopup.IsOpen = false;
            _chooserApp = null;
            return;
        }
        IntPtr fore = GetForegroundWindow();
        WindowListItems.Clear();
        foreach (var w in windows)
        {
            WindowListItems.Add(new WindowEntry
            {
                HWnd = w.HWnd,
                Title = w.Title,
                Icon = _chooserApp.IconImage,
                IsActive = w.HWnd == fore
            });
        }
    }

    // 中键点击：强制打开新窗口（不受“左键聚焦已有窗口”规则影响）。
    // WPF 没有独立的中键路由事件，借通用 PreviewMouseUp 判断 ChangedButton。
    private void Icon_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return; // 左键路径已由窗口级处理器接管
        if (_dragApp != null) return;                     // 正在拖拽中，不响应
        if (sender is not Button { DataContext: DockApp app }) return;

        if (app.IsFolder)
        {
            ToggleFolderPopup(sender as Button);
        }
        else
        {
            LaunchApp(app);
            if (FolderPopup.IsOpen && FolderApps.Contains(app)) FolderPopup.IsOpen = false;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 可靠地把窗口带到前台（最小化先还原）。
    /// SetForegroundWindow 受前台进程限制，借 AttachThreadInput 临时共享输入队列突破。
    /// </summary>
    private static void ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            IntPtr fore = GetForegroundWindow();
            uint foreThread = GetWindowThreadProcessId(fore, out _);
            uint myThread = GetCurrentThreadId();
            // 把前台线程附加到当前线程：共享其输入队列即继承前台资格，
            // 突破 SetForegroundWindow 的前台进程限制（方向不能反）
            if (foreThread != myThread && foreThread != 0)
            {
                AttachThreadInput(foreThread, myThread, true);
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
                AttachThreadInput(foreThread, myThread, false);
            }
            else
            {
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
            }
        }
        catch { }
    }

    /// <summary>程序的一个顶层窗口（开始栏式窗口列表用）。</summary>
    private sealed class AppWindow
    {
        public IntPtr HWnd { get; init; }
        public string Title { get; init; } = "";
    }

    /// <summary>左键多窗口选择面板中的一项。</summary>
    public sealed class WindowEntry
    {
        public IntPtr HWnd { get; init; }
        public string Title { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public bool IsActive { get; init; }
    }

    private static Dictionary<int, int> GetProcessParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snap == (IntPtr)(-1)) return map;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
            {
                do { map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
                while (Process32Next(snap, ref pe));
            }
        }
        catch { }
        finally { try { CloseHandle(snap); } catch { } }
        return map;
    }

    // 把 basePids 扩展到其全部后代进程（BFS），用于多进程程序的窗口归属。
    private static HashSet<int> ExpandWithDescendants(HashSet<int> basePids)
    {
        if (basePids.Count == 0) return basePids;
        var parentMap = GetProcessParentMap();
        if (parentMap.Count == 0) return basePids;

        var children = new Dictionary<int, List<int>>();
        foreach (var kv in parentMap)
        {
            if (!children.TryGetValue(kv.Value, out var list))
            { list = new List<int>(); children[kv.Value] = list; }
            list.Add(kv.Key);
        }

        var result = new HashSet<int>(basePids);
        var queue = new Queue<int>(basePids);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (children.TryGetValue(cur, out var kids))
                foreach (var k in kids)
                    if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }

    /// <summary>
    /// 取程序的运行进程集合：先按进程名精确匹配；匹配不到时按“源程序目录
    /// （含版本子目录）兜底”——WPS/QQ 这类启动器型快捷方式的目标进程名
    /// 与真正运行的主程序不同，靠目录关联识别。
    /// 返回的 pid 会扩展到子进程树（见 ExpandWithDescendants），以覆盖
    /// 主进程自身无窗口、窗口实际由其子进程持有的多进程程序（如 Steam）。
    /// </summary>
    private static HashSet<int> GetAppProcessIds(DockApp app)
    {
        var pids = new HashSet<int>();
        bool nameMatched = false;
        if (!string.IsNullOrWhiteSpace(app.RunningKey))
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(app.RunningKey))
                {
                    using (p) { pids.Add(p.Id); nameMatched = true; }
                }
            }
            catch { }
        }
        if (nameMatched || string.IsNullOrWhiteSpace(app.RunningDir)) return MaybeExpandChildren(app, pids);

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string? mp = null;
                    try { mp = p.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrEmpty(mp) &&
                        mp.StartsWith(app.RunningDir, StringComparison.OrdinalIgnoreCase))
                        pids.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return MaybeExpandChildren(app, pids);
    }

    // explorer.exe 是 Shell：用户启动的程序几乎都是它的子孙，扩展子进程会把全系统窗口都算成资源管理器窗口，
    // 因此对它禁用子进程扩展（它自己的文件夹窗口都在 explorer.exe 进程内，按进程名即可命中）
    private static HashSet<int> MaybeExpandChildren(DockApp app, HashSet<int> pids)
        => string.Equals(app.RunningKey, "explorer", StringComparison.OrdinalIgnoreCase)
            ? pids
            : ExpandWithDescendants(pids);

    // explorer.exe 还拥有 Network Flyout、音量浮窗等带标题的可见浮窗（ATL 类），
    // 只有 CabinetWClass / ExploreWClass 才是真正的文件窗口
    private static bool IsExplorerFileWindow(string cls)
        => cls == "CabinetWClass" || cls == "ExploreWClass";

    /// <summary>
    /// 枚举指定程序当前打开的顶层窗口（排除桌面 / 任务栏 / 附属窗 / 工具窗 / 无标题窗）。
    /// 资源管理器：每个文件窗口都是独立进程，与任务栏进程同名，靠窗口类名与标题过滤。
    /// </summary>
    private static List<AppWindow> EnumAppWindows(DockApp app)
    {
        var list = new List<AppWindow>();
        if (app == null) return list;

        var pids = GetAppProcessIds(app);
        if (pids.Count == 0) return list;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                // 不排除“被父窗口拥有”的窗口：Steam 主窗口带隐藏 Owner，排除就永远枚举不到
                if ((GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!pids.Contains((int)pid)) return true;

                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    return true;                                                        // 桌面 / 任务栏
                // 资源管理器只管理真正的文件窗口类，排除其拥有的 Network Flyout / 音量浮窗 等浮窗
                if (string.Equals(app.RunningKey, "explorer", StringComparison.OrdinalIgnoreCase)
                    && !IsExplorerFileWindow(cls.ToString()))
                    return true;

                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                list.Add(new AppWindow { HWnd = hWnd, Title = Truncate(title, 48) });
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>一次枚举收集所有拥有可见顶层窗口的 pid（运行检测用，避免逐 pid 反复 EnumWindows）。</summary>
    private static HashSet<int> BuildVisiblePidSet()
    {
        var set = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    set.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    /// <summary>
    /// 一次枚举收集"拥有像主面板的顶层窗口"的 pid（标题非空 + 不小于 180×180，不看是否可见）。
    /// 旧版 QQ 收起主面板时窗口既隐藏又带 WS_EX_TOOLWINDOW，只看可见窗口会把它判成未运行；
    /// 标题与尺寸两条门槛把 13×13 / 135×67 辅助窗以及无窗口的后台助手挡在外面。
    /// </summary>
    private static HashSet<int> BuildMainPanelPidSet()
    {
        var set = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (GetWindowTextLength(hWnd) <= 0) return true;
                GetWindowRect(hWnd, out RECT r);
                long w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 180 || h < 180) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                set.Add((int)pid);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    /// <summary>
    /// 取该程序"像主窗口"的顶层窗口（visibleOnly=false 时含隐藏/托盘窗口），用于托盘还原。
    /// 必须挑有标题、且不小于 120×80 的那个：QQ/Steam 有几十个隐藏辅助窗
    /// （TXGuiFoundation 272×128、MyWing 0×0、vguiPopupWindow 43×16 等），
    /// 把它们 ShowWindow 出来就是用户看到的"莫名其妙的窗口"。
    /// </summary>
    private static IntPtr FindAppTopWindow(DockApp app, bool visibleOnly)
        => FindAppWindow(app, visibleOnly, allowToolWindow: false, minW: 120, minH: 80);

    /// <summary>
    /// 找主面板（贴边收起 / 关闭到托盘后的还原目标）。
    /// 与 FindAppTopWindow 的差别是允许 WS_EX_TOOLWINDOW 与隐藏窗口——旧版 QQ 主面板两者都是，
    /// 这也正是它一直被漏掉的原因：既不在可见窗口集合里，又被工具窗过滤挡掉。
    /// 用 180×180 的下限加"必须有标题"把这些辅助窗挡在外面：
    /// 13×13、135×67、17×69 尺寸不够，272×236 那个没有标题，只有 300×600 的 'QQ' 主面板命中。
    /// </summary>
    private static IntPtr FindAppMainPanel(DockApp app)
        => FindAppWindow(app, visibleOnly: false, allowToolWindow: true, minW: 180, minH: 180);

    private static IntPtr FindAppWindow(DockApp app, bool visibleOnly, bool allowToolWindow, int minW, int minH)
    {
        var pids = GetAppProcessIds(app);
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (visibleOnly && !IsWindowVisible(hWnd)) return true;
                if (!allowToolWindow &&
                    (GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!pids.Contains((int)pid)) return true;

                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                string clsStr = cls.ToString();
                if (clsStr is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                if (string.Equals(app.RunningKey, "explorer", StringComparison.OrdinalIgnoreCase)
                    && !IsExplorerFileWindow(clsStr)) return true;

                if (GetWindowTextLength(hWnd) <= 0) return true;
                GetWindowRect(hWnd, out RECT r);
                long w = Math.Max(0, r.Right - r.Left), h = Math.Max(0, r.Bottom - r.Top);
                if (w < minW || h < minH) return true;

                if (w * h > bestArea) { bestArea = w * h; best = hWnd; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return best;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>优雅关闭窗口（等同点击标题栏 ×）；UWP 窗口不响应 WM_CLOSE，直接结束其进程。</summary>
    private static void CloseWindowGraceful(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        try
        {
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            if (cls.ToString() == "Windows.UI.Core.CoreWindow")
            {
                KillWindowProcess(hWnd);
                return;
            }
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static void KillWindowProcess(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            p.Kill();
        }
        catch { }
    }

    /// <summary>结束程序的所有进程（强制）。资源管理器与任务栏同进程名，禁用。</summary>
    private static void KillAppProcesses(DockApp app)
    {
        if (app == null || string.IsNullOrWhiteSpace(app.RunningKey)) return;
        if (string.Equals(app.RunningKey, "explorer", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            foreach (var p in Process.GetProcessesByName(app.RunningKey))
            {
                try { using (p) p.Kill(); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>程序启停后立即刷新运行状态（进程退出/窗口出现是异步的，稍后再补一次）。</summary>
    private void RefreshRunningSoon()
    {
        InvalidatePathIndex();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateRunningStates));
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { try { UpdateRunningStates(); } catch { } t.Stop(); };
        t.Start();
    }

    // Click 事件：服务于 UIA InvokePattern 与键盘（Space / Enter）激活。
    // 鼠标路径已在 Window_PreviewMouseLeftButtonUp 中处理并置 e.Handled，Click 不会触发，不会重复激活。
    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app }) return;
        ActivateApp(app, sender as Button);
    }

    // ---------- 拖拽悬停 / 放下（仅处理来自外部的文件拖放） ----------
    private void Icon_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp owner }) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (owner.IsRecycleBin) e.Effects = DragDropEffects.Move;        // 移入回收站
            else if (owner.IsFolder) e.Effects = files.All(IsExecutable) ? DragDropEffects.Move : DragDropEffects.None;
            else e.Effects = DragDropEffects.Copy;                          // 用该软件打开
            e.Handled = true;
        }
    }

    private void Icon_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp owner }) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (owner.IsFolder)
            {
                bool any = false;
                foreach (var f in files)
                    if (IsExecutable(f) && AddFolderAppByPath(f)) any = true;
                if (any) SaveApps();
            }
            else
            {
                HandleFilesOnApp(owner, files);
            }
            e.Handled = true;
        }
    }

    private void DockArea_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Effects = files.All(IsExecutable) ? DragDropEffects.Move : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DockArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool any = false;
            foreach (var f in files)
                if (IsExecutable(f) && AddAppByPath(f)) any = true;
            if (any) SaveApps();
            e.Handled = true;
        }
    }

    private void FolderPopup_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool any = false;
            foreach (var f in files)
                if (IsExecutable(f) && AddFolderAppByPath(f)) any = true;
            if (any) SaveApps();
            e.Handled = true;
        }
    }

    // 在 sourceList 中移除 source，插入到 targetList 的 targetIndex 处（支持跨栏）
    private void MoveItem(DockApp source, ObservableCollection<DockApp> sourceList,
        ObservableCollection<DockApp> targetList, int targetIndex)
    {
        if (!sourceList.Contains(source)) return;
        int oldIdx = sourceList.IndexOf(source);
        sourceList.RemoveAt(oldIdx);

        int idx = targetIndex;
        if (targetList == sourceList && oldIdx < idx) idx--;
        idx = Math.Clamp(idx, 0, targetList.Count);

        targetList.Insert(idx, source);
        SaveApps();
    }

    // ---------- 文件到软件 / 回收站 的操作 ----------
    private void HandleFilesOnApp(DockApp owner, string[] files)
    {
        foreach (var f in files)
        {
            if (owner.IsRecycleBin) MoveToRecycleBin(f);
            else OpenWithApp(owner, f);
        }
    }

    private void OpenWithApp(DockApp app, string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = EffectiveTarget(app),
                Arguments = $"\"{file}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // 将文件移入回收站（FOF_ALLOWUNDO）
    private static void MoveToRecycleBin(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 0x0003, // FO_DELETE
                pFrom = path + "\0\0",
                fFlags = 0x0040 | 0x0010 | 0x0400 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI
            };
            SHFileOperation(ref op);
        }
        catch { }
    }

    private static int ComputeDropIndex(Panel panel, Point p)
    {
        int index = panel.Children.Count;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            var topLeft = child.TranslatePoint(new Point(0, 0), panel);
            double centerX = topLeft.X + child.RenderSize.Width / 2;
            if (p.X < centerX) { index = i; break; }
        }
        return index;
    }

    private static bool IsExecutable(string path)
        => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    // ---------- 文件夹上拉面板 ----------
    private void ToggleFolderPopup(Button? folderButton)
    {
        if (FolderPopup.IsOpen) { FolderPopup.IsOpen = false; return; }
        if (folderButton != null) FolderPopup.PlacementTarget = folderButton;
        FolderPopup.IsOpen = true;
    }

    // ---------- 右键菜单 ----------

    // 共享右键菜单中的动态项（资源字典里的 x:Name 不生成字段，按 Tag 解析一次缓存）
    private MenuItem? _killProcessMenuItem;

    private bool EnsureMenuParts(ContextMenu menu)
    {
        if (_killProcessMenuItem != null) return true;
        _killProcessMenuItem = FindMenuPart<MenuItem>(menu, "win-kill");
        return _killProcessMenuItem != null;
    }

    private static T? FindMenuPart<T>(ItemsControl parent, object tag) where T : FrameworkElement
    {
        foreach (var item in parent.Items)
        {
            if (item is T t && Equals(t.Tag, tag)) return t;
            if (item is MenuItem mi)
            {
                var inner = FindMenuPart<T>(mi, tag);
                if (inner != null) return inner;
            }
        }
        return null;
    }

    // 右键弹出前：按运行状态决定“结束进程”是否显示
    private void Icon_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app }) return;
        // 固定项（资源管理器 / 回收站）无可管理项：结束进程对资源管理器禁用
        // （任务栏同进程名），打开文件位置 / 移除对固定项隐藏 → 不弹菜单
        if (app.IsFixed) { e.Handled = true; return; }
        if (sender is Button { ContextMenu: ContextMenu menu } && EnsureMenuParts(menu))
            PopulateIconMenu(app);
    }

    private void PopulateIconMenu(DockApp app)
    {
        if (_killProcessMenuItem == null) return;
        bool canKill = (EnumAppWindows(app).Count > 0 || app.IsRunning) &&
            !string.Equals(app.RunningKey, "explorer", StringComparison.OrdinalIgnoreCase);
        _killProcessMenuItem.Visibility = canKill ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KillProcessMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: DockApp app })
        {
            KillAppProcesses(app);
            RefreshRunningSoon();
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: DockApp app }) return;
        if (app.IsFixed || app.IsFolder || app.IsPlaceholder) return;

        if (FolderApps.Contains(app)) FolderApps.Remove(app);
        else if (Apps.Contains(app)) Apps.Remove(app);
        SaveApps();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: DockApp app }) return;

        // 定位到源程序目录：快捷方式被删除也能正确定位
        string target = EffectiveTarget(app);
        try
        {
            if (File.Exists(target))
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            else if (Directory.Exists(target))
                Process.Start("explorer.exe", $"\"{target}\"");
            else
            {
                // 源程序与快捷方式都已失效：退回原路径所在目录
                string? dir = Path.GetDirectoryName(app.Path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }
        catch { }
    }

    // ---------- 视觉树辅助 ----------
    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var inner = FindVisualChild<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    private static Panel? GetItemsPanel(ItemsControl ic)
    {
        var presenter = FindVisualChild<ItemsPresenter>(ic);
        if (presenter == null) return null;
        return FindVisualChild<Panel>(presenter);
    }

    // ---------- 运行指示 ----------
    private void UpdateRunningStates()
    {
        // 快照 1：进程名 → pid 集合，走一次 Toolhelp 快照（不创建 Process 对象、不碰句柄）
        var nameIndex = SnapshotProcessNames();
        if (nameIndex.Count == 0) return;

        // 快照 2：进程路径索引，带 5 秒过期；仅在需要目录兜底时使用
        // 快照 3：拥有可见顶层窗口的 pid，同一 tick 内只枚举一次
        // 快照 4：拥有"像主面板"窗口的 pid（不看可见性），按需构建
        HashSet<int>? visiblePids = null;
        HashSet<int>? panelPids = null;

        bool AnyRunning(DockApp app)
        {
            // 进程名命中即视为已启动（含驻留托盘、无可见窗口的程序）
            if (!string.IsNullOrWhiteSpace(app.RunningKey) &&
                nameIndex.TryGetValue(app.RunningKey, out var s) && s.Count > 0) return true;
            // 目录兜底：要求该目录下存在带可见窗口的进程，排除无窗口的后台助手
            if (!string.IsNullOrWhiteSpace(app.RunningDir))
            {
                visiblePids ??= BuildVisiblePidSet();
                foreach (var (id, path) in GetPathIndex())
                {
                    if (string.IsNullOrEmpty(path) ||
                        !path.StartsWith(app.RunningDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (visiblePids.Contains(id)) return true;
                    // 旧版 QQ 收起主面板时把窗口移出屏幕并清掉 WS_VISIBLE，
                    // 按"有可见窗口"判断会误报未运行，这里补一次"有主面板窗口"的判定
                    panelPids ??= BuildMainPanelPidSet();
                    if (panelPids.Contains(id)) return true;
                }
            }
            return false;
        }

        bool changed = false;
        foreach (var app in Apps)
        {
            if (app.IsFolder || app.IsPlaceholder || app.IsFixed) continue;
            bool now = AnyRunning(app);
            if (now) app.IsStarting = false;
            if (app.IsRunning != now) { app.IsRunning = now; changed = true; }
        }
        foreach (var app in FolderApps)
        {
            bool now = AnyRunning(app);
            if (now) app.IsStarting = false;
            if (app.IsRunning != now) { app.IsRunning = now; changed = true; }
        }

        // 文件夹项运行状态变化 → 在“Dock 栏运行区”与“文件夹面板”之间迁移显示
        if (changed)
        {
            FolderPopupView.Refresh();
            RunningFolderView.Refresh();
            ScheduleRecenter(); // 运行区出现/消失会改变栏宽，重新居中
        }
    }

    /// <summary>进程名 → pid 集合。Toolhelp 一次快照，避免为每个进程创建 Process 对象。</summary>
    private static Dictionary<string, HashSet<int>> SnapshotProcessNames()
    {
        var index = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == new IntPtr(-1)) return index;
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref pe)) return index;
            do
            {
                string name = pe.szExeFile ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                if (name.Length == 0) continue;
                if (!index.TryGetValue(name, out var set)) index[name] = set = new HashSet<int>();
                set.Add((int)pe.th32ProcessID);
            }
            while (Process32Next(snap, ref pe));
        }
        catch { }
        finally { if (snap != IntPtr.Zero && snap != new IntPtr(-1)) CloseHandle(snap); }
        return index;
    }

    // 进程路径索引缓存：整表构建要逐进程 OpenProcess，5 秒内复用；启停程序时主动失效
    private static List<(int Id, string? Path)>? _pathIndex;
    private static long _pathIndexStamp;
    private const int PathIndexTtlMs = 5000;

    private static void InvalidatePathIndex() => _pathIndex = null;

    private static List<(int Id, string? Path)> GetPathIndex()
    {
        long now = Environment.TickCount64;
        var cache = _pathIndex;
        if (cache != null && now - _pathIndexStamp < PathIndexTtlMs) return cache;
        _pathIndex = cache = BuildPathIndex();
        _pathIndexStamp = now;
        return cache;
    }

    private static readonly StringBuilder PathBuffer = new(1024);

    private static string? GetProcessPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = PathBuffer.Capacity;
            return QueryFullProcessImageName(h, 0, PathBuffer, ref size)
                ? PathBuffer.ToString(0, size)
                : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static List<(int Id, string? Path)> BuildPathIndex()
    {
        var list = new List<(int, string?)>();
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == new IntPtr(-1)) return list;
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref pe)) return list;
            do
            {
                int pid = (int)pe.th32ProcessID;
                list.Add((pid, GetProcessPath(pid)));
            }
            while (Process32Next(snap, ref pe));
        }
        catch { }
        finally { if (snap != IntPtr.Zero && snap != new IntPtr(-1)) CloseHandle(snap); }
        return list;
    }

    // ---------- 开机自启动 ----------
    private void AutoStart_Changed(object sender, RoutedEventArgs e)
        => SetAutoStart(AutoStartMenu.IsChecked);

    private void SetAutoStart(bool? enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enable == true)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe)) key.SetValue("MyDock", exe);
            }
            else
            {
                key.DeleteValue("MyDock", false);
            }
        }
        catch { }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue("MyDock") != null;
        }
        catch { return false; }
    }

    // ---------- 添加 / 批量移除 / 配置目录 ----------
    private void AddApps_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要添加到 Dock 的程序",
            Filter = "程序与快捷方式|*.exe;*.lnk;*.bat;*.cmd|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        bool added = false;
        foreach (var path in dlg.FileNames) added |= AddAppByPath(path);
        if (!added) return;

        SaveApps();
        RefreshRunningSoon();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            Process.Start(new ProcessStartInfo { FileName = _configDirectory, UseShellExecute = true });
        }
        catch { }
    }

    private void BatchRemove_Click(object sender, RoutedEventArgs e)
    {
        var w = new BatchRemoveWindow(Apps, FolderApps) { Owner = this };
        w.OnRemoved = SaveApps;
        w.ShowDialog();
    }

    // ---------- 退出 ----------
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}
