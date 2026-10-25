using System;

namespace MyDock;

/// <summary>
/// 可加入 Dock 的路径类型判定。拖放与「添加程序…」共用同一套规则，
/// 纯函数（无 IO、无 UI），便于直接断言。
/// </summary>
public static class AppPaths
{
    // .url 网页快捷方式、.bat/.cmd 脚本：桌面上很常见，之前拖进 Dock 没反应
    private static readonly string[] LaunchableExtensions =
    {
        ".exe", ".lnk", ".url", ".bat", ".cmd"
    };

    public static bool IsLaunchable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var ext in LaunchableExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
