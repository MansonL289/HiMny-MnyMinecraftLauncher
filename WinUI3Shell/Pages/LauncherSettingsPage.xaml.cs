using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>启动器设置：应用主题、开机自启动、游戏文件位置。</summary>
public sealed partial class LauncherSettingsPage : Page
{
    private bool _initializing;

    public LauncherSettingsPage()
    {
        InitializeComponent();

        _initializing = true;
        ThemeRadioButtons.SelectedIndex = AppSettings.Theme switch
        {
            ElementTheme.Light => 0,
            ElementTheme.Dark => 1,
            _ => 2,
        };
        StartupToggle.IsOn = AppSettings.AutoStart;
        ProxyBox.Text = AppSettings.ProxyUrl;
        _initializing = false;
    }

    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ElementTheme theme = ThemeRadioButtons.SelectedIndex switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        AppSettings.Theme = theme;
        App.MainWindow?.ApplyTheme(theme);
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppSettings.AutoStart = StartupToggle.IsOn;
    }

    private void OpenVersionsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(McPaths.VersionsDir);
        Process.Start("explorer.exe", McPaths.VersionsDir);
    }

    /// <summary>保存网络代理。McHttp.Client 在启动时创建，改完必须重启才生效。</summary>
    private async void SaveProxy_Click(object sender, RoutedEventArgs e)
    {
        string url = ProxyBox.Text.Trim();
        AppSettings.ProxyUrl = url; // 留空即清空代理
        ProxyBox.Text = url;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "代理设置已保存",
            Content = url.Length == 0
                ? "已清空代理，重启启动器后不再走代理。"
                : "重启启动器后生效。",
            CloseButtonText = "好",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
