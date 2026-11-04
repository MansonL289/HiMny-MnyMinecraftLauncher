using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>储存 - 版本子页：勾选批量删除版本；点击单项进入该版本的存档批量删除。</summary>
public sealed partial class StorageVersionsPage : Page
{
    /// <summary>版本列表行：勾选状态走绑定。</summary>
    public sealed class VersionRow
    {
        public string Name { get; set; } = "";
        public bool IsChecked { get; set; }
    }

    private List<VersionRow> _rows = new();

    public StorageVersionsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        RefreshVersions();
    }

    private void RefreshVersions()
    {
        StatusText.Text = "";
        _rows = new List<VersionRow>();
        if (Directory.Exists(McPaths.VersionsDir))
        {
            _rows = Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => new VersionRow { Name = Path.GetFileName(d)! })
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        VersionList.ItemsSource = _rows;
        DeleteButton.IsEnabled = false;
    }

    private void VersionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is string name)
        {
            var row = _rows.FirstOrDefault(r => r.Name == name);
            if (row != null)
            {
                row.IsChecked = box.IsChecked == true;
            }
        }
        DeleteButton.IsEnabled = _rows.Any(r => r.IsChecked);
    }

    /// <summary>点击版本项：进入该版本的存档批量删除页。</summary>
    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionRow row)
        {
            Frame.Navigate(typeof(StorageSavesPage), row.Name);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        bool target = !_rows.All(r => r.IsChecked);
        foreach (var row in _rows)
        {
            row.IsChecked = target;
        }
        // 重建列表让勾选状态生效
        VersionList.ItemsSource = null;
        VersionList.ItemsSource = _rows;
        DeleteButton.IsEnabled = target;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var names = _rows.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        if (names.Count == 0) return;

        // 下载中的版本不能删（目录正在写入）
        var busy = names.Where(n => DownloadManager.IsNameOrVersionBusy(n, VersionMeta.Load(n)?.Id ?? n)).ToList();
        if (busy.Count > 0)
        {
            StatusText.Text = "正在下载的版本不能删除：" + string.Join("、", busy);
            return;
        }

        // 删除是不可恢复操作：不设默认按钮（无蓝色高亮），避免误点
        var dialog = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定删除 {names.Count} 个版本吗？版本文件将一并删除，不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var failed = new List<string>();
        foreach (string name in names)
        {
            try
            {
                Directory.Delete(VersionMeta.DirPath(name), true);
            }
            catch
            {
                failed.Add(name);
            }
        }
        RefreshVersions();
        StatusText.Text = failed.Count == 0
            ? $"已删除 {names.Count} 个版本"
            : "删除失败：" + string.Join("、", failed);
    }
}
