using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyDock;

/// <summary>
/// 单实例协作：新进程发现已有实例时，把已存在的 Dock 唤到前台后自行退出。
/// </summary>
internal static class SingleInstance
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);

    /// <summary>唤出当前会话中已运行的 MyDock 主窗口（失败时静默，不影响新进程退出）。</summary>
    public static void ActivateExisting()
    {
        try
        {
            int currentId = Environment.ProcessId;
            string name = Process.GetCurrentProcess().ProcessName;
            Process? target = null;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id != currentId) { target = p; break; }
            }
            if (target == null) return;

            IntPtr h = target.MainWindowHandle;
            if (h == IntPtr.Zero)
            {
                target.Refresh();
                h = target.MainWindowHandle;
            }
            if (h == IntPtr.Zero) return;

            // 借助输入线程挂接绕过前台窗口限制，把已有 Dock 提到前面
            IntPtr fore = GetForegroundWindow();
            int foreThread = GetWindowThreadProcessId(fore, out _);
            int curThread = GetCurrentThreadId();
            bool attached = false;
            if (foreThread != 0 && foreThread != curThread)
                attached = AttachThreadInput(curThread, foreThread, true);
            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
            finally
            {
                if (attached) AttachThreadInput(curThread, foreThread, false);
            }
        }
        catch { }
    }
}
