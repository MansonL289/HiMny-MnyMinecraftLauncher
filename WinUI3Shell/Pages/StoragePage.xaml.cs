using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>设置 - 储存：缓存、版本入口（子页批量删除）、重置。</summary>
public sealed partial class StoragePage : Page
{
    public StoragePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        RefreshCacheStatus();
        RefreshVersionSummary();
    }

    /// <summary>扫描下载失败的版本：无有效 json（空壳/垃圾目录）或 json 在但 jar 缺失（半成品）。正在下载的不算。</summary>
    public static List<(string Name, long Bytes)> FailedVersions()
    {
        var list = new List<(string, long)>();
        if (!Directory.Exists(McPaths.VersionsDir)) return list;
        foreach (string dir in Directory.GetDirectories(McPaths.VersionsDir))
        {
            string name = Path.GetFileName(dir)!;
            var meta = VersionMeta.Load(name);
            if (meta != null && meta.IsComplete(dir)) continue;
            if (DownloadManager.IsNameOrVersionBusy(name, meta?.Id ?? name)) continue;
            long bytes = 0;
            try
            {
                bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { /* 目录被占用时按 0 算 */ }
            list.Add((name, bytes));
        }
        return list;
    }

    private void RefreshCacheStatus()
    {
        long bytes = Translator.CacheSizeBytes();
        var (netBytes, netCount) = NetCache.SizeInfo();
        var failed = FailedVersions();
        long failedBytes = failed.Sum(f => f.Bytes);

        var parts = new List<string>
        {
            bytes > 0 ? $"翻译缓存 {SizeText(bytes)}" : "翻译缓存为空",
        };
        if (netCount > 0)
        {
            parts.Add($"网络缓存 {netCount} 项（{SizeText(netBytes)}）");
        }
        if (failed.Count > 0)
        {
            parts.Add($"下载失败的版本 {failed.Count} 个（{SizeText(failedBytes)}）");
        }
        CacheStatus.Text = string.Join(" · ", parts);
    }

    /// <summary>「版本」入口卡片上的摘要：版本总数（含失败版本数）。</summary>
    private void RefreshVersionSummary()
    {
        int total = 0;
        if (Directory.Exists(McPaths.VersionsDir))
        {
            total = Directory.GetDirectories(McPaths.VersionsDir).Length;
        }
        int failed = FailedVersions().Count;
        VersionSummary.Text = total == 0
            ? "还没有版本"
            : failed > 0 ? $"共 {total} 个版本，其中 {failed} 个下载失败" : $"共 {total} 个版本";
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var failed = FailedVersions();
        var (netBytes, netCount) = NetCache.SizeInfo();
        string message = failed.Count > 0
            ? $"将清除翻译缓存、{netCount} 项网络缓存，并删除 {failed.Count} 个下载失败的版本（{SizeText(failed.Sum(f => f.Bytes))}）：\n"
                + string.Join("、", failed.Select(f => f.Name))
            : $"将清除翻译缓存与 {netCount} 项网络缓存（{SizeText(netBytes)}）。";
        var dialog = new ContentDialog
        {
            Title = "清除缓存",
            Content = message,
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Translator.ClearCache();
        NetCache.Clear();
        var deleteFailed = new List<string>();
        foreach (var (name, _) in failed)
        {
            try
            {
                Directory.Delete(VersionMeta.DirPath(name), true);
            }
            catch
            {
                deleteFailed.Add(name);
            }
        }
        RefreshCacheStatus();
        RefreshVersionSummary();
        CacheNote.Text = deleteFailed.Count == 0
            ? "已清除缓存"
            : "部分失败版本删除失败：" + string.Join("、", deleteFailed);
    }

    /// <summary>版本批量管理放在子页里，这里只做入口。</summary>
    private void OpenVersions_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(StorageVersionsPage));
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        // 不设默认按钮（无蓝色高亮），避免用户把高亮当成「确定」误点
        var dialog = new ContentDialog
        {
            Title = "重置游戏",
            Content = "真的要重置吗？将删除本用户下的全部内容：版本、存档、资源文件、Mod 库、缓存，并恢复默认设置，不可恢复。",
            PrimaryButtonText = "真的",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string? error = null;
        try
        {
            if (Directory.Exists(McPaths.GameDir))
            {
                Directory.Delete(McPaths.GameDir, true);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message; // 一般是游戏/文件被占用，删得掉的都删掉了
        }
        AppSettings.Reset();
        Translator.ClearCache();
        RefreshCacheStatus();
        RefreshVersionSummary();
        CacheNote.Text = error == null ? "已重置全部内容" : "部分内容删除失败：" + error;
    }
}
