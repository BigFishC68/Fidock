using System;
using System.Threading;
using System.Windows;

namespace MyDock;

/// <summary>
/// 应用入口：只负责单实例互斥与生命周期，窗口唤出细节见 <see cref="SingleInstance"/>。
/// </summary>
public partial class App : Application
{
    // 单实例互斥体（按用户会话隔离，多会话/远程桌面各自独立）
    private const string InstanceGuid = "MyDock-SingleInstance-{B7E4A2D1-3C5F-4E8A-9D6B-1F2A3C4E5D70}";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceGuid, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：唤出已存在的 Dock 后直接退出，避免叠加/配置互相覆盖
            SingleInstance.ActivateExisting();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 主窗口在这里显式创建（XAML 位于 src/ 子目录，StartupUri 的相对路径解析不可靠）
        var dock = new MyDock.MainWindow();
        MainWindow = dock;
        dock.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }
}
