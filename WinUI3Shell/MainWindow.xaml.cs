using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUI3Shell.Pages;
using WinUI3Shell.Services;
using WinUI3Shell.Services.Mc;
using Windows.Graphics;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace WinUI3Shell;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "MnyLauncher";
        SystemBackdrop = new MicaBackdrop();
        // 启动器自身窗口：屏幕工作区的 2/3 大小并居中（与“游戏窗口大小”设置无关）
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int winW = workArea.Width * 2 / 3, winH = workArea.Height * 2 / 3;
        AppWindow.Resize(new SizeInt32(winW, winH));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - winW) / 2,
            workArea.Y + (workArea.Height - winH) / 2));

        // 任务栏/窗口图标
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // 内容延伸到标题栏，由 AppTitleBar 充当可拖动的标题栏区域
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 与设置、任务管理器一致的高标题栏（48px），标题栏按钮垂直居中
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // 下载入口留出系统窗口按钮的宽度（随 DPI 变化，避免压到最小化/关闭按钮）
        int rightInset = AppWindow.TitleBar.RightInset;
        if (rightInset <= 0) rightInset = 140; // 尚未计算出来时用保守值
        DlIndicator.Margin = new Thickness(0, 0, rightInset + 8, 0);
        UpdateTitleBarColors();

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        }

        // 默认选中“启动”，触发导航
        NavView.SelectedItem = NavView.MenuItems[0];

        // 全局下载指示按钮
        DownloadManager.JobsChanged += OnJobsChanged;
        OnJobsChanged(null, EventArgs.Empty);
    }

    /// <summary>窗口句柄，供文件选择器等 Win32 互操作使用。</summary>
    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>打开指定版本的版本设置页（不进页面缓存，每次新建）；section 可直达子页。</summary>
    public void ShowVersionSettings(string versionDir, string? section = null)
    {
        ContentFrame.Content = new VersionSettingsPage(versionDir, section);
    }

    /// <summary>从版本设置返回启动页（恢复缓存的页面实例，保留状态）。</summary>
    public void ShowHome()
    {
        if (_pageCache.TryGetValue("Launch.Home", out var page))
        {
            ContentFrame.Content = page;
            // 改名/删除/置顶可能已变化，刷新版本列表
            if (page is HomePage home)
            {
                home.RefreshVersions();
                home.RefreshAccount();
            }
        }
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    // 已通知过的任务（按任务对象本身标识），避免重复弹
    private readonly HashSet<DownloadJob> _notifiedJobs = new();
    private DispatcherTimer? _bannerTimer;
    private bool _bannerHovering;

    // 下载任务状态变化（来自后台线程，需切回 UI 线程）
    private void OnJobsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var jobs = DownloadManager.Jobs;
            int running = jobs.Count(j => !j.Done);
            DlIndicator.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
            DlCountText.Text = running.ToString();
            ToolTipService.SetToolTip(DlIndicator, running > 0
                ? $"{running} 个下载任务进行中，点击查看\n" + string.Join('\n',
                    jobs.Where(j => !j.Done).Select(j => $"{j.VersionId}：{j.StatusText}"))
                : "下载任务");

            // 检测刚完成的任务，弹顶部提示
            foreach (var job in jobs)
            {
                if (!job.Done) continue;
                if (!_notifiedJobs.Add(job)) continue;
                if (job.Cancelled) continue;
                ShowDoneBanner(job);
                break; // 一次只弹一个，其余下一轮再弹
            }
        });
    }

    /// <summary>当前横幅对应的任务（「安装」作用对象）。</summary>
    private DownloadJob? _bannerJob;

    private void ShowDoneBanner(DownloadJob job)
    {
        _bannerJob = job;
        DoneBannerText.Text = DownloadManager.BannerText(job);
        // MOD / 安装器下载完成后可以直接安装；安装任务本身、失败或取消的不给安装入口
        DoneBannerInstallButton.Visibility = job.Error == null && !job.Cancelled && !job.IsInstallAction
            && (job.IsMod || job.IsInstaller)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DoneBanner.Visibility = Visibility.Visible;
        PlayBannerIn();
        StartBannerTimer();

        // 启动器不在前台（最小化/被别的窗口盖住）：额外弹一条系统通知
        if (!IsForegroundWindow()) Notifications.ShowDownloadDone(job);
    }

    private Storyboard? _bannerStoryboard;

    /// <summary>横幅入场：淡入 + 从上方滑下（CubicEase 减速，非线性）。</summary>
    private void PlayBannerIn()
    {
        _bannerStoryboard?.Stop();
        DoneBanner.Opacity = 0;
        DoneBannerTransform.Y = -16;

        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, DoneBanner);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = -16,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, DoneBannerTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        _bannerStoryboard = sb;
        sb.Begin();
    }

    /// <summary>当前前台窗口是否就是启动器自己。</summary>
    private bool IsForegroundWindow()
    {
        try
        {
            return GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void StartBannerTimer()
    {
        _bannerTimer?.Stop();
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bannerTimer.Tick += (_, _) =>
        {
            if (!_bannerHovering) HideDoneBanner();
        };
        _bannerTimer.Start();
    }

    private void DoneBanner_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _bannerHovering = true;
    }

    private void DoneBanner_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _bannerHovering = false;
    }

    // 点击「查看」：跳转到「库」
    private void DoneBannerView_Click(object sender, RoutedEventArgs e)
    {
        var job = _bannerJob;
        HideDoneBanner();
        ShowLibraryFor(job);
    }

    // 点击「安装」：MOD 装到选中版本，安装器给适配版本装加载器
    private async void DoneBannerInstall_Click(object sender, RoutedEventArgs e)
    {
        var job = _bannerJob;
        HideDoneBanner();
        if (job == null || (Content as FrameworkElement)?.XamlRoot is not { } root) return;
        await ModFlow.InstallFromJobAsync(root, job);
    }

    /// <summary>横幅出场：淡出 + 略微上移（CubicEase 加速）。</summary>
    private void HideDoneBanner()
    {
        _bannerTimer?.Stop();
        if (DoneBanner.Visibility != Visibility.Visible) return;

        _bannerStoryboard?.Stop();
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fade, DoneBanner);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = -10,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(slide, DoneBannerTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Completed += (_, _) => DoneBanner.Visibility = Visibility.Collapsed;
        _bannerStoryboard = sb;
        sb.Begin();
    }

    /// <summary>跳到「库」页并切到对应栏目（下载完成的内容在哪个栏目就显示哪个；系统通知点「查看」也走这里）。</summary>
    public void ShowLibraryFor(DownloadJob? job = null)
    {
        foreach (var obj in NavView.FooterMenuItems)
        {
            if (obj is NavigationViewItem navItem && navItem.Tag as string == "Mod.Library")
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
        if (_pageCache.TryGetValue("Mod.Library", out var page) && page is ModLibraryPage library)
        {
            library.ShowFor(job);
        }
    }

    // 点击标题栏下载按钮：跳到下载页并直达「下载任务」
    private void DlIndicator_Click(object sender, RoutedEventArgs e) => ShowDownloadTasks();

    /// <summary>跳到下载页并切到「下载任务」（下载开始后返回用）。</summary>
    public void ShowDownloadTasks()
    {
        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem navItem && navItem.Tag as string == "Download.Vanilla")
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
        if (_pageCache.TryGetValue("Download.Vanilla", out var page) && page is DownloadsPage dl)
        {
            dl.ShowTasks();
        }
    }

    /// <summary>跳到账号页；openAdd 为 true 时顺带弹出「添加账号」下拉（首页点「添加账号」）。</summary>
    public void ShowAccount(bool openAdd = false)
    {
        foreach (var obj in NavView.FooterMenuItems)
        {
            if (obj is NavigationViewItem navItem && navItem.Tag as string == "Account")
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
        if (openAdd && ContentFrame.Content is AccountPage accountPage)
        {
            accountPage.OpenAdd();
        }
    }

    /// <summary>拖放：整合包（.mrpack / 带 modrinth.index.json 或 manifest.json 的 zip）直接安装；存档文件夹导入。</summary>
    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "拖入以安装 / 导入";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (Directory.Exists(item.Path))
            {
                await HandleDroppedFolderAsync(item.Path);
            }
            else if (File.Exists(item.Path))
            {
                await HandleDroppedFileAsync(item.Path);
            }
        }
    }

    // 拖入存档文件夹：选一个版本导入（或导入共享存档目录）
    private async Task HandleDroppedFolderAsync(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "level.dat")))
        {
            await ShowDropMessageAsync("无法识别", "拖入的文件夹不是存档（缺少 level.dat）。");
            return;
        }

        var options = new List<string>();
        var dirs = new List<string>();
        options.Add("共享存档目录");
        dirs.Add(McPaths.GameDir);
        foreach (var versionDir in Directory.Exists(McPaths.VersionsDir)
                     ? Directory.GetDirectories(McPaths.VersionsDir) : [])
        {
            string name = Path.GetFileName(versionDir);
            var meta = VersionMeta.Load(name);
            if (meta != null && meta.IsComplete(name))
            {
                options.Add(name);
                dirs.Add(meta.GameDir(name));
            }
        }

        var picker = new ComboBox { ItemsSource = options, SelectedIndex = 0, Header = "导入到", MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = "导入存档",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = Path.GetFileName(folder),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    picker,
                },
            },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string savesDir = Path.Combine(dirs[picker.SelectedIndex], "saves");
        Directory.CreateDirectory(savesDir);
        string target = Path.Combine(savesDir, Path.GetFileName(folder));
        int suffix = 2;
        while (Directory.Exists(target))
        {
            string attempt = Path.GetFileName(folder) + " (" + suffix + ")";
            suffix++;
            target = Path.Combine(savesDir, attempt);
        }
        await Task.Run(() => CopyDirectory(folder, target));
        await ShowDropMessageAsync("已导入", "存档已导入到 " + (picker.SelectedIndex == 0 ? "共享存档目录" : options[picker.SelectedIndex]));
    }

    // 拖入整合包：预览后交给下载任务安装
    private async Task HandleDroppedFileAsync(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".mrpack" or ".zip"))
        {
            await ShowDropMessageAsync("无法识别", "目前支持拖入 .mrpack / 整合包 zip / 存档文件夹。");
            return;
        }

        ModpackInfo pack;
        try
        {
            pack = await Task.Run(() => Modpack.Load(file));
        }
        catch (Exception ex)
        {
            await ShowDropMessageAsync("无法读取整合包", ex.Message);
            return;
        }

        var nameBox = new TextBox { Text = Modpack.SuggestInstallName(pack), Header = "安装名称", MinWidth = 280 };
        var info = new TextBlock
        {
            Text = $"{pack.Name} {pack.Version} · Minecraft {pack.McVersion} · {pack.LoaderText}\n共 {pack.Files.Count} 个文件",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = "安装整合包",
            Content = new StackPanel { Spacing = 10, Children = { info, nameBox } },
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string installName = nameBox.Text.Trim();
        if (installName.Length == 0) return;
        DownloadManager.StartModpack(pack, installName, AppSettings.DownloadMirror);
        ShowDownloadTasks();
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), true);
    }

    private async Task ShowDropMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

    /// <summary>切换整个窗口的主题（ElementTheme.Default 表示跟随系统）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }

        UpdateTitleBarColors();
    }

    private void UpdateTitleBarColors()
    {
        bool isDark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        AppWindowTitleBar titleBar = AppWindow.TitleBar;

        // 按钮背景透明，让标题栏区域与 Mica 融为一体
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
    }

    // 手动缓存页面实例：切页后回来状态（如下载进度）保持不变。
    // WinUI 3 的 NavigationCacheMode 缓存不可靠，故不用 Frame.Navigate。
    private readonly Dictionary<string, Page> _pageCache = new();

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!_pageCache.TryGetValue(tag, out Page? page))
        {
            page = tag switch
            {
                "Download.Vanilla" => new DownloadsPage(),
                "Account" => new AccountPage(),
                "Mod.Library" => new ModLibraryPage(),
                "Settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pageCache[tag] = page;
        }

        if (!ReferenceEquals(ContentFrame.Content, page))
        {
            ContentFrame.Content = page;
        }
    }
}
