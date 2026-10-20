using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace MyDock;

public sealed class DockApp : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>.lnk 解析出的源程序路径，随配置持久化：快捷方式被删也能启动/定位。</summary>
    public string? TargetPath { get; set; }

    [JsonIgnore] public ImageSource? IconImage { get; set; }

    private bool _isRunning;
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); } }
    }

    // 用于运行检测的进程名（.lnk 已解析为真实目标 exe 的名称；空 = 不检测）
    [JsonIgnore] public string? RunningKey { get; set; }

    /// <summary>源程序目录（不持久化）。启动器型快捷方式（WPS/QQ）靠它做运行状态兜底匹配。</summary>
    [JsonIgnore] public string? RunningDir { get; set; }

    private bool _isDragging;

    /// <summary>是否正在被拖拽（用于半透明反馈）。</summary>
    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging != value) { _isDragging = value; OnPropertyChanged(); } }
    }

    private bool _isStarting;

    /// <summary>已发出启动请求、进程尚未出现：图标灰显，并忽略重复点击。</summary>
    [JsonIgnore]
    public bool IsStarting
    {
        get => _isStarting;
        set { if (_isStarting != value) { _isStarting = value; OnPropertyChanged(); } }
    }

    private string _launchHint = "";

    /// <summary>启动失败时的临时提示（几秒后自动清除）。</summary>
    [JsonIgnore]
    public string LaunchHint
    {
        get => _launchHint;
        set
        {
            if (_launchHint == value) return;
            _launchHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>提示文本：正常显示名称，启动失败时显示错误原因。</summary>
    public string ToolTipText => string.IsNullOrEmpty(_launchHint) ? Name : _launchHint;

    // 固定项（资源管理器、回收站）不可移除 / 不可拖动
    [JsonIgnore] public bool IsFixed { get; set; }

    // 回收站（特殊 CLSID，单独取图标 / 启动）
    [JsonIgnore] public bool IsRecycleBin { get; set; }

    // 文件夹项（呼出上拉面板），不可移除 / 不可拖动
    [JsonIgnore] public bool IsFolder { get; set; }

    // 拖拽时的占位格子（保留间距，自身不可见）
    [JsonIgnore] public bool IsPlaceholder { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
