using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>设置 - 关于：版本信息、反馈/更新链接与更新检查。</summary>
public sealed partial class AboutPage : Page
{
    private const string CurrentVersion = "Beta_2.0.20261104";
    private const string TagsApi = "https://api.github.com/repos/MansonL289/HiMny-MnyMinecraftLauncher/tags";

    private static readonly HttpClient Http = CreateHttp();

    public AboutPage()
    {
        InitializeComponent();
    }

    private void AboutPage_Loaded(object sender, RoutedEventArgs e)
    {
        CpuText.Text = "CPU：" + Hardware.CpuName();
        GpuText.Text = "显卡：" + Hardware.GpuName();
        var (total, avail) = Hardware.Memory();
        MemoryText.Text = total > 0
            ? $"内存：{total / 1024.0:F0} GB（可用 {avail / 1024.0:F0} GB）"
            : "内存：未知";
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckRing.Visibility = Visibility.Visible;
        CheckRing.IsActive = true;
        UpdateText.Text = "正在检查…";

        try
        {
            string json = await Http.GetStringAsync(TagsApi);
            var tags = JsonNode.Parse(json)?.AsArray();
            string? latest = tags?.Select(t => t?["name"]?.GetValue<string>())
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            if (latest == null)
            {
                UpdateText.Text = "没有读取到版本标签。";
            }
            else if (latest.Equals(CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                UpdateText.Text = $"已是最新版本（{CurrentVersion}）。";
            }
            else
            {
                UpdateText.Text = $"发现新版本：{latest}（当前 {CurrentVersion}）。";
            }
        }
        catch (Exception)
        {
            UpdateText.Text = "检查失败，请稍后再试，或直接打开上面的更新链接。";
        }
        finally
        {
            CheckRing.IsActive = false;
            CheckRing.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
