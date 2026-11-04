using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本详情子页面：选择附加模块（加载器互斥）及其版本后加入后台下载。</summary>
public sealed partial class VersionDetailPage : Page
{
    /// <summary>加载器版本下拉框项：显示文本 + 传给安装器的版本号（空 = 自动选最新）。</summary>
    public sealed record LoaderVersionOption(string Display, string? Version);

    private string _versionId = "";
    /// <summary>当前自动生成的版本名；用户手动改过后不再覆盖。</summary>
    private string _autoName = "";
    /// <summary>版本列表拉取序号：快速切换加载器时丢弃过期结果。</summary>
    private int _versionLoadSeq;
    /// <summary>上一次拉取的取消令牌：切换加载器时取消，且限制最长等待 20 秒。</summary>
    private System.Threading.CancellationTokenSource? _loadCts;
    /// <summary>各加载器的可用构建缓存（索引同 RadioButtons：1=Fabric 2=Forge 3=NeoForge）。</summary>
    private readonly Dictionary<int, List<LoaderBuild>> _buildsCache = new();

    public VersionDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _versionId = e.Parameter as string ?? "";
        TitleText.Text = _versionId;
        UpdateAutoName(force: true); // 默认版本名 = 版本-安装器
        _ = CheckLoaderSupportAsync(); // 后台检测各加载器是否支持此版本，不支持的灰置
    }

    /// <summary>加载器选项对应的名称后缀（不安装为空）。</summary>
    private static string LoaderName(int index) => index switch
    {
        1 => "Fabric",
        2 => "Forge",
        3 => "NeoForge",
        _ => "",
    };

    /// <summary>生成「版本-安装器」名称；重名时依次追加 -2、-3…。
    /// 用户手动改过名字后不再自动覆盖，除非 force。</summary>
    private void UpdateAutoName(bool force = false)
    {
        if (_versionId.Length == 0) return;
        if (!force && VersionNameBox.Text.Length > 0 && VersionNameBox.Text != _autoName) return;

        string loader = LoaderName(LoaderRadio.SelectedIndex);
        string baseName = loader.Length == 0 ? _versionId : $"{_versionId}-{loader}";
        string name = baseName;
        for (int i = 2; IsNameTaken(name); i++)
        {
            name = $"{baseName}-{i}";
        }
        _autoName = name;
        VersionNameBox.Text = name;
    }

    private static bool IsNameTaken(string name) =>
        System.IO.Directory.Exists(VersionMeta.DirPath(name)) || DownloadManager.IsNameOrVersionBusy(name, "");

    /// <summary>并行拉取三种加载器的构建列表：空列表 = 不支持该 MC 版本，禁用对应选项。</summary>
    private async System.Threading.Tasks.Task CheckLoaderSupportAsync()
    {
        string mirror = AppSettings.DownloadMirror;
        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(20));
        var results = await System.Threading.Tasks.Task.WhenAll(
            SafeFetch(() => LoaderVersions.FabricAsync(_versionId, mirror, cts.Token)),
            SafeFetch(() => LoaderVersions.ForgeAsync(_versionId, mirror, cts.Token)),
            SafeFetch(() => LoaderVersions.NeoForgeAsync(_versionId, mirror, cts.Token)));
        for (int i = 1; i <= 3; i++)
        {
            _buildsCache[i] = results[i - 1] ?? new();
        }
        // 网络失败时返回 null：保持可用（fail-open），安装时会给出明确错误
        FabricRadio.IsEnabled = results[0] is not { Count: 0 };
        ForgeRadio.IsEnabled = results[1] is not { Count: 0 };
        NeoForgeRadio.IsEnabled = results[2] is not { Count: 0 };
        // 当前选中的被禁用时退回「不安装」
        int sel = LoaderRadio.SelectedIndex;
        if (sel > 0 && sel <= 3 && results[sel - 1] is { Count: 0 })
        {
            LoaderRadio.SelectedIndex = 0;
        }
    }

    private static async System.Threading.Tasks.Task<List<LoaderBuild>?> SafeFetch(
        Func<System.Threading.Tasks.Task<List<LoaderBuild>>> fetch)
    {
        try { return await fetch(); }
        catch (Exception) { return null; }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void LoaderRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = LoaderRadio.SelectedIndex;
        UpdateAutoName(); // 名称跟随安装器（用户手动改过则不覆盖）
        bool show = idx > 0;
        LoaderVersionCombo.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LoaderVersionStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            await LoadLoaderVersionsAsync(idx);
        }
    }

    /// <summary>填充加载器版本下拉框（第一项为自动选最新），优先用支持性检测时已缓存的列表。</summary>
    private async System.Threading.Tasks.Task LoadLoaderVersionsAsync(int loaderIndex)
    {
        int seq = ++_versionLoadSeq;
        _loadCts?.Cancel(); // 取消上一次未完成的拉取
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(20));
        _loadCts = cts;

        var items = new List<LoaderVersionOption> { new("自动选择最新版（推荐）", null) };
        if (loaderIndex == 4)
        {
            // Quilt 由安装流程自动取最新 loader，无需列表
            LoaderVersionCombo.ItemsSource = items;
            LoaderVersionCombo.SelectedIndex = 0;
            LoaderVersionStatus.Text = "将使用最新 Quilt Loader";
            return;
        }

        string cacheLoader = LoaderName(loaderIndex).ToLowerInvariant();

        // 本轮会话刚拉过（支持性检测已完成）：直接显示，无需再转圈
        if (_buildsCache.TryGetValue(loaderIndex, out List<LoaderBuild>? known) && known is { Count: > 0 })
        {
            FillLoaderVersions(items, known);
            LoaderVersionStatus.Text = $"共 {known.Count} 个可用版本";
            return;
        }

        // 还没拉过：先立即显示磁盘缓存，再联网刷新
        var cached = LoaderVersions.CachedBuilds(cacheLoader, _versionId);
        if (cached is { Count: > 0 })
        {
            FillLoaderVersions(items, cached);
            LoaderVersionStatus.Text =
                $"显示缓存（{LoaderVersions.AgeText(cacheLoader, _versionId)}），正在刷新…";
        }
        else
        {
            LoaderVersionCombo.ItemsSource = items;
            LoaderVersionCombo.SelectedIndex = 0;
            LoaderVersionStatus.Text = "正在获取可用版本…";
        }

        try
        {
            string mirror = AppSettings.DownloadMirror;
            List<LoaderBuild> builds = loaderIndex switch
            {
                1 => await LoaderVersions.FabricAsync(_versionId, mirror, cts.Token),
                2 => await LoaderVersions.ForgeAsync(_versionId, mirror, cts.Token),
                3 => await LoaderVersions.NeoForgeAsync(_versionId, mirror, cts.Token),
                _ => new(),
            };
            if (seq != _versionLoadSeq) return; // 期间又切换了加载器，丢弃结果
            _buildsCache[loaderIndex] = builds;
            FillLoaderVersions(items, builds);
            LoaderVersionStatus.Text = builds.Count == 0
                ? "该加载器没有适配此游戏版本的构建。"
                : $"共 {builds.Count} 个可用版本";
        }
        catch (System.OperationCanceledException)
        {
            if (seq != _versionLoadSeq) return;
            LoaderVersionStatus.Text = cached is { Count: > 0 }
                ? $"网络超时，继续显示缓存（{LoaderVersions.AgeText(cacheLoader, _versionId)}）"
                : "获取版本列表超时，请检查网络（可改用自动选择）。";
        }
        catch (Exception ex)
        {
            if (seq != _versionLoadSeq) return;
            LoaderVersionStatus.Text = cached is { Count: > 0 }
                ? $"网络获取失败，继续显示缓存（{LoaderVersions.AgeText(cacheLoader, _versionId)}）：{ex.Message}"
                : $"获取版本列表失败：{ex.Message}（可改用自动选择）";
        }
    }

    /// <summary>用「自动选择 + 构建列表」整体替换下拉框内容（新列表实例，避免 ComboBox 复用到旧项）。</summary>
    private void FillLoaderVersions(List<LoaderVersionOption> head, List<LoaderBuild> builds)
    {
        var items = new List<LoaderVersionOption>(head);
        foreach (var b in builds.Take(200))
        {
            items.Add(new LoaderVersionOption(b.Display, b.Version));
        }
        LoaderVersionCombo.ItemsSource = items;
        LoaderVersionCombo.SelectedIndex = 0;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        // 版本名 = 安装目录名：必填、不含非法字符、不与现有版本/下载任务重复
        string name = VersionNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "请填写版本名。";
            return;
        }
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = "版本名不能包含 \\ / : * ? \" < > | 字符。";
            return;
        }
        if (System.IO.Directory.Exists(VersionMeta.DirPath(name)))
        {
            StatusText.Text = "已存在同名版本，请换个名字。";
            return;
        }
        if (DownloadManager.IsNameOrVersionBusy(name, _versionId))
        {
            StatusText.Text = "同名版本已在下载队列中。";
            return;
        }

        McAddon addon = LoaderRadio.SelectedIndex switch
        {
            1 => McAddon.Fabric,
            2 => McAddon.Forge,
            3 => McAddon.NeoForge,
            4 => McAddon.Quilt,
            _ => McAddon.None,
        };
        string? loaderVersion = addon == McAddon.None
            ? null
            : (LoaderVersionCombo.SelectedItem as LoaderVersionOption)?.Version;

        DownloadManager.Start(_versionId, name, AppSettings.DownloadMirror, addon, loaderVersion);
        // 加入后台下载后回到下载页的「下载任务」
        App.MainWindow?.ShowDownloadTasks();
    }
}
