using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MyDock;

public partial class BatchRemoveWindow : Window
{
    // 列表中的一项：记录对应 DockApp、所属集合、分组名、是否勾选
    private sealed class BatchEntry
    {
        public DockApp App { get; init; } = null!;
        public ObservableCollection<DockApp> List { get; init; } = null!;
        public string Group { get; init; } = "";
        public bool IsSelected { get; set; }
        public string Name => App.Name;
        public ImageSource? IconImage => App.IconImage;
    }

    private readonly ObservableCollection<BatchEntry> _entries = new();

    public Action? OnRemoved { get; set; }

    public BatchRemoveWindow(ObservableCollection<DockApp> apps,
                             ObservableCollection<DockApp> folderApps)
    {
        InitializeComponent();

        // 与主程序保持一致：深色标题栏
        DwmBlur.EnableDarkTitleBar(this);

        foreach (var a in apps)
            _entries.Add(new BatchEntry { App = a, List = apps, Group = "Dock" });
        foreach (var a in folderApps)
            _entries.Add(new BatchEntry { App = a, List = folderApps, Group = "文件夹" });

        ItemList.ItemsSource = _entries;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _entries.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) return; // 未勾选任何项：保持窗口打开，避免误关

        foreach (var en in selected)
            en.List.Remove(en.App);

        OnRemoved?.Invoke();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
