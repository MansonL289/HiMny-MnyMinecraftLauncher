using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>安装器下载子页面：列出 Forge/Fabric/NeoForge 安装器版本，选中后加入下载列表。</summary>
public sealed partial class ManualDownloadPage : Page
{
    private sealed record BuildItem(string Display, string JobName, string Url, string FileName,
        string? LoaderVersion);

    private string _loader = "forge";
    private readonly List<BuildItem> _builds = new();
    /// <summary>程序内部改选中项时抑制 SelectionChanged，避免重复加载列表。</summary>
    private bool _suppressReload;
    /// <summary>列表加载序号：快速切换游戏版本时丢弃过期结果。</summary>
    private int _listSeq;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return c;
    }

    public ManualDownloadPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loader = e.Parameter as string ?? "forge";
        TitleText.Text = _loader switch
        {
            "fabric" => "Fabric 安装器",
            "neoforge" => "NeoForge 安装器",
            _ => "Forge 安装器",
        };

        switch (_loader)
        {
            case "forge": // 需要先选择游戏版本
                McVersionCombo.Visibility = Visibility.Visible;
                await LoadMcVersionsAsync();
                break;
            default: // fabric / neoforge 直接列出
                await LoadListAsync();
                break;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReload) return; // 程序填充下拉框时不重复加载
        if (McVersionCombo.SelectedItem is string)
        {
            await LoadListAsync();
        }
    }

    // 填充 MC 正式版下拉框（从版本清单里筛 release）。先显示缓存清单并自动选中启动界面的版本，
    // 再后台联网刷新清单；刷新后若选中项变化才重新加载构建列表。
    private async Task LoadMcVersionsAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在获取版本清单…";

        var cached = Manifest.Cached();
        bool filledFromCache = cached != null && FillMcVersions(cached);
        if (filledFromCache)
        {
            StatusText.Text = $"版本清单：显示缓存（{Manifest.CachedAge()}），正在刷新…";
            await LoadListAsync(); // 先用缓存清单对应的 Forge 缓存列表顶上
        }

        try
        {
            var manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror);
            bool changed = FillMcVersions(manifest);
            if (!filledFromCache || changed)
            {
                await LoadListAsync();
            }
            // 选中项没变时，列表已在上面刷新过，保留它的状态文字
        }
        catch (Exception ex)
        {
            StatusText.Text = filledFromCache
                ? $"版本清单刷新失败，继续显示缓存（{Manifest.CachedAge()}）：{ex.Message}"
                : $"获取版本清单失败: {ex.Message}";
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
        }
    }

    /// <summary>把清单填进下拉框；返回 true 表示选中项发生了变化（调用方据此决定是否重新加载列表）。</summary>
    private bool FillMcVersions(System.Text.Json.Nodes.JsonObject manifest)
    {
        var ids = Manifest.Versions(manifest, "release").Select(v => v.Id).ToList();
        string keep = McVersionCombo.SelectedItem as string ?? "";
        _suppressReload = true;
        try
        {
            McVersionCombo.ItemsSource = ids;
            if (keep.Length > 0 && ids.Contains(keep))
            {
                McVersionCombo.SelectedItem = keep; // 刷新时保持用户当前选择
                return false;
            }
            if (ids.Count == 0) return false;

            // 默认选中启动界面当前的版本（加载器版本取其游戏版本）
            string target = "";
            string current = HomePage.CurrentLaunchDir();
            if (current.Length > 0)
            {
                var meta = VersionMeta.Load(current);
                target = meta != null && meta.McVersion(current).Length > 0 ? meta.McVersion(current) : current;
            }
            if (!ids.Contains(target))
            {
                target = ids[0]; // 兜底：最新正式版
            }
            McVersionCombo.SelectedItem = target;
            return true;
        }
        finally
        {
            _suppressReload = false;
        }
    }

    /// <summary>缓存键：Forge 按游戏版本区分，Fabric/NeoForge 的安装器列表与游戏版本无关。</summary>
    private string CacheKey(string mc)
    {
        return _loader switch
        {
            "fabric" => "fabric-installer",
            "neoforge" => "neoforge-installer",
            _ => $"forge-{mc}",
        };
    }

    /// <summary>联网取列表原始文本（json / xml）。</summary>
    private async Task<string> FetchRawAsync(string mc)
    {
        return _loader switch
        {
            "fabric" => await Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer"),
            "neoforge" => await Http.GetStringAsync(
                "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml"),
            _ => await Http.GetStringAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}"),
        };
    }

    /// <summary>解析原始文本填充 _builds；成功返回 true。</summary>
    private bool FillBuilds(string raw, string mc)
    {
        _builds.Clear();
        switch (_loader)
        {
            case "forge":
            {
                var arr = JsonNode.Parse(raw)!.AsArray();
                foreach (var node in arr.Reverse())
                {
                    string v = node!["version"]!.GetValue<string>();
                    string file = $"forge-{mc}-{v}-installer.jar";
                    _builds.Add(new BuildItem($"Forge {v}（MC {mc}）", $"Forge {v} · MC {mc}",
                        $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{mc}-{v}/{file}", file, v));
                }
                break;
            }
            case "fabric":
            {
                var arr = JsonNode.Parse(raw)!.AsArray();
                foreach (var node in arr)
                {
                    string v = node!["version"]!.GetValue<string>();
                    string file = $"fabric-installer-{v}.jar";
                    _builds.Add(new BuildItem($"Fabric Installer {v}", $"Fabric Installer {v}",
                        $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{v}/{file}", file, null));
                }
                break;
            }
            case "neoforge":
            {
                var xml = XDocument.Parse(raw);
                var vers = xml.Root!.Element("versioning")!.Element("versions")!
                    .Elements("version").Select(x => x.Value).Reverse().Take(50);
                foreach (string v in vers)
                {
                    string file = $"neoforge-{v}-installer.jar";
                    _builds.Add(new BuildItem($"NeoForge {v}", $"NeoForge {v}",
                        $"https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{v}/{file}", file, null));
                }
                break;
            }
        }
        BuildList.ItemsSource = _builds.Select(b => b.Display).ToList();
        BuildList.SelectedIndex = -1; // 刷新后不让旧选择指向别的版本
        QueueSelectedButton.IsEnabled = false;
        return _builds.Count > 0;
    }

    /// <summary>加载构建列表：先用磁盘缓存立即显示，同时转圈联网刷新，成功后覆盖缓存。</summary>
    private async Task LoadListAsync()
    {
        string mc = _loader == "forge" ? McVersionCombo.SelectedItem as string ?? "" : "";
        if (_loader == "forge" && mc.Length == 0)
        {
            StatusText.Text = "请先选择游戏版本。";
            return;
        }

        string key = CacheKey(mc);
        int seq = ++_listSeq;
        string? cached = NetCache.Get(key);
        bool hasCache = cached != null && FillBuilds(cached, mc);
        StatusText.Text = hasCache
            ? $"显示缓存（{NetCache.AgeText(key)}），正在刷新…"
            : "正在获取版本列表…";

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        try
        {
            string raw = await FetchRawAsync(mc);
            if (seq != _listSeq) return; // 期间又切换了游戏版本，丢弃过期结果
            if (FillBuilds(raw, mc))
            {
                NetCache.Set(key, raw); // 刷新成功才覆盖缓存
                StatusText.Text = $"共 {_builds.Count} 个，选中后点击“加入下载列表”。";
            }
            else
            {
                StatusText.Text = "没有找到可用版本。";
            }
        }
        catch (Exception ex)
        {
            if (seq != _listSeq) return;
            StatusText.Text = hasCache
                ? $"网络获取失败，继续显示缓存（{NetCache.AgeText(key)}）：{ex.Message}"
                : $"获取失败: {ex.Message}";
        }
        finally
        {
            if (seq == _listSeq)
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
            }
        }
    }

    /// <summary>手动刷新：跳过缓存的等待感，直接重新联网拉取（仍会先显示旧缓存）。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadListAsync();

    private void BuildList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueSelectedButton.IsEnabled = BuildList.SelectedIndex >= 0;
    }

    /// <summary>把选中的安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BuildList.SelectedIndex < 0) return;
        var item = _builds[BuildList.SelectedIndex];
        // Forge 绑定所选游戏版本；Fabric/NeoForge 的安装器与版本无关，任意版本都可装
        string mc = _loader == "forge" ? McVersionCombo.SelectedItem as string ?? "" : "";
        DownloadManager.StartInstaller(_loader, mc, item.JobName, item.Url, item.FileName, item.LoaderVersion);
        StatusText.Text = $"「{item.JobName}」已加入下载列表，完成后在库或顶部提示里安装。";
    }
}
