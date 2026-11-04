using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>Bool 取反后转 Visibility（用于「取消」按钮仅在任务进行中显示）。</summary>
public sealed class InverseBoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotImplementedException();
}

/// <summary>下载任务行：包装 DownloadJob，状态变化时在 UI 线程通知绑定刷新。</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    public JobRow(DownloadJob job) => Job = job;

    /// <summary>内部持有任务对象（不公开，避免 XAML 类型信息生成器尝试实例化 DownloadJob）。</summary>
    internal DownloadJob Job { get; }
    public string DisplayName => DownloadManager.DisplayName(Job);
    public string StatusText => Job.StatusText;
    public bool Done => Job.Done;
    public Brush StatusBrush => (Brush)Application.Current.Resources[
        Job.Error != null ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];

    /// <summary>排队中（还没开始下载）：任务名显示灰色。</summary>
    public bool IsQueued => !Job.Done && Job.StatusText == "排队中...";

    /// <summary>任务名颜色：完成/进行中白色，排队灰色（与状态色规范一致）。</summary>
    public Brush TitleBrush => (Brush)Application.Current.Resources[
        Done || !IsQueued ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    /// <summary>完成的任务名加删除线。</summary>
    public Windows.UI.Text.TextDecorations TitleDecorations => Done
        ? Windows.UI.Text.TextDecorations.Strikethrough
        : Windows.UI.Text.TextDecorations.None;

    /// <summary>进度 0~1（绑定 ProgressBar.Value）。</summary>
    public double Fraction => Math.Clamp(Job.Fraction, 0, 1);
    /// <summary>百分比文字，如 45%。</summary>
    public string PercentText => $"{(int)Math.Round(Fraction * 100)}%";
    /// <summary>进行中的任务才显示进度条/百分比（排队中转圈即可）。</summary>
    public Visibility ProgressVisibility => Done || IsQueued ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PercentVisibility => ProgressVisibility;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>刷新该行的全部绑定（空字符串 = 所有属性）。</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

/// <summary>下载任务独立页：列出全部后台任务，进行中的可取消，可一键清除已结束任务。</summary>
public sealed partial class DownloadTasksPage : Page
{
    private readonly ObservableCollection<JobRow> _rows = new();

    public DownloadTasksPage()
    {
        InitializeComponent();
        DownloadManager.JobsChanged += OnJobsChanged; // Unloaded 时退订
    }

    /// <summary>供 x:Bind 的列表源。</summary>
    public ObservableCollection<JobRow> Rows => _rows;

    private void Page_Loaded(object sender, RoutedEventArgs e) => RefreshList();

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        DownloadManager.JobsChanged -= OnJobsChanged;
    }

    // 任务状态变化（来自后台线程，切回 UI 线程刷新）
    private void OnJobsChanged(object? sender, System.EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshList);
    }

    private void RefreshList()
    {
        var jobs = DownloadManager.Jobs;
        // 同步行集合：新任务追加行，已移除的任务删行，尽量复用已有行避免闪烁
        foreach (var job in jobs)
        {
            if (_rows.All(r => !ReferenceEquals(r.Job, job)))
            {
                _rows.Add(new JobRow(job));
            }
        }
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!jobs.Contains(_rows[i].Job))
            {
                _rows.RemoveAt(i);
            }
        }
        foreach (var row in _rows)
        {
            row.Refresh();
        }

        bool empty = jobs.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        JobList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = jobs.Any(j => j.Done);
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JobRow row } && !row.Job.Done)
        {
            row.Job.Cts.Cancel();
        }
    }

    // 点击任务行：进入该任务的详情子页
    private void JobList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JobRow row)
        {
            Frame.Navigate(typeof(DownloadJobDetailPage), row.Job);
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => DownloadManager.ClearFinished();
}
