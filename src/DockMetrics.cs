using System;
using System.ComponentModel;

namespace MyDock;

/// <summary>
/// Dock 的尺寸度量：图标大小可调（空白处右键 → 图标大小），分隔线等比跟随。
/// 用单例 + <c>x:Static</c> 绑定而不是 RelativeSource：文件夹面板与多窗口面板是独立的
/// Popup 视觉树，AncestorType 找不到 Window，x:Static 在哪都能解析到同一份数值。
/// </summary>
public sealed class DockMetrics : INotifyPropertyChanged
{
    public static DockMetrics Current { get; } = new();

    public const double MinIconSize = 28;
    public const double MaxIconSize = 48;
    public const double DefaultIconSize = 32;

    private double _iconSize = DefaultIconSize;

    public double IconSize
    {
        get => _iconSize;
        set
        {
            double v = Math.Clamp(value, MinIconSize, MaxIconSize);
            if (Math.Abs(v - _iconSize) < 0.5) return;
            _iconSize = v;
            OnPropertyChanged(nameof(IconSize));
            OnPropertyChanged(nameof(ButtonSize));
            OnPropertyChanged(nameof(SeparatorHeight));
            OnPropertyChanged(nameof(CornerRadius));
        }
    }

    /// <summary>图标按钮边长：图标四周各留 4px（32 → 40，与原样式一致）。</summary>
    public double ButtonSize => _iconSize + 8;

    /// <summary>分隔线高度：保持原来的 26/32 比例。</summary>
    public double SeparatorHeight => Math.Round(_iconSize * 26 / 32);

    public double CornerRadius => Math.Round(_iconSize * 16 / 32);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
