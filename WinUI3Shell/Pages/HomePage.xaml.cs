using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

public sealed partial class HomePage : Page
{
    private CancellationTokenSource? _launchCts;

    // ===== 愚人节彩蛋（仅 4 月 1 日生效）=====
    private readonly bool _isAprilFools = DateTime.Now.Month == 4 && DateTime.Now.Day == 1;
    private bool _surrendered;

    public HomePage()
    {
        InitializeComponent();
        ApplyDefaultAvatar();
        ApplyAvatarShadow();
        // 下载任务状态变化时刷新版本列表（下载中的版本不可选）
        DownloadManager.JobsChanged += (_, _) => QueueVersionListRefresh();
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshVersionList();
        RefreshAccount();

        if (_isAprilFools) InitAprilFools();

        // 快捷方式带 --launch：自动选中对应版本并直接启动（只执行一次）
        string startup = App.StartupVersion;
        if (startup.Length == 0) return;
        App.ConsumeStartupVersion();
        if (VersionComboBox.ItemsSource is IEnumerable<ComboBoxItem> items)
        {
            var target = items.FirstOrDefault(i => (i.Tag as string) == startup && i.IsEnabled);
            if (target != null)
            {
                VersionComboBox.SelectedItem = target;
                LaunchButton_Click(null!, null!);
            }
        }
    }

    /// <summary>供主窗口在从版本设置返回时刷新（改名/删除/置顶可能已变化）。</summary>
    public void RefreshVersions() => RefreshVersionList();

    // 任务「结构」指纹：只有新增/结束/失败/取消才重建版本下拉框，进度变化不重建
    private int _jobsSignature = -1;
    private bool _refreshScheduled;
    private bool _refreshWaitingForDropDown;

    /// <summary>
    /// 下载任务变化时刷新版本列表。进度回调每秒可能触发几十次，此时不重建列表；
    /// 且下拉框展开期间绝不能替换 ItemsSource（WinUI 会抛 0x80070490 让进程直接退出）。
    /// </summary>
    private void QueueVersionListRefresh()
    {
        int signature = JobsSignature();
        if (signature == _jobsSignature) return;
        _jobsSignature = signature;
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshScheduled = false;
            RefreshVersionList();
        });
    }

    /// <summary>任务列表的结构指纹（忽略进度，只看任务集合与结束状态）。</summary>
    private static int JobsSignature()
    {
        int hash = 17;
        foreach (var job in DownloadManager.Jobs)
        {
            hash = hash * 31 + job.VersionId.GetHashCode();
            hash = hash * 31 + job.InstallName.GetHashCode();
            hash = hash * 31 + (job.Done ? 1 : 0) + (job.Cancelled ? 2 : 0) + (job.Error != null ? 4 : 0);
        }
        return hash;
    }

    // 下拉框收起后再补一次刷新（展开期间被推迟的）
    private void VersionComboBox_DropDownClosed(object sender, object e)
    {
        if (!_refreshWaitingForDropDown) return;
        _refreshWaitingForDropDown = false;
        RefreshVersionList();
    }

    // 默认头像：Head.png（在线账号加载失败时也回落到它）
    private void ApplyDefaultAvatar()
    {
        AvatarBorder.Background = new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Head.png")),
            Stretch = Stretch.UniformToFill,
        };
    }

    /// <summary>是否已有登录账号：没有账号时禁止启动并提示。</summary>
    private static bool HasAccount => AccountStore.Active != null;

    /// <summary>按当前账号刷新名称与皮肤头；离线账号/没有账号时显示默认头像。供主窗口从账号页返回时调用。</summary>
    public void RefreshAccount()
    {
        var account = AccountStore.Active;
        NoAccountHint.Visibility = HasAccount ? Visibility.Collapsed : Visibility.Visible;
        if (account == null || string.IsNullOrWhiteSpace(account.Name))
        {
            NameText.Text = "Steve";
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            NameText.Text = account.Name;
            // 离线账号没有皮肤，按名字去 mc-heads 会拉到别人的重名皮肤，始终用本地默认头像
            if (account.Type == "offline")
            {
                AvatarImage.Source = null;
                AvatarImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                AvatarImage.Source = new BitmapImage(
                    new Uri($"https://mc-heads.net/avatar/{Uri.EscapeDataString(account.Name)}/56"));
                AvatarImage.Visibility = Visibility.Visible;
            }
        }
        UpdateLaunchButtonState();
    }

    /// <summary>启动按钮：必须同时选了版本且有登录账号。</summary>
    private void UpdateLaunchButtonState()
    {
        LaunchButton.IsEnabled = VersionComboBox.SelectedItem != null && HasAccount;
    }

    // 皮肤头加载失败（离线/无网络）：隐藏图片，露出默认 Head.png
    private void AvatarImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;
    }

    // 从 versions/ 目录刷新已下载的版本列表（置顶优先）；正在下载的版本列出但禁用
    private void RefreshVersionList()
    {
        // 下拉框展开时替换 ItemsSource 会让 WinUI 抛 0x80070490（找不到元素）并直接退出进程
        if (VersionComboBox.IsDropDownOpen)
        {
            _refreshWaitingForDropDown = true;
            return;
        }

        string? current = (VersionComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var entries = Directory.Exists(McPaths.VersionsDir)
            ? Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => (Dir: Path.GetFileName(d)!, Meta: VersionMeta.Load(Path.GetFileName(d)!)))
                .Where(x => x.Meta != null)
                .Select(x => (x.Dir, Meta: x.Meta!))
                // 装了一半的版本（取消/失败/文件缺失）不进启动列表
                .Where(x => x.Meta.IsComplete(x.Dir))
                .ToList()
            : new();
        // 置顶优先，其余按名字倒序
        var ordered = entries.OrderByDescending(x => x.Meta.Pinned)
            .ThenByDescending(x => x.Dir).ToList();
        // 还没落盘的下载中版本也补进列表
        var names = ordered.Select(x => x.Dir).ToHashSet();
        foreach (var job in DownloadManager.Jobs)
        {
            if (!job.Done && !names.Contains(job.InstallName))
            {
                ordered.Insert(0, (job.InstallName, new VersionMeta { Id = job.VersionId }));
            }
        }

        ComboBoxItem? toSelect = null;
        var comboItems = ordered.Select(x =>
        {
            bool downloading = DownloadManager.Jobs.Any(j => !j.Done && j.InstallName == x.Dir);
            var item = new ComboBoxItem
            {
                Content = BuildVersionContent(x.Dir, x.Meta, downloading),
                Tag = x.Dir,
                IsEnabled = !downloading,
            };
            if (x.Dir == current && item.IsEnabled)
            {
                toSelect = item;
            }
            return item;
        }).ToList();
        VersionComboBox.ItemsSource = comboItems;
        // 没有保留的上次选择时，按时间挑默认版本：最新下载 > 上次打开 > 不选
        if (toSelect == null)
        {
            string? defaultDir = PickDefaultDir(comboItems);
            if (defaultDir != null)
            {
                toSelect = comboItems.FirstOrDefault(i => (i.Tag as string) == defaultDir);
            }
        }
        VersionComboBox.SelectedItem = toSelect;
        UpdateLaunchButtonState();
        VersionSettingsButton.IsEnabled = VersionComboBox.SelectedItem != null;
    }

    /// <summary>版本下拉框内容：有自定义图标时显示「图标 + 名字」，否则纯文本。</summary>
    private static object BuildVersionContent(string dir, VersionMeta meta, bool downloading)
    {
        string text = downloading ? dir + "（下载中）" : dir;
        if (downloading || string.IsNullOrWhiteSpace(meta.Icon)) return text;
        try
        {
            string iconPath = Path.Combine(VersionMeta.DirPath(dir), meta.Icon);
            if (!File.Exists(iconPath)) return text;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new Image
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage { UriSource = new Uri(iconPath) },
            });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        catch
        {
            return text; // 图标路径异常时退回纯文本
        }
    }

    /// <summary>默认版本：比较「最新下载时间」与「上次打开时间」，取较新者；都没有则不选。</summary>
    private static string? PickDefaultDir(List<ComboBoxItem> items)
    {
        var installed = items.Where(i => i.IsEnabled && i.Tag is string).ToList();
        if (installed.Count == 0) return null;

        string? newestDir = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (var item in installed)
        {
            string dir = (string)item.Tag!;
            DateTime created = Directory.GetCreationTime(VersionMeta.DirPath(dir));
            if (newestDir == null || created > newestTime)
            {
                newestDir = dir;
                newestTime = created;
            }
        }

        string lastDir = AppSettings.LastVersion;
        DateTime lastTime = AppSettings.LastLaunchTicks > 0
            ? new DateTime(AppSettings.LastLaunchTicks)
            : DateTime.MinValue;
        bool lastExists = lastDir.Length > 0 && installed.Any(i => (i.Tag as string) == lastDir);

        return lastExists && lastTime >= newestTime ? lastDir : newestDir;
    }

    /// <summary>
    /// 启动界面当前选中的版本目录名（与首页默认选中逻辑一致）：
    /// 上次启动过的版本优先，否则最新下载的完整版本；一个版本都没有则返回空。
    /// 供 Forge 等需要按游戏版本过滤的页面直接使用。
    /// </summary>
    public static string CurrentLaunchDir()
    {
        string? newestDir = null;
        DateTime newestTime = DateTime.MinValue;
        if (Directory.Exists(McPaths.VersionsDir))
        {
            foreach (string dir in Directory.GetDirectories(McPaths.VersionsDir))
            {
                string name = Path.GetFileName(dir);
                var meta = VersionMeta.Load(name);
                if (meta == null || !meta.IsComplete(name)) continue;
                DateTime created = Directory.GetCreationTime(dir);
                if (newestDir == null || created > newestTime)
                {
                    newestDir = name;
                    newestTime = created;
                }
            }
        }

        string lastDir = AppSettings.LastVersion;
        DateTime lastTime = AppSettings.LastLaunchTicks > 0
            ? new DateTime(AppSettings.LastLaunchTicks)
            : DateTime.MinValue;
        var lastMeta = lastDir.Length > 0 ? VersionMeta.Load(lastDir) : null;
        bool lastExists = lastMeta != null && lastMeta.IsComplete(lastDir);

        return lastExists && lastTime >= newestTime ? lastDir : newestDir ?? "";
    }

    // 用 Composition DropShadow 给直角头像加阴影（挂在头像后面的承载层上）
    private void ApplyAvatarShadow()
    {
        var visual = ElementCompositionPreview.GetElementVisual(AvatarShadowHost);
        var compositor = visual.Compositor;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 10f;
        shadow.Offset = new Vector3(0f, 3f, 0f);
        shadow.Color = Windows.UI.Color.FromArgb(140, 0, 0, 0);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Size = new Vector2(56f, 56f);
        sprite.Shadow = shadow;

        ElementCompositionPreview.SetElementChildVisual(AvatarShadowHost, sprite);
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 只有选择了已下载的版本才能启动/进版本设置
        UpdateLaunchButtonState();
        VersionSettingsButton.IsEnabled = VersionComboBox.SelectedItem != null;
        UpdatePlaytimeText();
    }

    /// <summary>显示当前选中版本的累计时长与上次启动时间。</summary>
    private void UpdatePlaytimeText()
    {
        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is not string dir)
        {
            PlaytimeText.Visibility = Visibility.Collapsed;
            return;
        }
        long seconds = Playtime.GetSeconds(dir);
        DateTime? last = Playtime.GetLastPlayed(dir);
        if (seconds <= 0 && last == null)
        {
            PlaytimeText.Visibility = Visibility.Collapsed;
            return;
        }
        string text = $"已玩 {Playtime.FormatSeconds(seconds)}";
        if (last.HasValue) text += $" · 上次 {last.Value:MM-dd HH:mm}";
        PlaytimeText.Text = text;
        PlaytimeText.Visibility = Visibility.Visible;
    }

    private void VersionSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is string dir)
        {
            App.MainWindow?.ShowVersionSettings(dir);
        }
    }

    // 启动流程：检查游戏文件 -> 检查/自动下载 Java -> 启动
    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        // 愚人节彩蛋：没点「投降」就启动不了（按钮一直在跑，这里兜底防止碰巧点中）
        if (_isAprilFools && !_surrendered) return;

        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is not string dir) return;
        var meta = VersionMeta.Load(dir);
        if (meta == null) return;
        string versionId = meta.Id;

        // 启动前检查 Mod 冲突：只提示，不阻止（用户可选择仍然启动）
        var conflicts = ModConflict.Check(dir);
        if (conflicts.Count > 0)
        {
            var panel = new StackPanel { Spacing = 6, MaxWidth = 520 };
            panel.Children.Add(new TextBlock
            {
                Text = "检测到以下问题，可能导致启动失败：",
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var item in conflicts.Take(8))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "• " + item,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Mod 可能冲突",
                Content = panel,
                PrimaryButtonText = "仍然启动",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        // 记录上次打开的版本与时间，供下次首页默认选中
        AppSettings.LastVersion = dir;
        AppSettings.LastLaunchTicks = DateTime.Now.Ticks;

        LaunchButton.IsEnabled = false;
        VersionComboBox.IsEnabled = false;
        CancelLaunchButton.Visibility = Visibility.Visible;
        LaunchProgress.Visibility = Visibility.Visible; // 上次启动后可能被隐藏，恢复
        LaunchProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<McProgress>(OnLaunchProgress);
        _launchCts = new CancellationTokenSource();
        bool crashed = false;
        Stopwatch? playWatch = null; // 进程启动后开始计时，退出后累加本次时长
        string gameDir = meta.GameDir(dir); // 版本隔离时 = 版本目录

        try
        {
            SetLaunchStatus("检查游戏文件…");
            string jsonPath = meta.JsonPath(dir);
            string jarPath = meta.JarPath(dir);

            JsonObject versionJson;
            if (File.Exists(jsonPath) && File.Exists(jarPath))
            {
                versionJson = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;
            }
            else
            {
                // 文件不完整，自动补全下载（用下载页选定的镜像源；已存在且校验通过的文件会跳过）
                versionJson = await Installer.PrepareAsync(versionId, AppSettings.DownloadMirror, 16, progress, _launchCts.Token, dir);
            }

            // 自愈：老版本 virtual 资源未物化（历史安装/安装中断）→ 补齐；natives 目录为空 → 重新解压
            SetLaunchStatus("检查资源与运行库…");
            await Assets.HealVirtualAsync(versionJson, _launchCts.Token);
            var (_, _, nativeItems) = Libraries.Collect(versionJson, AppSettings.DownloadMirror);
            string nativesDir = Path.Combine(McPaths.VersionDir(dir), "natives");
            if (nativeItems.Count > 0 &&
                (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir, "*.dll").Any()))
            {
                SetLaunchStatus("修复运行库…");
                await Task.Run(() => Libraries.ExtractNatives(nativeItems, nativesDir));
            }

            var (javaMin, javaMax) = JavaService.RequiredRange(versionJson);
            SetLaunchStatus(javaMax == int.MaxValue
                ? $"检查 Java（需要 >= {javaMin}）…"
                : $"检查 Java（老版本需要 Java {javaMin}）…");
            // 自定义 Java 打不开/主版本不符时自动回退自动选择
            string java = await meta.ResolveJavaAsync(javaMin, javaMax, progress, _launchCts.Token);

            // 启动前解析账号：微软令牌过期先用刷新令牌续期，失败则不启动
            var account = AccountStore.Active;
            if (account?.Type == "microsoft" && account.IsExpired)
            {
                SetLaunchStatus("刷新微软登录…");
                try
                {
                    account = await MsaLogin.RefreshAsync(account, _launchCts.Token);
                    AccountStore.Add(account);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SetLaunchStatus($"登录已失效：{ex.Message}");
                    return;
                }
            }
            else if (account?.Type == "yggdrasil" && account.IsExpired)
            {
                SetLaunchStatus("刷新第三方登录…");
                try
                {
                    account = await YggdrasilLogin.RefreshAsync(account, _launchCts.Token);
                    AccountStore.Add(account);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    SetLaunchStatus("第三方登录已过期，请在账号页重新登录");
                    return;
                }
            }

            // 第三方账号：注入 authlib-injector，把验证服务器地址告诉游戏
            string? authlibAgent = null;
            if (account?.Type == "yggdrasil")
            {
                SetLaunchStatus("准备 authlib-injector…");
                authlibAgent = await AuthlibInjector.EnsureAsync(progress, _launchCts.Token);
            }

            SetLaunchStatus("启动游戏…");
            string player = account?.Name ?? (string.IsNullOrWhiteSpace(NameText.Text) ? "Steve" : NameText.Text.Trim());
            LaunchService.WriteGameLanguage(gameDir, ResolveLocale());
            var cmd = LaunchService.BuildCommand(versionJson, java, player, meta.MaxMemString(), gameDir,
                AppSettings.WindowWidth, AppSettings.WindowHeight, account, authlibAgent,
                SplitArgs(meta.ExtraJvmArgs), SplitArgs(meta.ExtraGameArgs));
            // 自动加入服务器：追加 --server/--port 游戏参数
            if (!string.IsNullOrWhiteSpace(meta.AutoJoinServer))
            {
                var parts = meta.AutoJoinServer.Trim().Split(':', 2);
                cmd.Add("--server");
                cmd.Add(parts[0]);
                cmd.Add("--port");
                cmd.Add(parts.Length > 1 && int.TryParse(parts[1], out int port) ? port.ToString() : "25565");
            }
            int code = await LaunchService.RunAsync(cmd, gameDir, null, _launchCts.Token,
                onStarted: () =>
                {
                    playWatch = Stopwatch.StartNew();
                    // 游戏进程已启动：进度条没有存在意义了，隐藏，只留状态文字
                    LaunchProgress.Visibility = Visibility.Collapsed;
                    SetLaunchStatus($"游戏运行中（{dir}）");
                });

            SetLaunchStatus(code == 0
                ? "游戏已正常退出"
                : $"游戏异常退出 (代码 {code})");
            crashed = code != 0;
        }
        catch (OperationCanceledException)
        {
            SetLaunchStatus("已取消启动");
        }
        catch (Exception ex)
        {
            SetLaunchStatus($"启动失败: {ex.Message}");
        }
        finally
        {
            _launchCts.Dispose();
            _launchCts = null;
            CancelLaunchButton.Visibility = Visibility.Collapsed;
            VersionComboBox.IsEnabled = true;
            UpdateLaunchButtonState();
            // 无论正常退出还是崩溃，本次游玩时长都计入该版本
            if (playWatch != null)
            {
                playWatch.Stop();
                Playtime.Add(dir, (long)playWatch.Elapsed.TotalSeconds);
                UpdatePlaytimeText();
            }
        }

        // 非正常退出：自动给出崩溃分析（和 PCL 一样，用户不用自己翻日志）
        if (crashed)
        {
            await ShowLogDialogAsync(gameDir);
        }
    }

    /// <summary>日志窗口：先给中文结论，下面是最新日志 / 崩溃报告原文。</summary>
    private async Task ShowLogDialogAsync(string gameDir)
    {
        string? text = await Task.Run(() => LogAnalyzer.ReadLog(gameDir));
        var report = LogAnalyzer.Analyze(text);

        var panel = new StackPanel { Spacing = 8, MinWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = report.Headline,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (string finding in report.Findings)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "• " + finding,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
        }

        var box = new TextBox
        {
            Text = report.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "游戏日志",
            Content = panel,
            PrimaryButtonText = "打开日志文件夹",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string logDir = Path.Combine(McPaths.GameDir, "logs");
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDir}\"") { UseShellExecute = true });
        }
    }

    // 取消启动：下载阶段直接中断；游戏已启动则杀掉进程
    private void CancelLaunch_Click(object sender, RoutedEventArgs e) => _launchCts?.Cancel();

    private void OnLaunchProgress(McProgress p)
    {
        if (p.TotalBytes > 0)
        {
            LaunchProgress.IsIndeterminate = false;
            LaunchProgress.Maximum = p.TotalBytes;
            LaunchProgress.Value = Math.Min(p.DoneBytes, p.TotalBytes);
            SetLaunchStatus(p.TotalFiles > 0
                ? $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB ({p.DoneFiles}/{p.TotalFiles})"
                : $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB");
        }
        else
        {
            LaunchProgress.IsIndeterminate = true;
            SetLaunchStatus(p.Phase);
        }
    }

    private void SetLaunchStatus(string text) => LaunchStatus.Text = text;

    // ========== 愚人节彩蛋：启动按钮直接消失，必须点「投降」才能启动 ==========

    private void InitAprilFools()
    {
        LaunchButton.Visibility = Visibility.Collapsed;   // 启动按钮直接消失
        SurrenderHint.Visibility = Visibility.Visible;     // 中间下方显示提示
        SurrenderButton.Visibility = Visibility.Visible;   // 右下角投降按钮
    }

    /// <summary>投降：恢复启动按钮，隐藏提示与投降按钮，之后可以正常启动。</summary>
    private void SurrenderButton_Click(object sender, RoutedEventArgs e)
    {
        _surrendered = true;
        LaunchButton.Visibility = Visibility.Visible;
        SurrenderHint.Visibility = Visibility.Collapsed;
        SurrenderButton.Visibility = Visibility.Collapsed;
    }

    // 解析游戏语言：设置项为空时跟随系统；游戏没有的语言依次退回同名语言 / en_us
    private static string ResolveLocale() => GameLanguages.Resolve(AppSettings.GameLanguage) ?? "en_us";

    // 把设置里的「每行一个」参数拆成列表
    private static List<string>? SplitArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return list.Count > 0 ? list : null;
    }

    // 悬浮在名称上时切换为文本指针，移开恢复箭头
    private void NameText_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    private void NameText_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void NameText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 点名字弹出账号下拉框：切换账号 / 去账号页添加
        var flyout = new MenuFlyout();
        string activeKey = AccountStore.ActiveKey;
        foreach (var account in AccountStore.Accounts)
        {
            var item = new MenuFlyoutItem
            {
                Text = account.Name + (AccountStore.KeyOf(account) == activeKey ? "（使用中）" : ""),
                Tag = account,
            };
            item.Click += AccountFlyoutItem_Click;
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var addItem = new MenuFlyoutItem { Text = "添加账号" };
        addItem.Click += (_, _) => App.MainWindow?.ShowAccount(openAdd: true);
        flyout.Items.Add(addItem);
        flyout.ShowAt(NameText);
    }

    private void AccountFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: McAccount account }) return;
        AccountStore.SetActive(account);
        RefreshAccount();
    }
}
