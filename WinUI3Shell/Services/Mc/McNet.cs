using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace WinUI3Shell.Services.Mc;

/// <summary>.minecraft 规范的游戏目录结构（默认 %USERPROFILE%\.mnymc）。</summary>
public static class McPaths
{
    public static readonly string GameDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mnymc");
    public static string VersionsDir => Path.Combine(GameDir, "versions");
    public static string VersionDir(string id) => Path.Combine(VersionsDir, id);
    public static string LibrariesDir => Path.Combine(GameDir, "libraries");
    public static string AssetsDir => Path.Combine(GameDir, "assets");
    public static string RuntimesDir => Path.Combine(GameDir, "runtimes");
    /// <summary>MOD 库：下载的 Mod 文件仓库（与具体版本无关）。</summary>
    public static string ModLibraryDir => Path.Combine(GameDir, "modlibrary");
    /// <summary>库里的安装器目录：下载的 Forge/Fabric/NeoForge/Quilt 安装器 jar。</summary>
    public static string InstallersDir => Path.Combine(GameDir, "modlibrary", "installers");
    /// <summary>下载的服务端 jar 存放目录。</summary>
    public static string ServerDir => Path.Combine(GameDir, "server");
    /// <summary>存档备份目录。</summary>
    public static string BackupsDir => Path.Combine(GameDir, "backups", "saves");
}

/// <summary>下载进度：阶段名、字节进度、文件数进度。</summary>
public sealed record McProgress(string Phase, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles);

/// <summary>镜像源：官方 / BMCLAPI；auto = 断点换源（先走镜像，失败自动切官方并续传）。</summary>
public static class Mirror
{
    public const string Official = "official";
    public const string Bmclapi = "bmclapi";
    public const string Auto = "auto";

    /// <summary>断点换源时，记录「镜像地址 → 官方原始地址」，供失败重试切源用。</summary>
    private static readonly ConcurrentDictionary<string, string> AutoOriginals = new();

    /// <summary>是否优先走 BMCLAPI 镜像（断点换源也优先镜像）。</summary>
    public static bool PrefersBmclapi(string mirror) => mirror != Official;

    /// <summary>断点换源模式下取回原始官方地址；返回 false 表示无需切源。</summary>
    public static bool TryGetOriginal(string url, out string original) =>
        AutoOriginals.TryGetValue(url, out original!);

    private static readonly Dictionary<string, Dictionary<string, string>> HostRewrites = new()
    {
        [Official] = new(),
        [Bmclapi] = new()
        {
            ["launchermeta.mojang.com"] = "bmclapi2.bangbang93.com",
            ["piston-meta.mojang.com"] = "bmclapi2.bangbang93.com",
            ["piston-data.mojang.com"] = "bmclapi2.bangbang93.com",
            ["libraries.minecraft.net"] = "bmclapi2.bangbang93.com/maven",
            ["resources.download.minecraft.net"] = "bmclapi2.bangbang93.com/assets",
            ["maven.minecraftforge.net"] = "bmclapi2.bangbang93.com/maven",
            ["files.minecraftforge.net"] = "bmclapi2.bangbang93.com/maven",
            // NeoForge 官方仓库带 /releases 前缀，BMCLAPI maven 代理没有，需剥离
            ["maven.neoforged.net"] = "bmclapi2.bangbang93.com/maven|/releases",
        },
    };

    public static string Rewrite(string url, string mirror = Official)
    {
        // 断点换源先按 BMCLAPI 改写，同时记住官方地址，下载失败时可切回
        string rulesKey = mirror == Auto ? Bmclapi : mirror;
        if (!HostRewrites.TryGetValue(rulesKey, out var rules) || rules.Count == 0)
            return url;
        var uri = new Uri(url);
        if (!rules.TryGetValue(uri.Host, out var target))
            return url;
        string strip = "";
        int bar = target.IndexOf('|');
        if (bar >= 0)
        {
            strip = target[(bar + 1)..];
            target = target[..bar];
        }
        string host, path = uri.AbsolutePath;
        if (strip.Length > 0 && path.StartsWith(strip, StringComparison.Ordinal))
            path = path[strip.Length..];
        int slash = target.IndexOf('/');
        if (slash >= 0)
        {
            host = target[..slash];
            path = "/" + target[(slash + 1)..] + path;
        }
        else
        {
            host = target;
        }
        string rewritten = $"{uri.Scheme}://{host}{path}{uri.Query}";
        if (mirror == Auto && rewritten != url)
        {
            if (AutoOriginals.Count > 20000) AutoOriginals.Clear();
            AutoOriginals[rewritten] = url;
        }
        return rewritten;
    }
}

public sealed record McDownloadTask(string Url, string Dest, string? Sha1 = null, long? Size = null);

public static class McHttp
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        // 设置里填了代理就全局走代理（Mojang / BMCLAPI / Modrinth / Adoptium 都生效）
        string proxy = AppSettings.ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(proxy.Trim());
                handler.UseProxy = true;
            }
            catch { /* 代理地址写错就当没填，不阻断下载 */ }
        }
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        c.Timeout = TimeSpan.FromMinutes(30);
        return c;
    }
}

/// <summary>多线程下载：sha1/size 校验、失败重试、进度汇报、已存在文件自动跳过。</summary>
public static class Downloader
{
    private const int Retries = 3;

    // 全局限速状态（所有并发下载共享一个桶，避免 N 个线程各限一份导致总和超限）
    private static readonly object LimitGate = new();
    private static long _limitOriginTicks;
    private static long _limitBytes;
    private static double _limitCredit;

    /// <summary>按设置的全局限速等待；未限速立即返回。</summary>
    private static async Task ThrottleAsync(long bytes, CancellationToken ct)
    {
        int limitKb = AppSettings.DownloadLimitKb;
        if (limitKb <= 0 || bytes <= 0) return;

        double limitBytesPerSec = limitKb * 1024.0;
        double delaySeconds;
        lock (LimitGate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_limitOriginTicks == 0) _limitOriginTicks = now;
            // 空闲超过 1 秒就重置窗口，避免长时间暂停后「攒」出一大笔额度瞬间突发
            double elapsed = (now - _limitOriginTicks) / (double)Stopwatch.Frequency;
            if (elapsed > 1.0)
            {
                _limitOriginTicks = now;
                _limitBytes = 0;
                _limitCredit = 0;
                elapsed = 0;
            }
            _limitBytes += bytes;
            double shouldTake = _limitBytes / limitBytesPerSec;
            delaySeconds = shouldTake - elapsed + _limitCredit;
            _limitCredit = 0;
            if (delaySeconds < 0)
            {
                // 跑得比限额快是不允许的：把多出来的时间记成负额度，下次扣回来
                _limitCredit = delaySeconds;
                delaySeconds = 0;
            }
        }
        if (delaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }
    }

    public static bool IsValid(McDownloadTask t)
    {
        if (!File.Exists(t.Dest)) return false;
        if (t.Size is long size && new FileInfo(t.Dest).Length != size) return false;
        if (t.Sha1 is string sha1 && !Sha1Of(t.Dest).Equals(sha1, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Sha1Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>删除游戏目录下所有残留的 .part 半成品文件（启动时调用：上次被关闭/强杀导致的半截下载不续传，直接清掉）。</summary>
    public static int CleanupPartialDownloads()
    {
        int count = 0;
        if (!Directory.Exists(McPaths.GameDir)) return 0;
        foreach (string file in Directory.EnumerateFiles(McPaths.GameDir, "*.part", SearchOption.AllDirectories))
        {
            try { File.Delete(file); count++; }
            catch { /* 文件被占用等情况跳过 */ }
        }
        return count;
    }

    /// <summary>下载单个文件，onChunk 回调报告每次写入的字节数。</summary>
    public static async Task DownloadFileAsync(McDownloadTask t, Action<long>? onChunk = null, CancellationToken ct = default)
    {
        if (IsValid(t)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(t.Dest)!);
        string tmp = t.Dest + ".part";
        // 断点换源：镜像失败后切官方地址，并从已下载的部分继续
        bool canSwitch = Mirror.TryGetOriginal(t.Url, out string fallbackUrl);
        Exception? last = null;
        for (int attempt = 1; attempt <= Retries + 1; attempt++)
        {
            string url = canSwitch && attempt % 2 == 0 ? fallbackUrl : t.Url;
            try
            {
                // 半成品已达到或超过目标大小，说明文件坏了，直接重来
                if (canSwitch && t.Size is long fullSize
                    && File.Exists(tmp) && new FileInfo(tmp).Length >= fullSize)
                {
                    TryDelete(tmp);
                }
                // 大文件走多线程分段下载；已有断点半成品时保持单线程续传，避免推倒重来
                bool hasPartial = canSwitch && File.Exists(tmp) && new FileInfo(tmp).Length > 0;
                bool segmented = !hasPartial && await TryDownloadSegmentedAsync(url, tmp, t.Size, onChunk, ct);
                if (!segmented)
                {
                    await DownloadToAsync(url, tmp, canSwitch, onChunk, ct);
                }
                File.Move(tmp, t.Dest, true);
                if (IsValid(t)) return;
                TryDelete(t.Dest);
                last = new InvalidDataException($"sha1/size 校验失败: {t.Url}");
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp); // 取消（如用户点「取消」）：半成品立刻删，不留到下次启动
                throw;
            }
            catch (Exception ex)
            {
                if (!canSwitch) TryDelete(tmp);
                last = ex;
            }
        }
        throw new HttpRequestException($"下载失败（重试 {Retries} 次后仍不成功）: {t.Url}", last);
    }

    /// <summary>写入 .part；append=true 时带 Range 从断点续传，服务器不支持则从头开始。</summary>
    private static async Task DownloadToAsync(string url, string tmp, bool append,
        Action<long>? onChunk, CancellationToken ct)
    {
        long existing = append && File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }
        using var resp = await McHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        bool resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            resp.EnsureSuccessStatusCode();
        }
        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(tmp,
            resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                await ThrottleAsync(n, ct);
                onChunk?.Invoke(n);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* 忽略清理失败 */ }
    }

    // ===== 单文件多线程（分段）下载 =====

    /// <summary>每段目标大小约 2MB，单文件最多 8 个并发连接。</summary>
    private const long SegLen = 2 * 1024 * 1024;
    private const int MaxSegments = 8;

    /// <summary>
    /// 单文件分段并发下载：首个请求用开放式 Range（bytes=0-）探测服务器是否支持断点，
    /// 响应的 Content-Range 给出总大小后，把剩余部分拆成多段并发拉取。
    /// 返回 false 表示服务器不支持 Range 或文件太小没必要分段，调用方回退单线程。
    /// 文件完整性最终由 DownloadFileAsync 的 sha1/size 校验兜底。
    /// </summary>
    private static async Task<bool> TryDownloadSegmentedAsync(
        string url, string tmp, long? knownSize, Action<long>? onChunk, CancellationToken ct)
    {
        // 已知太小的文件不值得分段（省一次探测往返）
        if (knownSize is long ks && ks < 2 * SegLen) return false;

        using var seg0Req = new HttpRequestMessage(HttpMethod.Get, url);
        seg0Req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, null);
        using var seg0Resp = await McHttp.Client.SendAsync(seg0Req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (seg0Resp.StatusCode != HttpStatusCode.PartialContent)
            return false; // 服务器无视 Range（回了 200 全量），走单线程

        long? total = seg0Resp.Content.Headers.ContentRange?.Length;
        if (total is not long size || size <= 0)
        {
            // 服务器不给总大小（bytes 0-x/*），没法分段：这条流本来就是全量，直接读完
            await using var input = await seg0Resp.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
            await PumpRangeAsync(input, output, 0, long.MaxValue, onChunk, ct);
            return true;
        }

        int segments = (int)Math.Min(MaxSegments, Math.Max(1, size / SegLen));
        if (segments <= 1)
        {
            await using var input = await seg0Resp.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
            await PumpRangeAsync(input, output, 0, long.MaxValue, onChunk, ct);
            return true;
        }
        long segLen = (size + segments - 1) / segments;

        await using (var seg0Stream = await seg0Resp.Content.ReadAsStreamAsync(ct))
        await using (var seg0File = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16, true))
        {
            seg0File.SetLength(size); // 预分配，其余段按偏移直接写入

            using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var jobs = new List<Task>(segments);

            // 第 0 段：复用已拿到的响应流，只写 [0, segLen)
            jobs.Add(PumpRangeAsync(seg0Stream, seg0File, 0, segLen, onChunk, segCts.Token));

            // 其余段：各开一条连接按 Range 并发下载
            for (int i = 1; i < segments; i++)
            {
                long start = i * segLen;
                long end = Math.Min(size, start + segLen) - 1;
                jobs.Add(DownloadSegmentAsync(url, tmp, start, end, onChunk, segCts.Token));
            }

            try
            {
                await Task.WhenAll(jobs);
            }
            catch
            {
                // 任一段失败：取消其余段，等全部退出（句柄释放）后再抛，避免 .part 删不掉
                segCts.Cancel();
                try { await Task.WhenAll(jobs); }
                catch { /* 异常已在上方抛出 */ }
                throw;
            }
        }
        return true;
    }

    /// <summary>把流写入文件偏移 offset 起的 count 字节；越界部分属于其他段，不读不写。</summary>
    private static async Task PumpRangeAsync(Stream input, FileStream output, long offset, long count,
        Action<long>? onChunk, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long pos = offset, end = offset + count;
        output.Seek(offset, SeekOrigin.Begin);
        while (pos < end)
        {
            int want = (int)Math.Min(buffer.Length, end - pos);
            int n = await input.ReadAsync(buffer.AsMemory(0, want), ct);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            await ThrottleAsync(n, ct);
            onChunk?.Invoke(n);
            pos += n;
        }
    }

    /// <summary>单段下载：Range [start, end] 写入 tmp 的对应偏移。</summary>
    private static async Task DownloadSegmentAsync(string url, string tmp, long start, long end,
        Action<long>? onChunk, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        using var resp = await McHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidOperationException($"分段下载被服务器拒绝: {url}");
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16, true);
        await PumpRangeAsync(input, output, start, end - start + 1, onChunk, ct);
    }

    /// <summary>并发下载一批任务，进度按字节+文件数汇报（约每 100ms 一次）。</summary>
    public static async Task DownloadManyAsync(IReadOnlyList<McDownloadTask> tasks, int workers,
        string phase, IProgress<McProgress>? progress, CancellationToken ct = default)
    {
        var pending = tasks.Where(t => !IsValid(t)).ToList();
        if (pending.Count == 0) return;

        long totalBytes = pending.Sum(t => t.Size ?? 0);
        long doneBytes = 0;
        int doneFiles = 0;
        long lastReportMs = 0;
        var sw = Stopwatch.StartNew();

        void OnChunk(long n)
        {
            Interlocked.Add(ref doneBytes, n);
            long ms = sw.ElapsedMilliseconds;
            if (ms - Interlocked.Read(ref lastReportMs) > 100)
            {
                Interlocked.Exchange(ref lastReportMs, ms);
                progress?.Report(new McProgress(phase,
                    Interlocked.Read(ref doneBytes), totalBytes,
                    Volatile.Read(ref doneFiles), pending.Count));
            }
        }

        using var sem = new SemaphoreSlim(workers);
        var running = pending.Select(async t =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await DownloadFileAsync(t, OnChunk, ct);
            }
            finally
            {
                sem.Release();
                Interlocked.Increment(ref doneFiles);
            }
        }).ToList();
        await Task.WhenAll(running);
        progress?.Report(new McProgress(phase, Interlocked.Read(ref doneBytes), totalBytes, pending.Count, pending.Count));
    }
}
