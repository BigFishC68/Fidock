using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyDock;

public partial class MainWindow
{
#if DEBUG
    // ---------- 临时自测钩子（仅 DEBUG 构建，Release 不编译） ----------
    // 由环境变量 MYDOCK_SELFTEST 驱动，无需注入鼠标即可验证窗口枚举 / 菜单 / 左键交互：
    //   enum:<程序名>          记录 EnumAppWindows 结果
    //   menu:<程序名>          填充并打开该图标的右键菜单，记录菜单结构
    //   leftclick:<程序名>     走真实 ActivateApp 左键路径（启动/最小化/切换/呼出面板），支持 :N 连续点击
    //   minimize-flow:<程序>   模拟真实点击序列（用户在别的程序→目标到顶层→点 Dock→再左键），验证最小化
    //   restore-flow:<程序>    先最小化再左键，验证恢复
    //   close-item-flow:<程序>:N  左键呼出面板后点击第 N 项行尾的关闭按钮（走真实按钮 Click 路径）
    //   chooser-flow:<程序>:N  左键呼出面板后点击第 N 项（走真实按钮 Click 路径）
    //   running:<程序名>       运行识别诊断（进程名匹配 vs 目录兜底匹配）
    //   visual-menu:<程序名>   渲染右键菜单并采样颜色（验证深色背景/浅色文字）
    //   visual-chooser:<程序>  呼出选择面板并采样颜色
    //   close:<程序名>:N       关闭第 N 个窗口（N=all 关全部）
    //   close-hwnd:123,456     按窗口句柄精确关闭（测试清理用）
    //   focus:<程序名>         激活最上面的窗口并记录前台窗口变化
    private static void Dbg(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "mydock_dbg.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    private void MaybeRunSelfTest()
    {
        string? cmd = Environment.GetEnvironmentVariable("MYDOCK_SELFTEST");
        if (string.IsNullOrWhiteSpace(cmd)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RunSelfTest(cmd)));
    }

    private void RunSelfTest(string cmd)
    {
        try
        {
            if (cmd.StartsWith("enum:"))
            {
                var app = FindAppByName(cmd[5..]);
                if (app == null) { Dbg("ST enum: app-not-found"); return; }
                var wins = EnumAppWindows(app);
                Dbg($"ST enum {app.Name}: count={wins.Count} [{string.Join(" | ", wins.Select(w => $"{w.HWnd}:{w.Title}"))}]");
                IntPtr hidden = FindAppTopWindow(app, visibleOnly: false);
                Dbg($"ST enum {app.Name}: findTop(visibleOnly=false)={hidden}");
            }
            else if (cmd.StartsWith("menu:"))
            {
                var app = FindAppByName(cmd[5..]);
                if (app == null) { Dbg("ST menu: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn?.ContextMenu is not ContextMenu menu) { Dbg("ST menu: button-or-menu-not-found"); return; }
                if (!EnsureMenuParts(menu)) { Dbg("ST menu: parts-not-found"); return; }
                PopulateIconMenu(app);
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                Dbg($"ST menu {app.Name}: kill={_killProcessMenuItem!.Visibility}");
            }
            else if (cmd.StartsWith("leftclick:"))
            {
                // 走真实左键激活路径（ActivateApp）。leftclick:<程序>[:次数] —— 支持连续多次点击
                // （每次点击间隔 800ms 并各记录一行），用于验证 激活→再点→最小化 序列
                var parts = cmd.Split(':');
                var app = FindAppByName(parts[1]);
                if (app == null) { Dbg("ST leftclick: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST leftclick: button-not-found"); return; }
                int times = 1;
                if (parts.Length > 2 && int.TryParse(parts[2], out int t) && t >= 1) times = t;
                Dbg($"ST leftclick {app.Name}: times={times} fore-before={ForeTitle()} wins={EnumAppWindows(app).Count}");

                void ClickOnce(int idx)
                {
                    ActivateApp(app, btn);
                    var tc = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                    tc.Tick += (_, _) =>
                    {
                        tc.Stop();
                        var wins = EnumAppWindows(app);
                        var iconic = wins.Where(w => IsIconic(w.HWnd)).Select(w => w.Title).ToArray();
                        Dbg($"ST leftclick {app.Name}: click#{idx} wins={wins.Count} chooserOpen={WindowListPopup.IsOpen} chooserItems={WindowListItems.Count} iconic=[{string.Join("|", iconic)}] fore={ForeTitle()}");
                    };
                    tc.Start();
                }

                ClickOnce(1);
                if (times > 1)
                {
                    var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    int done = 1;
                    t1.Tick += (_, _) =>
                    {
                        done++;
                        ClickOnce(done);
                        if (done >= times) t1.Stop();
                    };
                    t1.Start();
                }
            }
            else if (cmd.StartsWith("chooser-pick:"))
            {
                // 点击选择面板中第 N 项（走真实按钮 Click 路径）
                if (!int.TryParse(cmd[13..], out int idx) || idx < 1 || idx > WindowListItems.Count)
                { Dbg("ST chooser-pick: bad-index"); return; }
                var entry = WindowListItems[idx - 1];
                var btn = FindVisualChild<Button>(WindowListPopup.Child, b => b.DataContext == entry);
                if (btn == null) { Dbg("ST chooser-pick: button-not-found"); return; }
                Dbg($"ST chooser-pick: '{entry.Title}' fore-before={ForeTitle()}");
                btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                t.Tick += (_, _) =>
                {
                    Dbg($"ST chooser-pick: popupOpen={WindowListPopup.IsOpen} fore-after={ForeTitle()}");
                    t.Stop();
                };
                t.Start();
            }
            else if (cmd.StartsWith("minimize-flow:"))
            {
                // 模拟真实点击序列：① 用户在别的程序 → ② 目标程序到顶层 → ③ 用户点 Dock
                // （Dock 自身成为前台，本进程事件被回调过滤，_lastForegroundHwnd 保持目标窗口）
                // → ④ 再左键 → 应最小化
                var app = FindAppByName(cmd[14..]);
                if (app == null) { Dbg("ST minimize-flow: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST minimize-flow: button-not-found"); return; }
                var wins0 = EnumAppWindows(app);
                if (wins0.Count == 0) { Dbg("ST minimize-flow: no windows"); return; }
                Dbg($"ST minimize-flow {app.Name}: step0 lastFore={_lastForegroundHwnd} fore={ForeTitle()}");

                // ① 先把前台切到一个无关程序（模拟“用户当前在别的窗口”）
                var otherApp = Apps.Concat(FolderApps).FirstOrDefault(a => a != app && EnumAppWindows(a).Count > 0);
                if (otherApp != null)
                {
                    ForceForegroundWindow(EnumAppWindows(otherApp)[0].HWnd);
                }

                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t1.Tick += (_, _) =>
                {
                    t1.Stop();
                    Dbg($"ST minimize-flow {app.Name}: step1-user-in-other lastFore={_lastForegroundHwnd} fore={ForeTitle()}");
                    ForceForegroundWindow(wins0[0].HWnd); // ② 目标程序到顶层
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        Dbg($"ST minimize-flow {app.Name}: step2-target-foreground lastFore={_lastForegroundHwnd} fore={ForeTitle()}");
                        ForceForegroundWindow(new WindowInteropHelper(this).Handle); // ③ Dock 到前台（模拟点击）
                        var t3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        t3.Tick += (_, _) =>
                        {
                            t3.Stop();
                            Dbg($"ST minimize-flow {app.Name}: step3-dock-foreground lastFore={_lastForegroundHwnd} fore={ForeTitle()}");
                            ActivateApp(app, btn); // ④ 再左键 → 应最小化
                            var t4 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                            t4.Tick += (_, _) =>
                            {
                                var wins = EnumAppWindows(app);
                                var iconic = wins.Where(w => IsIconic(w.HWnd)).Select(w => w.Title).ToArray();
                                Dbg($"ST minimize-flow {app.Name}: step4-second-click iconic=[{string.Join("|", iconic)}] chooserOpen={WindowListPopup.IsOpen} fore={ForeTitle()}");
                                t4.Stop();
                            };
                            t4.Start();
                        };
                        t3.Start();
                    };
                    t2.Start();
                };
                t1.Start();
            }
            else if (cmd.StartsWith("running:"))
            {
                // 运行识别诊断：进程名匹配 vs 目录兜底匹配
                var app = FindAppByName(cmd[8..]);
                if (app == null) { Dbg("ST running: app-not-found"); return; }
                UpdateRunningStates();
                var pids = GetAppProcessIds(app);
                bool nameHit = false;
                try
                {
                    nameHit = !string.IsNullOrWhiteSpace(app.RunningKey) &&
                              Process.GetProcessesByName(app.RunningKey).Length > 0;
                }
                catch { }
                Dbg($"ST running {app.Name}: IsRunning={app.IsRunning} key={app.RunningKey} dir={app.RunningDir} nameHit={nameHit} pids={pids.Count} windows={EnumAppWindows(app).Count}");
            }
            else if (cmd.StartsWith("close-item-flow:"))
            {
                // 左键呼出面板 → 点击第 N 项行尾的关闭按钮（走真实按钮 Click 路径）
                var parts = cmd.Split(':');
                var app = FindAppByName(parts[1]);
                if (app == null) { Dbg("ST close-item-flow: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST close-item-flow: button-not-found"); return; }
                if (!int.TryParse(parts[2], out int idx)) { Dbg("ST close-item-flow: bad-index"); return; }
                Dbg($"ST close-item-flow {app.Name}: step1 ActivateApp wins-before={EnumAppWindows(app).Count}");
                ActivateApp(app, btn);
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                t1.Tick += (_, _) =>
                {
                    t1.Stop();
                    Dbg($"ST close-item-flow {app.Name}: chooserOpen={WindowListPopup.IsOpen} items={WindowListItems.Count}");
                    if (WindowListPopup.IsOpen && idx >= 1 && idx <= WindowListItems.Count)
                    {
                        var entry = WindowListItems[idx - 1];
                        var closeBtn = FindVisualChild<Button>(WindowListPopup.Child,
                            b => b.DataContext == entry && Equals(b.Tag, "win-close-item"));
                        if (closeBtn != null)
                        {
                            Dbg($"ST close-item-flow {app.Name}: closing '{entry.Title}'");
                            closeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                        else { Dbg("ST close-item-flow: close-button-not-found"); }
                    }
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                    t2.Tick += (_, _) =>
                    {
                        Dbg($"ST close-item-flow {app.Name}: after popupOpen={WindowListPopup.IsOpen} items={WindowListItems.Count} wins={EnumAppWindows(app).Count}");
                        t2.Stop();
                    };
                    t2.Start();
                };
                t1.Start();
            }
            else if (cmd.StartsWith("restore-flow:"))
            {
                // 验证“最小化后再左键恢复”：先把窗口最小化（模拟用户最小化），再走 ActivateApp
                var app = FindAppByName(cmd[13..]);
                if (app == null) { Dbg("ST restore-flow: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST restore-flow: button-not-found"); return; }
                var wins0 = EnumAppWindows(app);
                if (wins0.Count == 0) { Dbg("ST restore-flow: no windows"); return; }
                ForceForegroundWindow(wins0[0].HWnd);
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t1.Tick += (_, _) =>
                {
                    t1.Stop();
                    foreach (var w in EnumAppWindows(app)) ShowWindow(w.HWnd, SW_MINIMIZE);
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        Dbg($"ST restore-flow {app.Name}: minimized, now clicking (fore={ForeTitle()})");
                        ActivateApp(app, btn);
                        var t3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                        t3.Tick += (_, _) =>
                        {
                            var wins = EnumAppWindows(app);
                            var iconic = wins.Where(w => IsIconic(w.HWnd)).Select(w => w.Title).ToArray();
                            Dbg($"ST restore-flow {app.Name}: after-click iconic=[{string.Join("|", iconic)}] chooserOpen={WindowListPopup.IsOpen} fore={ForeTitle()}");
                            t3.Stop();
                        };
                        t3.Start();
                    };
                    t2.Start();
                };
                t1.Start();
            }
            else if (cmd.StartsWith("visual-menu:"))
            {
                // 打开右键菜单并渲染采样，验证深色背景 + 浅色文字真实生效
                var app = FindAppByName(cmd[12..]);
                if (app == null) { Dbg("ST visual-menu: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn?.ContextMenu is not ContextMenu menu) { Dbg("ST visual-menu: no-menu"); return; }
                if (!EnsureMenuParts(menu)) { Dbg("ST visual-menu: no-parts"); return; }
                PopulateIconMenu(app);
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                // 等弹窗布局完成再渲染（Popup 打开是异步的）
                var vt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                vt.Tick += (_, _) =>
                {
                    vt.Stop();
                    try
                    {
                        var itemsInfo = string.Join(" | ", menu.Items.OfType<MenuItem>().Select(mi => $"{mi.Header}:{mi.Visibility}:h={mi.ActualHeight:0.#}"));
                        Dbg($"ST visual-menu {app.Name}: items=[{itemsInfo}] parent={VisualTreeHelper.GetParent(menu)?.GetType().Name} children={VisualTreeHelper.GetChildrenCount(menu)}");
                        // 渲染 Popup 的 Child（模板根 Border）——直接 Render(menu) 拿不到弹窗内容
                        FrameworkElement target = menu;
                        try
                        {
                            if (VisualTreeHelper.GetParent(menu) is System.Windows.Controls.Primitives.Popup popup && popup.Child is FrameworkElement pc)
                                target = pc;
                            else if (VisualTreeHelper.GetChildrenCount(menu) > 0)
                                target = (FrameworkElement)VisualTreeHelper.GetChild(menu, 0);
                        }
                        catch { }
                        target.UpdateLayout();
                        // 用 ContextMenu 实际宽度（含 MinWidth 约束）渲染，否则只采到左侧勾选列
                        double renderW = target.DesiredSize.Width;
                        if (menu.ActualWidth > renderW) renderW = menu.ActualWidth;
                        double renderH = target.DesiredSize.Height;
                        Dbg($"ST visual-menu {app.Name}: size=({target.DesiredSize.Width:0.#},{target.DesiredSize.Height:0.#}) ctxWidth={menu.ActualWidth:0.#} {SampleVisual(target, Math.Max(1, (int)Math.Ceiling(renderW)), Math.Max(1, (int)Math.Ceiling(renderH)))}");
                        menu.IsOpen = false;
                    }
                    catch (Exception ex) { Dbg($"ST visual-menu error: {ex}"); }
                };
                vt.Start();
            }
            else if (cmd.StartsWith("menu-sep:"))
            {
                // 测量右键菜单里分隔线的真实像素位置（相对弹层根），与菜单项文字左缘对比，验证不再右偏
                var app = FindAppByName(cmd[9..]);
                if (app == null) { Dbg("ST menu-sep: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn?.ContextMenu is not ContextMenu menu) { Dbg("ST menu-sep: no-menu"); return; }
                if (!EnsureMenuParts(menu)) { Dbg("ST menu-sep: no-parts"); return; }
                PopulateIconMenu(app);
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                var mt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                mt.Tick += (_, _) =>
                {
                    mt.Stop();
                    try
                    {
                        FrameworkElement root = menu;
                        if (VisualTreeHelper.GetParent(menu) is System.Windows.Controls.Primitives.Popup popup && popup.Child is FrameworkElement pc)
                            root = pc;
                        else if (VisualTreeHelper.GetChildrenCount(menu) > 0)
                            root = (FrameworkElement)VisualTreeHelper.GetChild(menu, 0);
                        root.UpdateLayout();
                        Rect R(FrameworkElement e) => e.TransformToVisual(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                        var sep = FindVisualChild<Separator>(root);
                        var mi = FindVisualChild<MenuItem>(root);
                        var cp = mi != null ? FindVisualChild<ContentPresenter>(mi) : null;
                        if (sep != null)
                        {
                            var r = R(sep);
                            Dbg($"ST menu-sep sep: left={r.Left:0.#} right={root.ActualWidth - r.Right:0.#} width={r.Width:0.#} (menuW={root.ActualWidth:0.#}) marginL={sep.Margin.Left:0.#}");
                        }
                        else Dbg("ST menu-sep: no-separator-found");
                        if (mi != null)
                        {
                            var r = R(mi);
                            Dbg($"ST menu-sep item: left={r.Left:0.#} width={r.Width:0.#}");
                        }
                        if (cp != null)
                        {
                            var r = R(cp);
                            Dbg($"ST menu-sep content: left={r.Left:0.#} width={r.Width:0.#}");
                        }
                        menu.IsOpen = false;
                    }
                    catch (Exception ex) { Dbg($"ST menu-sep error: {ex}"); }
                };
                mt.Start();
            }
            else if (cmd.StartsWith("visual-chooser:"))
            {
                // 呼出选择面板并渲染采样，验证深色面板 + 浅色文字真实生效
                var app = FindAppByName(cmd[15..]);
                if (app == null) { Dbg("ST visual-chooser: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST visual-chooser: button-not-found"); return; }
                ActivateApp(app, btn);
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    try
                    {
                        if (!WindowListPopup.IsOpen) { Dbg("ST visual-chooser: popup-not-open"); return; }
                        var border = WindowListPopup.Child;
                        if (border == null) { Dbg("ST visual-chooser: no-child"); return; }
                        Dbg($"ST visual-chooser {app.Name}: size=({border.DesiredSize.Width:0.#},{border.DesiredSize.Height:0.#}) {SampleVisual(border, Math.Max(1, (int)border.DesiredSize.Width), Math.Max(1, (int)border.DesiredSize.Height))}");
                        WindowListPopup.IsOpen = false;
                    }
                    catch (Exception ex) { Dbg($"ST visual-chooser error: {ex}"); }
                };
                t.Start();
            }
            else if (cmd.StartsWith("visual-chooser-demo:"))
            {
                // 用合成的多窗口项直接打开选择面板并渲染采样，验证深色面板 + 浅色文字真实生效
                var app = FindAppByName(cmd.Substring("visual-chooser-demo:".Length));
                if (app == null) { Dbg("ST visual-chooser-demo: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                WindowListItems.Clear();
                var icon = app.IconImage;
                WindowListItems.Add(new WindowEntry { HWnd = IntPtr.Zero, Title = app.Name + " - 窗口 一", Icon = icon, IsActive = true });
                WindowListItems.Add(new WindowEntry { HWnd = IntPtr.Zero, Title = app.Name + " - 窗口 二", Icon = icon, IsActive = false });
                if (btn != null) WindowListPopup.PlacementTarget = btn;
                WindowListPopup.IsOpen = true;
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    try
                    {
                        if (!WindowListPopup.IsOpen) { Dbg("ST visual-chooser-demo: popup-not-open"); return; }
                        var border = WindowListPopup.Child;
                        if (border == null) { Dbg("ST visual-chooser-demo: no-child"); return; }
                        Dbg($"ST visual-chooser-demo {app.Name}: size=({border.DesiredSize.Width:0.#},{border.DesiredSize.Height:0.#}) {SampleVisual(border, Math.Max(1, (int)border.DesiredSize.Width), Math.Max(1, (int)border.DesiredSize.Height))}");
                        WindowListPopup.IsOpen = false;
                    }
                    catch (Exception ex) { Dbg($"ST visual-chooser-demo error: {ex}"); }
                };
                t.Start();
            }
            else if (cmd.StartsWith("chooser-flow:"))
            {
                // 全链路：左键 ActivateApp 呼出面板 → 点击第 N 项（走真实按钮 Click）
                var parts = cmd.Split(':');
                var app = FindAppByName(parts[1]);
                if (app == null) { Dbg("ST chooser-flow: app-not-found"); return; }
                var btn = FindButtonForApp(app);
                if (btn == null) { Dbg("ST chooser-flow: button-not-found"); return; }
                if (!int.TryParse(parts[2], out int idx)) { Dbg("ST chooser-flow: bad-index"); return; }
                Dbg($"ST chooser-flow {app.Name}: step1 ActivateApp fore-before={ForeTitle()}");
                ActivateApp(app, btn);
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t1.Tick += (_, _) =>
                {
                    t1.Stop();
                    Dbg($"ST chooser-flow {app.Name}: chooserOpen={WindowListPopup.IsOpen} items={WindowListItems.Count}");
                    if (WindowListPopup.IsOpen && idx >= 1 && idx <= WindowListItems.Count)
                    {
                        var entry = WindowListItems[idx - 1];
                        var item = FindVisualChild<Button>(WindowListPopup.Child, b => b.DataContext == entry);
                        if (item != null)
                        {
                            Dbg($"ST chooser-flow {app.Name}: picking '{entry.Title}'");
                            item.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                        else { Dbg("ST chooser-flow: item-button-not-found"); }
                    }
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    t2.Tick += (_, _) =>
                    {
                        Dbg($"ST chooser-flow {app.Name}: popupOpen={WindowListPopup.IsOpen} fore-after={ForeTitle()}");
                        t2.Stop();
                    };
                    t2.Start();
                };
                t1.Start();
            }
            else if (cmd.StartsWith("close:"))
            {
                var parts = cmd.Split(':');
                var app = FindAppByName(parts[1]);
                if (app == null) { Dbg("ST close: app-not-found"); return; }
                var wins = EnumAppWindows(app);
                Dbg($"ST close {app.Name}: before={wins.Count} [{string.Join(" | ", wins.Select(w => w.Title))}]");
                if (parts.Length > 2 && parts[2] == "all")
                {
                    foreach (var w in wins) CloseWindowGraceful(w.HWnd);
                }
                else if (parts.Length > 2 && int.TryParse(parts[2], out int idx) && idx >= 1 && idx <= wins.Count)
                {
                    CloseWindowGraceful(wins[idx - 1].HWnd);
                }
                else { Dbg("ST close: bad-index"); return; }
                RefreshRunningSoon();
            }
            else if (cmd.StartsWith("close-hwnd:"))
            {
                // close-hwnd:123,456 —— 按窗口句柄精确关闭（测试清理用，不碰进程名匹配）
                foreach (var part in cmd[11..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out long h) && h != 0)
                    {
                        Dbg($"ST close-hwnd: {h}");
                        CloseWindowGraceful((IntPtr)h);
                    }
                }
                RefreshRunningSoon();
            }
            else if (cmd.StartsWith("focus:"))
            {
                var app = FindAppByName(cmd[6..]);
                if (app == null) { Dbg("ST focus: app-not-found"); return; }
                var wins = EnumAppWindows(app);
                Dbg($"ST focus {app.Name}: count={wins.Count} fore-before={ForeTitle()}");
                if (wins.Count > 0) ForceForegroundWindow(wins[0].HWnd);
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                t.Tick += (_, _) => { Dbg($"ST focus {app.Name}: fore-after={ForeTitle()}"); t.Stop(); };
                t.Start();
            }
        }
        catch (Exception ex) { Dbg($"ST error: {ex}"); }
    }

    private static string ForeTitle()
    {
        var h = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return $"{h}:'{sb}'";
    }

    // 把视觉树渲染到位图，统计亮/暗像素并采样关键点颜色（无截图验证弹层配色）
    private static string SampleVisual(Visual visual, int w, int h)
    {
        try
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            long bright = 0, dark = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                int lum = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000;
                if (lum > 140) bright++;
                else if (lum < 70) dark++;
            }
            string At(double fx, double fy)
            {
                int x = Math.Min(w - 1, Math.Max(0, (int)(w * fx)));
                int y = Math.Min(h - 1, Math.Max(0, (int)(h * fy)));
                int idx = (y * w + x) * 4;
                return $"#{px[idx + 2]:X2}{px[idx + 1]:X2}{px[idx]:X2}";
            }
            int total = w * h;
            return $"bg={At(0.5, 0.06)} mid={At(0.5, 0.5)} bright%={100.0 * bright / total:0.#} dark%={100.0 * dark / total:0.#}";
        }
        catch (Exception ex) { return $"sample-error: {ex.Message}"; }
    }

    private DockApp? FindAppByName(string name)
    {
        if (string.Equals(ExplorerApp?.Name, name, StringComparison.OrdinalIgnoreCase)) return ExplorerApp;
        if (string.Equals(RecycleApp?.Name, name, StringComparison.OrdinalIgnoreCase)) return RecycleApp;
        return Apps.Concat(FolderApps)
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private Button? FindButtonForApp(DockApp app)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(this); i++)
        {
            var r = FindButtonForApp(VisualTreeHelper.GetChild(this, i), app);
            if (r != null) return r;
        }
        return null;
    }

    private static Button? FindButtonForApp(DependencyObject parent, DockApp app)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button b && b.DataContext == app) return b;
            var inner = FindButtonForApp(child, app);
            if (inner != null) return inner;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent, Func<T, bool> predicate) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && predicate(t)) return t;
            var inner = FindVisualChild(child, predicate);
            if (inner != null) return inner;
        }
        return null;
    }
#endif
}
