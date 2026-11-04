using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>官方版本清单读取（version_manifest.json / version_manifest_v2.json）。</summary>
public static class Manifest
{
    public const string V1 = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    public const string V2 = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>拉取清单。v2 额外带每个 version.json 的 sha1。成功后写入磁盘缓存，供下次启动先显示。</summary>
    public static async Task<JsonObject> FetchAsync(string mirror, bool v2 = true, CancellationToken ct = default)
    {
        string url = Mirror.Rewrite(v2 ? V2 : V1, mirror);
        string raw = await McHttp.Client.GetStringAsync(url, ct);
        var obj = (JsonObject)JsonNode.Parse(raw)!;
        NetCache.Set(CacheKey(v2), raw);
        return obj;
    }

    private static string CacheKey(bool v2) => v2 ? "version-manifest-v2" : "version-manifest-v1";

    /// <summary>读磁盘缓存里的清单（同步、不联网）；没有缓存返回 null。</summary>
    public static JsonObject? Cached(bool v2 = true)
    {
        string? raw = NetCache.Get(CacheKey(v2));
        if (raw == null) return null;
        try
        {
            return (JsonObject)JsonNode.Parse(raw)!;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清单缓存的新鲜度描述，如「2 小时前」。</summary>
    public static string CachedAge(bool v2 = true) => NetCache.AgeText(CacheKey(v2));

    public static string LatestRelease(JsonObject m) => m["latest"]!["release"]!.GetValue<string>();

    public static IEnumerable<(string Id, string Url, string? Sha1, string Type)> Versions(JsonObject m, string? type = null)
    {
        foreach (var node in m["versions"]!.AsArray())
        {
            var v = node!.AsObject();
            string t = v["type"]!.GetValue<string>();
            if (type != null && t != type) continue;
            yield return (v["id"]!.GetValue<string>(), v["url"]!.GetValue<string>(), v["sha1"]?.GetValue<string>(), t);
        }
    }

    public static (string Url, string? Sha1) Find(JsonObject m, string id)
    {
        foreach (var (vid, url, sha1, _) in Versions(m))
            if (vid == id)
                return (url, sha1);
        throw new ArgumentException($"清单中不存在版本: {id}");
    }
}
