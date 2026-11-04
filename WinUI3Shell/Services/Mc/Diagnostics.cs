using System.IO;
using System.IO.Compression;
using System.Text;

namespace WinUI3Shell.Services.Mc;

/// <summary>
/// 导出诊断报告（zip）：logs 目录、各版本的 mnymc.json、设置快照（不含令牌/代理地址）、硬件信息与版本号。
/// 不打包 assets / libraries，避免报告过大。
/// </summary>
public static class Diagnostics
{
    private const string AppVersion = "Beta_2.0.20261104";

    /// <summary>把诊断信息打包到 destZip，返回该路径。</summary>
    public static Task<string> ExportAsync(string destZip) => Task.Run(() =>
    {
        using var zip = new ZipArchive(File.Create(destZip), ZipArchiveMode.Create);

        // 1) 日志目录（整个打包）
        string logDir = Path.Combine(McPaths.GameDir, "logs");
        if (Directory.Exists(logDir))
        {
            foreach (string file in Directory.EnumerateFiles(logDir))
            {
                try
                {
                    ZipFileExtensions.CreateEntryFromFile(zip, file,
                        "logs/" + Path.GetFileName(file), CompressionLevel.Optimal);
                }
                catch
                {
                    /* 日志正被游戏占用时跳过 */
                }
            }
        }

        // 2) 各版本的 mnymc.json（只含元数据，不含 jar/资源）
        if (Directory.Exists(McPaths.VersionsDir))
        {
            foreach (string dir in Directory.EnumerateDirectories(McPaths.VersionsDir))
            {
                string meta = Path.Combine(dir, "mnymc.json");
                if (!File.Exists(meta))
                {
                    continue;
                }
                try
                {
                    ZipFileExtensions.CreateEntryFromFile(zip, meta,
                        "versions/" + Path.GetFileName(dir) + "/mnymc.json", CompressionLevel.Optimal);
                }
                catch
                {
                    /* 单个版本读取失败不影响整体导出 */
                }
            }
        }

        WriteText(zip, "settings.txt", SettingsText());
        WriteText(zip, "hardware.txt", HardwareText());
        WriteText(zip, "version.txt", AppVersion + Environment.NewLine);

        return destZip;
    });

    /// <summary>设置快照：只记录配置项，排除 CurseForge Key、令牌与代理地址等敏感信息。</summary>
    private static string SettingsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("主题: " + AppSettings.Theme);
        sb.AppendLine("下载镜像: " + AppSettings.DownloadMirror);
        sb.AppendLine("下载限速: " + (AppSettings.DownloadLimitKb > 0
            ? AppSettings.DownloadLimitKb + " KB/s"
            : "不限速"));
        sb.AppendLine("内存模式: " + AppSettings.MemoryMode);
        sb.AppendLine("分配内存: " + (AppSettings.MemoryMode == "custom"
            ? AppSettings.MemoryMb + " MB"
            : "自动"));
        sb.AppendLine("游戏语言: " + (string.IsNullOrEmpty(AppSettings.GameLanguage)
            ? "跟随系统"
            : AppSettings.GameLanguage));
        sb.AppendLine("版本隔离: " + AppSettings.VersionIsolation);
        sb.AppendLine("默认 Java: " + (string.IsNullOrEmpty(AppSettings.GlobalJavaPath)
            ? "自动选择"
            : AppSettings.GlobalJavaPath));
        // 代理只记录是否启用，避免把可能含账号密码的地址带出去
        sb.AppendLine("代理: " + (string.IsNullOrWhiteSpace(AppSettings.ProxyUrl) ? "未启用" : "已启用"));
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return sb.ToString();
    }

    /// <summary>硬件信息文本。</summary>
    private static string HardwareText()
    {
        var (total, avail) = Hardware.Memory();
        var sb = new StringBuilder();
        sb.AppendLine("CPU: " + Hardware.CpuName());
        sb.AppendLine("显卡: " + Hardware.GpuName());
        sb.AppendLine("内存: " + (total > 0
            ? $"{total / 1024.0:F0} GB（可用 {avail / 1024.0:F0} GB）"
            : "未知"));
        return sb.ToString();
    }

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }
}
