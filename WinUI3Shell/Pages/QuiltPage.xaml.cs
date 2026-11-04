using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>Quilt 安装器下载子页面：列出 Quilt 官方安装器版本，选中后加入下载列表。</summary>
public sealed partial class QuiltPage : Page
{
    private sealed record BuildItem(string Display, string Url, string FileName);

    private const string RepoBase = "https://maven.quiltmc.org/repository/release";
    private const string CacheKey = "quilt-installer";
    private readonly List<BuildItem> _builds = new();

    public QuiltPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await LoadListAsync();
    }

    /// <summary>加载安装器列表：先用磁盘缓存立即显示，同时转圈联网刷新，成功后覆盖缓存。</summary>
    private async Task LoadListAsync()
    {
        string? cached = NetCache.Get(CacheKey);
        bool hasCache = cached != null && FillBuilds(cached);
        if (!hasCache)
        {
            _builds.Clear();
            BuildList.ItemsSource = null;
            StatusText.Text = "正在获取版本列表…";
        }
        else
        {
            StatusText.Text = $"显示缓存（{NetCache.AgeText(CacheKey)}），正在刷新…";
        }

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string xml = await McHttp.Client.GetStringAsync(
                $"{RepoBase}/org/quiltmc/quilt-installer/maven-metadata.xml", cts.Token);
            if (FillBuilds(xml))
            {
                NetCache.Set(CacheKey, xml); // 刷新成功才覆盖缓存
                StatusText.Text = $"共 {_builds.Count} 个，选中后点击“加入下载列表”。";
            }
            else
            {
                StatusText.Text = "没有找到可用的 Quilt 安装器版本。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = hasCache
                ? $"网络获取失败，继续显示缓存（{NetCache.AgeText(CacheKey)}）：{ex.Message}"
                : $"获取 Quilt 安装器列表失败: {ex.Message}（请检查网络）";
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
        }
    }

    /// <summary>解析 maven-metadata.xml 填充列表；成功返回 true。</summary>
    private bool FillBuilds(string xml)
    {
        _builds.Clear();
        var doc = XDocument.Parse(xml);
        var versions = doc.Root?.Element("versioning")?.Element("versions")?
            .Elements("version").Select(x => x.Value).Reverse().Take(50) ?? Enumerable.Empty<string>();
        foreach (string v in versions)
        {
            string file = $"quilt-installer-{v}.jar";
            _builds.Add(new BuildItem($"Quilt Installer {v}",
                $"{RepoBase}/org/quiltmc/quilt-installer/{v}/{file}", file));
        }
        BuildList.ItemsSource = _builds.Select(b => b.Display).ToList();
        BuildList.SelectedIndex = -1; // 刷新后不让旧选择指向别的版本
        QueueSelectedButton.IsEnabled = false;
        return _builds.Count > 0;
    }

    private void BuildList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueSelectedButton.IsEnabled = BuildList.SelectedIndex >= 0;
    }

    /// <summary>把选中的安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BuildList.SelectedIndex < 0) return;
        var item = _builds[BuildList.SelectedIndex];
        string version = item.Display.Replace("Quilt Installer ", "");
        DownloadManager.StartInstaller("quilt", "", $"Quilt Installer {version}", item.Url, item.FileName);
        StatusText.Text = $"「Quilt Installer {version}」已加入下载列表，完成后在库或顶部提示里安装。";
    }
}
