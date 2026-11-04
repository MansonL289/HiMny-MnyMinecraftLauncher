using System.Text.Json.Nodes;
using System.Threading;

namespace WinUI3Shell.Services.Mc;

/// <summary>LiteLoader 安装（仅支持 1.12.2 及更早；用 launchwrapper + tweakerClass 加载，不打补丁）。</summary>
public static class LiteLoader
{
    public sealed record LlVersion(string McVersion, string Version, string TweakClass,
        List<string> Libraries, string Url);

    /// <summary>BMCLAPI 的 LiteLoader 列表，只保留 1.12.2 及更早的版本。</summary>
    public static async Task<List<LlVersion>> ListAsync(CancellationToken ct = default)
    {
        string json = await McHttp.Client.GetStringAsync("https://bmclapi2.bangbang93.com/liteloader/list", ct);
        NetCache.Set(CacheKey, json);
        return ParseList(json);
    }

    private const string CacheKey = "liteloader-list";

    /// <summary>磁盘缓存里的 LiteLoader 列表（同步、不联网）；没有缓存返回 null。</summary>
    public static List<LlVersion>? CachedList()
    {
        string? json = NetCache.Get(CacheKey);
        if (json == null) return null;
        try
        {
            return ParseList(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缓存新鲜度描述，如「1 小时前」。</summary>
    public static string AgeText() => NetCache.AgeText(CacheKey);

    /// <summary>解析 BMCLAPI 的 LiteLoader 列表 json。</summary>
    public static List<LlVersion> ParseList(string json)
    {
        var array = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
        var list = new List<LlVersion>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;
            string mc = obj["mcversion"]?.GetValue<string>() ?? "";
            var build = obj["build"]?.AsObject();
            string version = build?["version"]?.GetValue<string>() ?? "";
            string tweak = build?["tweakClass"]?.GetValue<string>() ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker";
            string file = build?["file"]?.GetValue<string>() ?? $"liteloader-{mc}-SNAPSHOT.jar";
            if (mc.Length == 0 || version.Length == 0) continue;
            if (!IsLegacy(mc)) continue;

            var libs = new List<string>();
            if (build?["libraries"] is JsonArray arr)
            {
                foreach (var lib in arr)
                {
                    string name = lib?.AsObject()?["name"]?.GetValue<string>() ?? "";
                    if (name.Length > 0) libs.Add(name);
                }
            }
            string url = $"https://dl.liteloader.com/versions/com/mumfrey/liteloader/{mc}-SNAPSHOT/{file}";
            list.Add(new LlVersion(mc, version, tweak, libs, url));
        }
        return list.OrderByDescending(v => v.McVersion).ToList();
    }

    /// <summary>把 LiteLoader 装到新版本目录 installName（基于 baseDir 的原版版本）。</summary>
    /// <param name="installerPath">已下载的安装器 jar 路径；为空则从官方地址下载。</param>
    public static async Task InstallAsync(string baseDir, LlVersion ll, string installName,
        IProgress<McProgress>? progress, CancellationToken ct, string? installerPath = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "mnymc-ll-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            string jar = Path.Combine(temp, "liteloader.jar");
            if (installerPath != null && File.Exists(installerPath))
            {
                // 库里已有安装器：直接复用，不再重复下载
                jar = installerPath;
            }
            else
            {
                progress?.Report(new McProgress("下载 LiteLoader", 0, 0, 0, 1));
                await Downloader.DownloadManyAsync(
                    [new McDownloadTask(ll.Url, jar)], 1, "下载 LiteLoader", progress, ct);
            }

            // 1) LiteLoader 本体放到 maven 标准路径
            string libName = $"com.mumfrey:liteloader:{ll.Version}";
            string dest = Path.Combine(McPaths.LibrariesDir, Libraries.MavenPath(libName).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(jar, dest, true);

            // 2) 写版本目录：复用原版 jar（LiteLoader 在运行时改类，不需要打补丁）
            string versionDir = VersionMeta.DirPath(installName);
            Directory.CreateDirectory(versionDir);
            var baseMeta = VersionMeta.Load(baseDir);
            string baseJar = baseMeta?.JarPath(baseDir) ?? "";
            if (!File.Exists(baseJar)) throw new InvalidOperationException("基础版本缺少客户端 jar，请先下载完整版本");
            File.Copy(baseJar, Path.Combine(versionDir, ll.McVersion + ".jar"), true);
            File.WriteAllText(Path.Combine(versionDir, ll.McVersion + ".json"),
                BuildVersionJson(ll, libName));
            new VersionMeta { Id = ll.McVersion, IsolationMode = "on" }.Save(installName);
            progress?.Report(new McProgress("LiteLoader 安装完成", 0, 0, 1, 1));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>老版本（1.12.2 及以下）用 minecraftArguments，把 tweakClass 拼在末尾。</summary>
    private static string BuildVersionJson(LlVersion ll, string libName)
    {
        var libs = new JsonArray { new JsonObject { ["name"] = libName } };
        foreach (var name in ll.Libraries)
        {
            libs.Add(new JsonObject { ["name"] = name });
        }
        // launchwrapper 与 asm-all 是必需依赖（列表里没给时补上）
        var names = ll.Libraries.Select(l => l.Split(':')[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains("launchwrapper")) libs.Add(new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" });
        if (!names.Contains("asm-all")) libs.Add(new JsonObject { ["name"] = "org.ow2.asm:asm-all:5.2" });

        var json = new JsonObject
        {
            ["id"] = ll.McVersion + "-LiteLoader",
            ["inheritsFrom"] = ll.McVersion,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["libraries"] = libs,
            ["minecraftArguments"] =
                "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} " +
                "--assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} " +
                "--accessToken ${auth_access_token} --userType ${user_type} --tweakClass " + ll.TweakClass,
        };
        return json.ToJsonString();
    }

    private static bool IsLegacy(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int minor)) return false;
        return minor <= 12;
    }
}
