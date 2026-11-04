using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>下载页外壳：左侧栏（游戏版本 / Forge / Fabric / NeoForge / MOD / 整合包 / 下载任务）+ 右侧子页面。</summary>
public sealed partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        InitializeComponent();
    }

    private void DownloadsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (DlFrame.Content == null)
        {
            DlFrame.Navigate(typeof(DownloadVersionsPage));
        }
    }

    /// <summary>切到「下载任务」子页（供标题栏下载指示按钮调用）。</summary>
    public void ShowTasks()
    {
        foreach (var obj in SideBar.FooterMenuItems)
        {
            if (obj is NavigationViewItem item && item.Tag as string == "tasks")
            {
                SideBar.SelectedItem = item;
                break;
            }
        }
    }

    private void SideBar_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (DlFrame == null || args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        string tag = item.Tag?.ToString() ?? "versions";
        switch (tag)
        {
            case "versions":
                if (DlFrame.CurrentSourcePageType != typeof(DownloadVersionsPage))
                {
                    DlFrame.Navigate(typeof(DownloadVersionsPage));
                }
                break;
            case "tasks":
                // 正看着某个任务的详情子页时，点侧栏「下载任务」回到列表
                if (DlFrame.CurrentSourcePageType == typeof(DownloadJobDetailPage) && DlFrame.CanGoBack)
                {
                    DlFrame.GoBack();
                }
                else if (DlFrame.CurrentSourcePageType != typeof(DownloadTasksPage))
                {
                    DlFrame.Navigate(typeof(DownloadTasksPage));
                }
                break;
            case "mods":
                if (DlFrame.CurrentSourcePageType == typeof(ModDetailPage) && DlFrame.CanGoBack)
                {
                    DlFrame.GoBack();
                }
                else if (DlFrame.CurrentSourcePageType != typeof(ModSearchPage))
                {
                    DlFrame.Navigate(typeof(ModSearchPage));
                }
                break;
            case "modpacks":
                if (DlFrame.CurrentSourcePageType != typeof(ModpackPage))
                {
                    DlFrame.Navigate(typeof(ModpackPage));
                }
                break;
            case "quilt":
                if (DlFrame.CurrentSourcePageType != typeof(QuiltPage))
                {
                    DlFrame.Navigate(typeof(QuiltPage));
                }
                break;
            case "server":
                if (DlFrame.CurrentSourcePageType != typeof(ServerDownloadPage))
                {
                    DlFrame.Navigate(typeof(ServerDownloadPage));
                }
                break;
            case "optifine":
                DlFrame.Navigate(typeof(OptiFinePage), "optifine");
                break;
            case "liteloader":
                DlFrame.Navigate(typeof(OptiFinePage), "liteloader");
                break;
            default:
                // 手动下载子页面，参数为加载器名
                DlFrame.Navigate(typeof(ManualDownloadPage), tag);
                break;
        }
    }
}
