using System.IO;
using System.Text.RegularExpressions;

namespace WinUI3Shell.Services.Mc;

/// <summary>
/// 网络响应磁盘缓存：按 key 保存服务器返回的原始文本，用于「先显示上次的内容，再后台刷新」。
/// 读写在 UI 线程也会被调用，所以全部吞掉异常，缓存坏了就当没有。
/// </summary>
public static class NetCache
{
    public static string Dir => Path.Combine(McPaths.GameDir, "cache", "net");

    private static string PathFor(string key)
    {
        // key 里可能带 / : 空格等，统一换成下划线；长度截断防止超长文件名
        string safe = Regex.Replace(key, @"[^\w\.\-]", "_");
        if (safe.Length > 120) safe = safe[..120];
        return Path.Combine(Dir, safe + ".cache");
    }

    /// <summary>读缓存文本；没有或为空返回 null。</summary>
    public static string? Get(string key)
    {
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缓存的写入时间；没有返回 null。</summary>
    public static DateTime? SavedAt(string key)
    {
        try
        {
            string path = PathFor(key);
            return File.Exists(path) ? File.GetLastWriteTime(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入缓存（失败静默，不影响正常使用）。</summary>
    public static void Set(string key, string text)
    {
        try
        {
            string path = PathFor(key);
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换：避免半截文件被下次当成有效缓存
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch
        {
            /* 缓存失败可忽略 */
        }
    }

    /// <summary>缓存新鲜度描述，如「3 分钟前」。</summary>
    public static string AgeText(string key)
    {
        var saved = SavedAt(key);
        if (saved == null) return "缓存";
        var span = DateTime.Now - saved.Value;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    /// <summary>缓存总大小与文件数（供设置页显示）。</summary>
    public static (long Bytes, int Count) SizeInfo()
    {
        long bytes = 0;
        int count = 0;
        try
        {
            if (!Directory.Exists(Dir)) return (0, 0);
            foreach (string file in Directory.EnumerateFiles(Dir))
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
        }
        catch
        {
            /* 忽略统计失败 */
        }
        return (bytes, count);
    }

    /// <summary>清空全部网络缓存。</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
        catch
        {
            /* 被占用时下次再清 */
        }
    }
}
