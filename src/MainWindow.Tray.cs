using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace MyDock;

/// <summary>
/// 托盘图标：Shell_NotifyIcon 常驻通知区，双击唤出 Dock，右键弹出与 Dock 空白处相同的菜单。
/// </summary>
public partial class MainWindow
{
    private const int WM_TRAYICON = 0x8001;         // WM_APP + 1
    private const int NIM_ADD = 0, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    private HwndSource? _trayHook;
    private IntPtr _trayIcon;
    private bool _trayAdded;

    private void InitTrayIcon()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            _trayHook ??= HwndSource.FromHwnd(hwnd);
            _trayHook?.AddHook(TrayWndProc);
            AddTrayIcon(hwnd);
        }
        catch { }
    }

    private void AddTrayIcon(IntPtr hwnd)
    {
        // 从自身 exe 取第一个图标组；取不到退回系统默认图标
        if (_trayIcon == IntPtr.Zero && !string.IsNullOrEmpty(Environment.ProcessPath))
        {
            ExtractIconEx(Environment.ProcessPath, 0, out _, out IntPtr small, 1);
            _trayIcon = small;
        }
        if (_trayIcon == IntPtr.Zero) _trayIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIcon,
            szTip = "MyDock",
            szInfo = "",
            szInfoTitle = ""
        };
        _trayAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void RemoveTrayIcon()
    {
        try
        {
            if (_trayAdded)
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = new WindowInteropHelper(this).Handle,
                    uID = 1
                };
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _trayAdded = false;
            }
            if (_trayHook != null)
            {
                _trayHook.RemoveHook(TrayWndProc);
                _trayHook = null;
            }
            if (_trayIcon != IntPtr.Zero)
            {
                DestroyIcon(_trayIcon);
                _trayIcon = IntPtr.Zero;
            }
        }
        catch { }
    }

    private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启后通知区会被清空，重新注册一次
        if (msg == WM_TASKBARCREATED)
        {
            _trayAdded = false;
            AddTrayIcon(hwnd);
            return IntPtr.Zero;
        }

        if (msg != WM_TRAYICON) return IntPtr.Zero;

        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WM_LBUTTONDBLCLK:
                ForceForegroundWindow(hwnd);
                handled = true;
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowTrayMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        var menu = DockArea.ContextMenu;
        if (menu == null) return;

        AutoStartMenu.IsChecked = IsAutoStartEnabled();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
}
