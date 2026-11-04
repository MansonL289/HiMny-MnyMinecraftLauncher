using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>下载页 - OptiFine / LiteLoader：给已装版本加优化加载器，不弹官方安装器 GUI。</summary>
public sealed partial class OptiFinePage : Page
{
    private List<OfChoice> _ofVersions = [];
    private List<WinUI3Shell.Services.Mc.LiteLoader.LlVersion> _llVersions = [];
    private string _mode = "optifine";
    /// <summary>OptiFine 列表加载序号：快速切换基础版本时丢弃过期结果。</summary>
    private int _ofSeq;

    public OptiFinePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string mode && mode == "liteloader")
        {
            _mode = "liteloader";
            TitleText.Text = "LiteLoader";
            OfSection.Visibility = Visibility.Collapsed;
            LlSection.Visibility = Visibility.Visible;
        }
        else
        {
            _mode = "optifine";
            TitleText.Text = "OptiFine";
            OfSection.Visibility = Visibility.Visible;
            LlSection.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record OfChoice(string Text, OptiFine.OfVersion Version);

    private async void OptiFinePage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        PageRing.Visibility = Visibility.Visible;
        BaseCombo.IsEnabled = false;
        try
        {
            // 只用「原版 + 完整」的版本作为基础，加载器版本不能打 OptiFine 补丁
            var items = new List<ComboBoxItem>();
            if (Directory.Exists(McPaths.VersionsDir))
            {
                foreach (string dir in Directory.GetDirectories(McPaths.VersionsDir).OrderByDescending(d => d))
                {
                    string name = Path.GetFileName(dir);
                    var meta = VersionMeta.Load(name);
                    if (meta == null || !meta.IsComplete(name)) continue;
                    if (meta.DetectLoader(name).Length > 0) continue;
                    items.Add(new ComboBoxItem { Content = $"{name}（{meta.McVersion(name)}）", Tag = name });
                }
            }
            BaseCombo.ItemsSource = items;
            BaseCombo.IsEnabled = items.Count > 0;
            if (items.Count > 0)
            {
                BaseCombo.SelectedIndex = 0;
            }
            else
            {
                StatusText.Text = "没有可用的原版版本，请先在「游戏」页下载一个原版版本。";
            }

            // LiteLoader 列表：先拿磁盘缓存立即显示，下面再联网刷新
            var cachedLl = WinUI3Shell.Services.Mc.LiteLoader.CachedList();
            if (cachedLl != null) _llVersions = cachedLl;
        }
        catch (Exception ex)
        {
            StatusText.Text = "获取版本列表失败：" + ex.Message;
        }
        finally
        {
            PageRing.Visibility = Visibility.Collapsed;
            PageRing.IsActive = false;
        }

        await RefreshLlAsync();
        await LoadOfAsync();
    }

    /// <summary>联网刷新 LiteLoader 列表（调用方保证页面已显示缓存）。</summary>
    private async Task RefreshLlAsync()
    {
        bool hasCache = _llVersions.Count > 0;
        PageRing.IsActive = true;
        PageRing.Visibility = Visibility.Visible;
        try
        {
            _llVersions = await WinUI3Shell.Services.Mc.LiteLoader.ListAsync();
        }
        catch (Exception ex)
        {
            if (!hasCache)
            {
                StatusText.Text = "获取 LiteLoader 列表失败：" + ex.Message;
            }
        }
        finally
        {
            PageRing.Visibility = Visibility.Collapsed;
            PageRing.IsActive = false;
        }
    }

    private async Task LoadOfAsync()
    {
        if (!TryGetBase(out _, out string mcVersion)) return;

        if (_mode == "optifine")
        {
            int seq = ++_ofSeq;
            // 1) 先显示磁盘缓存，不用等网络
            var cached = OptiFine.CachedList(mcVersion);
            if (cached != null)
            {
                FillOf(cached);
            }
            else
            {
                _ofVersions = [];
                OfCombo.ItemsSource = null;
                OfCombo.PlaceholderText = "正在获取版本列表…";
            }

            // 2) 联网刷新，成功后覆盖缓存
            PageRing.IsActive = true;
            PageRing.Visibility = Visibility.Visible;
            try
            {
                var list = await OptiFine.ListAsync(mcVersion);
                if (seq != _ofSeq) return; // 期间又切了基础版本，丢弃
                FillOf(list);
                StatusText.Text = $"OptiFine 共 {_ofVersions.Count} 个版本";
            }
            catch (Exception ex)
            {
                if (seq != _ofSeq) return;
                if (cached == null)
                {
                    OfCombo.PlaceholderText = "获取失败";
                }
                StatusText.Text = cached != null
                    ? $"网络获取失败，继续显示缓存（{OptiFine.AgeText(mcVersion)}）：{ex.Message}"
                    : "获取 OptiFine 版本失败：" + ex.Message;
            }
            finally
            {
                if (seq == _ofSeq)
                {
                    PageRing.Visibility = Visibility.Collapsed;
                    PageRing.IsActive = false;
                }
            }
        }
        else
        {
            // LiteLoader：只有 1.12.2 及更早才有
            var candidates = _llVersions.Where(v => v.McVersion == mcVersion).ToList();
            LlCombo.ItemsSource = candidates
                .Select(v => new ComboBoxItem { Content = v.Version, Tag = v }).ToList();
            LlCombo.PlaceholderText = candidates.Count > 0 ? "选择 LiteLoader 版本" : "该版本没有 LiteLoader";
            if (candidates.Count > 0) LlCombo.SelectedIndex = 0;
            QueueLlButton.IsEnabled = candidates.Count > 0;
        }
    }

    /// <summary>填充 OptiFine 下拉框；尽量保留用户已选项。</summary>
    private void FillOf(List<OptiFine.OfVersion> list)
    {
        string keep = (OfCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        _ofVersions = list.Select(v => new OfChoice($"{v.Type} {v.Patch}", v)).ToList();
        OfCombo.ItemsSource = _ofVersions.Select(v => new ComboBoxItem { Content = v.Text, Tag = v }).ToList();
        OfCombo.PlaceholderText = _ofVersions.Count > 0 ? "选择 OptiFine 版本" : "该版本没有 OptiFine";
        int index = keep.Length > 0 ? _ofVersions.FindIndex(v => v.Text == keep) : -1;
        if (index >= 0) OfCombo.SelectedIndex = index;
        else if (_ofVersions.Count > 0) OfCombo.SelectedIndex = 0;
    }

    private bool TryGetBase(out string baseDir, out string mcVersion)
    {
        baseDir = "";
        mcVersion = "";
        if ((BaseCombo.SelectedItem as ComboBoxItem)?.Tag is not string name || name.Length == 0) return false;
        var meta = VersionMeta.Load(name);
        if (meta == null) return false;
        baseDir = name;
        mcVersion = meta.McVersion(name);
        return true;
    }

    private async void BaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageRing.IsActive) return;
        await LoadOfAsync();
    }

    /// <summary>把选中的 OptiFine 安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueOf_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetBase(out _, out string mcVersion))
        {
            StatusText.Text = "请先选择一个基础版本（用于确定 OptiFine 对应的游戏版本）。";
            return;
        }
        if ((OfCombo.SelectedItem as ComboBoxItem)?.Tag is not OfChoice choice) return;

        var of = choice.Version;
        string url = $"https://bmclapi2.bangbang93.com/optifine/{mcVersion}/{of.Type}/{of.Patch}";
        DownloadManager.StartInstaller("optifine", mcVersion,
            $"OptiFine {mcVersion} {of.Type} {of.Patch}", url, of.FileName);
        StatusText.Text = $"「OptiFine {of.Type} {of.Patch}」已加入下载列表，完成后在库或顶部提示里安装。";
    }

    /// <summary>把选中的 LiteLoader 安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueLl_Click(object sender, RoutedEventArgs e)
    {
        if ((LlCombo.SelectedItem as ComboBoxItem)?.Tag is not WinUI3Shell.Services.Mc.LiteLoader.LlVersion ll) return;

        DownloadManager.StartInstaller("liteloader", ll.McVersion,
            $"LiteLoader {ll.McVersion} {ll.Version}", ll.Url, ModFlow.LiteLoaderFileName(ll));
        StatusText.Text = $"「LiteLoader {ll.Version}」已加入下载列表，完成后在库或顶部提示里安装。";
    }
}
