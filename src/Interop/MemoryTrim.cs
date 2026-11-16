using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyDock;

/// <summary>
/// 空闲内存回收：把工作集还给系统（页面转入 standby，再次访问时自动换回）。
/// 只用于"用户已经离开键鼠一段时间"的场景，避免操作途中出现冷启动卡顿。
/// </summary>
internal static class MemoryTrim
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr min, IntPtr max, uint flags);

    /// <summary>距最后一次键鼠输入的秒数。取不到时返回 0（当作正在使用，调用方不要回收）。</summary>
    public static int IdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            uint idle = unchecked((uint)Environment.TickCount - lii.dwTime);
            return (int)(idle / 1000);
        }
        catch { return 0; }
    }

    /// <summary>释放工作集（-1,-1 表示尽可能多地换出）。私有提交量不变，但常驻物理内存会明显下降。</summary>
    public static bool TrimWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return SetProcessWorkingSetSizeEx(p.Handle, new IntPtr(-1), new IntPtr(-1), 0);
        }
        catch { return false; }
    }
}
