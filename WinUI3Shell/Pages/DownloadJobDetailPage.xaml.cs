using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>单个下载任务的详情子页：实时进度条 + 百分比 + 状态，进行中可取消。</summary>
public sealed partial class DownloadJobDetailPage : Page
{
    private DownloadJob _job = null!;

    public DownloadJobDetailPage()
    {
        InitializeComponent();
        DownloadManager.JobsChanged += OnJobsChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _job = (DownloadJob)e.Parameter;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DownloadManager.JobsChanged -= OnJobsChanged;
    }

    // 任务状态变化（后台线程事件，切回 UI 线程刷新）
    private void OnJobsChanged(object? sender, System.EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 任务被「清除已完成」移除时退回任务列表
            if (!DownloadManager.Jobs.Contains(_job))
            {
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }
            Refresh();
        });
    }

    private void Refresh()
    {
        string display = DownloadManager.DisplayName(_job);
        TitleText.Text = display;
        PhaseText.Text = _job.StatusText;

        // 标题颜色：完成白色+删除线 / 排队灰色 / 进行中白色（与任务列表一致）
        bool queued = !_job.Done && _job.StatusText == "排队中...";
        TitleText.Foreground = (Brush)Application.Current.Resources[
            _job.Done || !queued ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        TitleText.TextDecorations = _job.Done
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;

        // 进度：排队中转圈，进行中按比例，完成隐藏并显示结果
        if (_job.Done)
        {
            JobProgress.IsIndeterminate = false;
            JobProgress.Visibility = Visibility.Collapsed;
            PercentText.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            if (_job.Error != null)
            {
                StatusNote.Visibility = Visibility.Visible;
                StatusNote.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                StatusNote.Text = $"下载失败：{_job.Error}";
            }
            else if (_job.Cancelled)
            {
                StatusNote.Visibility = Visibility.Visible;
                StatusNote.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                StatusNote.Text = "任务已取消。";
            }
            else
            {
                StatusNote.Visibility = Visibility.Visible;
                StatusNote.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                StatusNote.Text = _job.IsInstallAction
                    ? "安装完成，可以启动游戏了。"
                    : "下载完成！去「库」里看看吧。";
            }
        }
        else if (queued)
        {
            JobProgress.Visibility = Visibility.Visible;
            JobProgress.IsIndeterminate = true;
            PercentText.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            StatusNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            JobProgress.Visibility = Visibility.Visible;
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = Math.Clamp(_job.Fraction, 0, 1);
            PercentText.Visibility = Visibility.Visible;
            PercentText.Text = $"{(int)Math.Round(Math.Clamp(_job.Fraction, 0, 1) * 100)}%";
            CancelButton.Visibility = Visibility.Visible;
            StatusNote.Visibility = Visibility.Collapsed;
        }

        InfoText.Text = _job.Kind switch
        {
            DownloadKind.Mod => $"MOD 下载：{_job.VersionId}{_job.AddonText}",
            DownloadKind.Modpack => $"整合包：{_job.InstallName}（游戏版本 {_job.VersionId}）",
            DownloadKind.Installer => $"安装器：{_job.InstallerLoader}（{_job.VersionId}）",
            DownloadKind.File => $"文件：{_job.VersionId}",
            _ => $"游戏版本：{_job.VersionId}{_job.AddonText}"
                + (string.IsNullOrEmpty(_job.LoaderVersion) ? "" : $"（{_job.LoaderVersion}）"),
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _job.Cts.Cancel();
}
