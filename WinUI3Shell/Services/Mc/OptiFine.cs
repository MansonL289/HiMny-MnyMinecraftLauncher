using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Threading;

namespace WinUI3Shell.Services.Mc;

/// <summary>OptiFine 安装：走 optifine.Patcher 静默路线，不弹官方安装器 GUI。</summary>
public static class OptiFine
{
    public sealed record OfVersion(string Type, string Patch, string FileName, string Forge);

    /// <summary>BMCLAPI 的 OptiFine 版本列表（已按 patch 倒序，新的在前）。</summary>
    public static async Task<List<OfVersion>> ListAsync(string mcVersion, CancellationToken ct = default)
    {
        string url = $"https://bmclapi2.bangbang93.com/optifine/{mcVersion}";
        string json = await McHttp.Client.GetStringAsync(url, ct);
        NetCache.Set(CacheKey(mcVersion), json);
        return ParseList(json, mcVersion);
    }

    /// <summary>磁盘缓存里的 OptiFine 列表（同步、不联网）；没有缓存返回 null。</summary>
    public static List<OfVersion>? CachedList(string mcVersion)
    {
        string? json = NetCache.Get(CacheKey(mcVersion));
        if (json == null) return null;
        try
        {
            return ParseList(json, mcVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缓存新鲜度描述，如「5 分钟前」。</summary>
    public static string AgeText(string mcVersion) => NetCache.AgeText(CacheKey(mcVersion));

    private static string CacheKey(string mcVersion) => $"optifine-{mcVersion}";

    /// <summary>解析 BMCLAPI 返回的 OptiFine 列表 json。</summary>
    public static List<OfVersion> ParseList(string json, string mcVersion)
    {
        var array = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
        var list = new List<OfVersion>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;
            string type = obj["type"]?.GetValue<string>() ?? "";
            string patch = obj["patch"]?.GetValue<string>() ?? "";
            if (type.Length == 0 || patch.Length == 0) continue;
            list.Add(new OfVersion(type, patch,
                obj["filename"]?.GetValue<string>() ?? $"OptiFine_{mcVersion}_{type}_{patch}.jar",
                obj["forge"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>把 OptiFine 装到新版本目录 installName（基于 baseDir 的原版 jar 打补丁）。</summary>
    /// <param name="installerPath">已下载的安装器 jar 路径；为空则从 BMCLAPI 下载。</param>
    public static async Task InstallAsync(string baseDir, string mcVersion, OfVersion of,
        string installName, IProgress<McProgress>? progress, CancellationToken ct,
        string? installerPath = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "mnymc-of-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            string installer = Path.Combine(temp, of.FileName);
            if (installerPath != null && File.Exists(installerPath))
            {
                // 库里已有安装器：直接复用，不再重复下载
                installer = installerPath;
            }
            else
            {
                progress?.Report(new McProgress("下载 OptiFine", 0, 0, 0, 1));
                await Downloader.DownloadManyAsync(
                    [new McDownloadTask($"https://bmclapi2.bangbang93.com/optifine/{mcVersion}/{of.Type}/{of.Patch}", installer)],
                    1, "下载 OptiFine", progress, ct);
            }

            string baseJar = VersionMeta.Load(baseDir)?.JarPath(baseDir) ?? "";
            if (!File.Exists(baseJar))
            {
                throw new InvalidOperationException("基础版本缺少客户端 jar，请先下载完整版本");
            }

            // 1) 打补丁：optifine.Patcher <原版 jar> <安装器 jar> <输出 jar>
            progress?.Report(new McProgress("应用 OptiFine 补丁", 0, 0, 0, 1));
            string java = await JavaService.EnsureAsync(8, 999, progress, ct);
            string patched = Path.Combine(temp, "patched.jar");
            string output = await RunPatcherAsync(java, installer, baseJar, patched, ct);

            // 2) 处理 launchwrapper-of（版本号写在 launchwrapper-of.txt 里）
            string lwVersion = "2.3";
            string? lwJar = null;
            using (var zip = ZipFile.OpenRead(installer))
            {
                var txt = zip.GetEntry("launchwrapper-of.txt");
                if (txt != null)
                {
                    using var reader = new StreamReader(txt.Open());
                    string text = (await reader.ReadToEndAsync(ct)).Trim();
                    if (text.Length > 0) lwVersion = text;
                }
                lwJar = zip.Entries.FirstOrDefault(e =>
                    e.Name.StartsWith("launchwrapper-of-", StringComparison.OrdinalIgnoreCase)
                    && e.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))?.FullName;
                if (lwJar != null)
                {
                    string lwDest = Path.Combine(McPaths.LibrariesDir, "optifine", "launchwrapper-of", lwVersion,
                        $"launchwrapper-of-{lwVersion}.jar");
                    Directory.CreateDirectory(Path.GetDirectoryName(lwDest)!);
                    zip.GetEntry(lwJar)!.ExtractToFile(lwDest, true);
                }
            }

            // 3) 版本身份：<mc>_<type>_<patch>，OptiFine jar 落到标准 maven 路径
            string ofVersion = $"{mcVersion}_{of.Type}_{of.Patch}";
            string ofDest = Path.Combine(McPaths.LibrariesDir, "optifine", "OptiFine", ofVersion,
                $"OptiFine-{ofVersion}.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(ofDest)!);
            File.Copy(installer, ofDest, true);

            // 4) 写版本目录：补丁后的 jar + 版本 json + mnymc.json
            string versionDir = VersionMeta.DirPath(installName);
            Directory.CreateDirectory(versionDir);
            File.Copy(output, Path.Combine(versionDir, mcVersion + ".jar"), true);
            File.WriteAllText(Path.Combine(versionDir, mcVersion + ".json"), BuildVersionJson(baseDir, mcVersion, ofVersion, lwVersion));
            new VersionMeta { Id = mcVersion, IsolationMode = "on" }.Save(installName);
            progress?.Report(new McProgress("OptiFine 安装完成", 0, 0, 1, 1));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }

    /// <summary>运行 Patcher，返回输出 jar 路径；失败时抛带 java 输出的中文异常。</summary>
    private static async Task<string> RunPatcherAsync(string java, string installer, string baseJar,
        string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = java,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("optifine.Patcher");
        psi.ArgumentList.Add(baseJar);
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Java 进程");
        string log = await process.StandardOutput.ReadToEndAsync(ct);
        string err = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            string detail = (err + "\n" + log).Trim();
            if (detail.Length > 400) detail = detail[^400..];
            throw new InvalidOperationException("OptiFine 补丁失败：" + (detail.Length > 0 ? detail : "未知错误"));
        }
        return output;
    }

    /// <summary>1.13+ 用 arguments.game；更早版本把 tweakClass 拼到 minecraftArguments 末尾。</summary>
    private static string BuildVersionJson(string baseDir, string mcVersion, string ofVersion, string lwVersion)
    {
        var json = new JsonObject
        {
            ["id"] = mcVersion + "-OptiFine_" + ofVersion,
            ["inheritsFrom"] = mcVersion,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["libraries"] = new JsonArray
            {
                new JsonObject { ["name"] = "optifine:OptiFine:" + ofVersion },
                new JsonObject { ["name"] = "optifine:launchwrapper-of:" + lwVersion },
            },
        };

        string baseJsonPath = Path.Combine(VersionMeta.DirPath(baseDir), mcVersion + ".json");
        string? legacyArgs = null;
        if (File.Exists(baseJsonPath))
        {
            try
            {
                legacyArgs = JsonNode.Parse(File.ReadAllText(baseJsonPath))?["minecraftArguments"]?.GetValue<string>();
            }
            catch { /* 读不到就按新版处理 */ }
        }

        if (legacyArgs != null)
        {
            json["minecraftArguments"] = legacyArgs + " --tweakClass optifine.OptiFineTweaker";
        }
        else
        {
            json["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray { "--tweakClass", "optifine.OptiFineTweaker" },
            };
        }
        return json.ToJsonString();
    }
}
