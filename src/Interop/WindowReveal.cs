using System;
using System.Runtime.InteropServices;

namespace MyDock;

/// <summary>
/// 把"贴边收起 / 滑出屏幕 / 关闭到托盘 / 最小化"的窗口拉回屏幕并显示。
///
/// 旧版 QQ 收起主面板的做法：保持原始尺寸，把窗口整体平移出屏幕只留几像素，
/// 同时清掉 WS_VISIBLE（且带 WS_EX_TOOLWINDOW）——实测 300×600 的主面板停在 (1231,-595)-(1531,5)。
/// 所以还原只需沿"被隐藏的那条边"平移回屏内，不需要模拟鼠标划过屏幕边缘
/// （那属于输入注入，会真实干扰用户操作）。
///
/// 判定只对"大到像主面板"的窗口生效：QQ 的 13×13 / 135×67 / 272×236 等辅助窗一律不动，
/// 避免重演"点一下弹出一个莫名其妙的小窗"。
/// </summary>
public static class WindowReveal
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // 主面板尺寸下限：小于它的是辅助窗
    private const int MinPanelW = 180;
    private const int MinPanelH = 180;
    // 距屏幕边缘在此范围内，才算"贴着边滑出去"（而非随便飘在屏外）
    private const int EdgeSlop = 16;
    // 屏内可见面积占比低于 15% 视为收起；正常窗口哪怕被拖到只剩一条边在屏内，也远高于此值
    private const int OnScreenMinPermille = 150;

    /// <summary>窗口是否被收起/滑出了屏幕（只对"像主面板"的窗口判定）。</summary>
    public static bool IsSlidOut(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (!GetWindowRect(hWnd, out RECT r)) return false;
        if (r.Right - r.Left < MinPanelW || r.Bottom - r.Top < MinPanelH) return false;
        if (!TryGetMonitor(hWnd, out RECT bounds, out _)) return false;
        return OnScreenPermille(r, bounds) < OnScreenMinPermille;
    }

    /// <summary>
    /// 显示窗口、取消最小化，并在滑出屏幕时把它平移回屏内。
    /// 返回是否真的做了动作（调用方据此决定要不要复检）。
    /// </summary>
    public static bool ShowAndBringOnScreen(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        bool acted = false;

        if (IsIconic(hWnd))
        {
            // SW_RESTORE 同时负责取消最小化与显示
            ShowWindowAsync(hWnd, SW_RESTORE);
            acted = true;
        }
        else if (!IsWindowVisible(hWnd))
        {
            ShowWindowAsync(hWnd, SW_SHOW);
            acted = true;
        }

        if (TryGetRestoreSpot(hWnd, out int x, out int y) && GetWindowRect(hWnd, out RECT r)
            && (r.Left != x || r.Top != y))
        {
            // 跨进程移动用异步投递：目标进程若正忙，不至于把 Dock 的 UI 线程一起卡住
            SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            acted = true;
        }

        return acted;
    }

    /// <summary>
    /// 算出"拉回屏内"的目标位置（只改位置、不改尺寸）。窗口正常在屏上时返回 false。
    /// 只沿被隐藏的那条轴平移，另一轴原样保留——这样主面板会回到它收起前所在的那一侧，
    /// 不会跳到别的显示器或屏幕中央。
    /// </summary>
    public static bool TryGetRestoreSpot(IntPtr hWnd, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (!GetWindowRect(hWnd, out RECT r)) return false;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < MinPanelW || h < MinPanelH) return false;
        if (!TryGetMonitor(hWnd, out RECT bounds, out RECT work)) return false;
        if (OnScreenPermille(r, bounds) >= OnScreenMinPermille) return false;

        int nx = r.Left;
        int ny = r.Top;
        if (r.Bottom <= work.Top + EdgeSlop) ny = work.Top;                    // 从顶边滑出
        else if (r.Top >= work.Bottom - EdgeSlop) ny = work.Bottom - h;         // 从底边滑出
        else if (r.Right <= work.Left + EdgeSlop) nx = work.Left;               // 从左边滑出
        else if (r.Left >= work.Right - EdgeSlop) nx = work.Right - w;          // 从右边滑出

        // 兜底：显示器被拔掉/改分辨率后窗口留在不存在的区域，直接夹回工作区
        x = Math.Clamp(nx, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(ny, work.Top, Math.Max(work.Top, work.Bottom - h));
        return true;
    }

    private static bool TryGetMonitor(IntPtr hWnd, out RECT bounds, out RECT work)
    {
        bounds = default;
        work = default;
        IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        bounds = mi.rcMonitor;
        work = mi.rcWork;
        return true;
    }

    /// <summary>窗口落在显示器范围内的面积占比（千分数）。</summary>
    private static long OnScreenPermille(RECT r, RECT bounds)
    {
        long w = Math.Max(0, r.Right - r.Left);
        long h = Math.Max(0, r.Bottom - r.Top);
        if (w == 0 || h == 0) return 0;
        long ix = Math.Max(0, Math.Min(r.Right, bounds.Right) - Math.Max(r.Left, bounds.Left));
        long iy = Math.Max(0, Math.Min(r.Bottom, bounds.Bottom) - Math.Max(r.Top, bounds.Top));
        return ix * iy * 1000 / (w * h);
    }
}
