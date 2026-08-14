using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BnetSwitch.Services;

/// <summary>某个区服的游戏状态清单:相对路径 → 内容哈希。</summary>
public sealed class GameStateManifest
{
    /// <summary>区服代码,取自游戏 .build.info 的 Branch 列(cn / kr / us / eu)。</summary>
    public string Region { get; set; } = "";

    /// <summary>抓这份快照时的游戏安装路径,还原前要核对,换了盘就别乱还原。</summary>
    public string GameRoot { get; set; } = "";

    public DateTime CapturedUtc { get; set; }

    /// <summary>相对路径(game/… 或 agent/…)→ {size, sha256}。</summary>
    public Dictionary<string, GameStateFile> Files { get; set; } = new();
}

public sealed class GameStateFile
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>文件修改时间(Ticks)。下次抓快照时 size+mtime 都没变就直接沿用旧哈希,
    /// 免得每次切号都把几百 MB 重算一遍 —— 自动抓要做到几乎无感,全靠这个。</summary>
    public long MtimeTicks { get; set; }
}

/// <summary>
/// 按区服存/还原【守望先锋的游戏文件】,让国服↔国际服切换不用每次重下 100+ MB。
///
/// 为什么要这么做:国服用网易 NEAC 反作弊、国际服用暴雪原版,两边是两套不同的二进制
/// (Overwatch.exe 本身 + Neac*.sys/exe/dll vs libxess/DLSS/vivoxsdk 等),
/// 每次切区服战网都要把对方那套重新下一遍。两边差异实测只有 ~314 MB,存下来直接换即可。
///
/// 【三条实测出来的硬规则,违反必失败】
/// 1. 必须连 <c>C:\ProgramData\Battle.net\Agent\product.db</c> 一起换 —— 战网【不校验磁盘文件】,
///    它按自己数据库里记的已装构建算补丁。只换游戏文件的话照样重下(实测 70.65MB)。
/// 2. CASC 索引(.idx)同一分桶【取序号最高的那个】,两个区服的索引并存时客户端会挑到错的那份。
///    所以还原时必须把不属于本区服的 .idx 移走(移进隔离区,不删)。
/// 3. 必须等战网客户端【和 Agent】都退出。Agent 内存里存着 product.db,活着时写进去会被它覆盖回来。
///
/// 存储采用【内容去重】:文件按 sha256 进池,两个区服相同的文件只存一份
/// (实测 1200 个文件里有 1059 MB 是两边一样的,去重后两个区服合计 ~700 MB)。
/// 617MB 的 <c>data/casc/indices</c> 两区服完全一致、76GB 的 <c>data.00N</c> 是内容寻址容器
/// (两区服内容共存其中),都不进快照。
/// </summary>
public sealed class GameStateStore
{
    private const string AgentDir = @"C:\ProgramData\Battle.net\Agent";

    /// <summary>要纳入快照的路径。(相对游戏根, 是否递归, 后缀过滤)</summary>
    private static readonly (string Rel, bool Recurse, string? Ext)[] GameSpecs =
    {
        (".build.info", false, null),
        (".product.db", false, null),
        (".patch.result", false, null),
        ("Launcher.db", false, null),
        ("Overwatch Launcher.exe", false, null),
        ("_retail_", true, null),
        (@"data\casc\config", true, null),
        (@"data\casc\data", false, ".idx"),
        (@"data\casc\pro", false, ".idx"),
        (@".battle.net\bts", false, ".idx"),
    };

    /// <summary>Agent 侧:决定「战网认为本地装的是哪个区服」的那几个文件。</summary>
    private static readonly string[] AgentFiles =
        { "product.db", ".product.db", ".patch.result", "aggregate.json" };

    /// <summary>需要做索引隔离的目录(相对游戏根)。</summary>
    private static readonly string[] IdxDirs =
        { @"data\casc\data", @"data\casc\pro", @".battle.net\bts" };

    public string Root { get; }
    private string PoolDir => Path.Combine(Root, "pool");
    private string ManifestDir => Path.Combine(Root, "regions");
    private string QuarantineDir => Path.Combine(Root, "quarantine");

    /// <summary>守望先锋安装根目录;找不到就是 null(没装游戏 / 路径识别失败)。</summary>
    public string? GameRoot { get; }

    public GameStateStore(string? rootOverride = null, string? gameRootOverride = null)
    {
        GameRoot = gameRootOverride is { Length: > 0 } && IsGameRoot(gameRootOverride)
            ? gameRootOverride
            : FindGameRoot();

        // 默认放在【游戏所在盘】:这些快照是游戏级体量(几百 MB),
        // 系统盘常年吃紧,塞 %LOCALAPPDATA% 容易把 C 盘撑爆。
        if (rootOverride is { Length: > 0 })
        {
            Root = rootOverride;
        }
        else
        {
            var drive = GameRoot is { Length: > 2 } ? GameRoot[..3] : @"C:\";
            Root = Path.Combine(drive, "BnetSwitch-gamestate");
        }
    }

    // ---------- 定位安装目录 ----------

    private static bool IsGameRoot(string dir) =>
        File.Exists(Path.Combine(dir, ".build.info")) && Directory.Exists(Path.Combine(dir, "_retail_"));

    /// <summary>
    /// 从战网 Agent 的 product.db 里挖出游戏安装路径 —— 它是 protobuf,不硬解,
    /// 只把可打印字符串抠出来找形如 X:\... 且底下有 .build.info 的那个。
    /// </summary>
    private static string? FindGameRoot()
    {
        try
        {
            var db = Path.Combine(AgentDir, "product.db");
            if (!File.Exists(db)) return null;
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(db));
            foreach (Match m in Regex.Matches(text, @"[A-Za-z]:[/\\][ -~]{3,120}"))
            {
                // protobuf 里字段是紧挨着的,路径后面往往直接粘着别的内容,
                // 实测形如 "D:/overwatch/Overwatchcn  (2zhCN:zhCNB" —— 整段拿去判断必然失败。
                // 所以从长到短逐位回退,取第一个真的是游戏根目录的前缀。
                var s = m.Value.Replace('/', '\\');
                for (var len = s.Length; len >= 4; len--)
                {
                    var p = s[..len].TrimEnd('\\', ' ');
                    if (p.Length >= 4 && IsGameRoot(p)) return p;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>读游戏当前处于哪个区服(.build.info 里 Active=1 的那一行的 Branch)。</summary>
    public string? ReadCurrentRegion()
    {
        try
        {
            if (GameRoot is null) return null;
            var lines = File.ReadAllLines(Path.Combine(GameRoot, ".build.info"));
            foreach (var line in lines.Skip(1))
            {
                var cols = line.Split('|');
                if (cols.Length >= 2 && cols[1].Trim() == "1")
                    return cols[0].Trim().ToUpperInvariant();
            }
        }
        catch { }
        return null;
    }

    // ---------- 快照 ----------

    private IEnumerable<(string Full, string Rel)> Enumerate()
    {
        if (GameRoot is null) yield break;

        foreach (var (rel, recurse, ext) in GameSpecs)
        {
            var full = Path.Combine(GameRoot, rel);
            if (recurse)
            {
                if (!Directory.Exists(full)) continue;
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    yield return (f, "game/" + Path.GetRelativePath(GameRoot, f).Replace('\\', '/'));
            }
            else if (Directory.Exists(full))
            {
                foreach (var f in Directory.EnumerateFiles(full))
                    if (ext is null || f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        yield return (f, "game/" + Path.GetRelativePath(GameRoot, f).Replace('\\', '/'));
            }
            else if (File.Exists(full))
            {
                yield return (full, "game/" + rel.Replace('\\', '/'));
            }
        }

        foreach (var f in AgentFiles)
        {
            var full = Path.Combine(AgentDir, f);
            if (File.Exists(full)) yield return (full, "agent/" + f);
        }
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private string PoolPath(string sha) => Path.Combine(PoolDir, sha[..2], sha);

    /// <summary>
    /// 游戏状态是否自洽 —— 自动抓快照前必查,免得把「下了一半 / 正在修复」的坏状态存成基准,
    /// 那样下次还原反而会触发一次修复下载,比不做还糟。
    ///
    /// 三个条件:上次打补丁成功(.patch.result == 0)、.build.info 里读得出激活区服、
    /// 且 Agent 数据库里 prometheus 段的区服标签和它一致(不一致说明正处在换区服的中途)。
    /// </summary>
    public bool IsConsistent(out string? region)
    {
        region = null;
        if (GameRoot is null) return false;
        try
        {
            var pr = Path.Combine(GameRoot, ".patch.result");
            if (File.Exists(pr) && File.ReadAllText(pr).Trim() is { Length: > 0 } s && s != "0")
                return false;

            region = ReadCurrentRegion();
            if (string.IsNullOrEmpty(region)) return false;

            var db = Path.Combine(AgentDir, "product.db");
            if (!File.Exists(db)) return false;
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(db));
            // prometheus 段的标签形如 "... CN? acct-CHN? geoip-CN? zhCN ..." / "... KR? acct-HKG? ..."
            var m = Regex.Match(text, @"DX11 DX12[ -~]{0,120}");
            if (!m.Success) return false;
            return m.Value.Contains(region + "?", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 把当前游戏状态存为某个区服的快照。调用前应确认战网客户端已退出
    /// (自动抓时还会先等 Agent 退出,避免读到它正在改写的 product.db)。
    /// 返回 (区服, 文件数, 新增入池字节数)。
    /// </summary>
    public (string Region, int Files, long NewBytes) Capture()
    {
        if (GameRoot is null)
            throw new InvalidOperationException("没有找到守望先锋的安装目录,无法保存游戏状态。");
        var region = ReadCurrentRegion()
            ?? throw new InvalidOperationException("读不出游戏当前的区服(.build.info 异常)。");

        Directory.CreateDirectory(PoolDir);
        Directory.CreateDirectory(ManifestDir);

        // 上一份清单用作「size+mtime 没变就沿用旧哈希」的缓存 —— 不然每次切号都要哈希几百 MB
        var prev = ReadManifest(region)?.Files ?? new Dictionary<string, GameStateFile>();

        var man = new GameStateManifest
        {
            Region = region,
            GameRoot = GameRoot,
            CapturedUtc = DateTime.UtcNow,
        };

        long newBytes = 0;
        foreach (var (full, rel) in Enumerate())
        {
            var fi = new FileInfo(full);
            var ticks = fi.LastWriteTimeUtc.Ticks;

            string sha;
            if (prev.TryGetValue(rel, out var old) && old.Size == fi.Length
                && old.MtimeTicks == ticks && File.Exists(PoolPath(old.Sha256)))
            {
                sha = old.Sha256;                 // 没动过,连读都不用读
            }
            else
            {
                sha = Sha256Of(full);
                var pool = PoolPath(sha);
                if (!File.Exists(pool))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pool)!);
                    File.Copy(full, pool, overwrite: true);
                    newBytes += fi.Length;
                }
            }

            man.Files[rel] = new GameStateFile { Size = fi.Length, Sha256 = sha, MtimeTicks = ticks };
        }

        File.WriteAllText(ManifestFile(region),
            JsonSerializer.Serialize(man, new JsonSerializerOptions { WriteIndented = true }));
        return (region, man.Files.Count, newBytes);
    }

    private string ManifestFile(string region) =>
        Path.Combine(ManifestDir, region.ToUpperInvariant() + ".json");

    public GameStateManifest? ReadManifest(string region)
    {
        try
        {
            var f = ManifestFile(region);
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<GameStateManifest>(File.ReadAllText(f));
        }
        catch { return null; }
    }

    public bool Has(string region) => ReadManifest(region) is not null;


    // ---------- 还原 ----------

    /// <summary>
    /// 把某个区服的快照还原回去。<b>调用前必须确认战网客户端和 Agent 都已退出</b>,
    /// 否则 Agent 会把 product.db 覆盖回它内存里的旧值,白做(见类注释规则 3)。
    /// 返回 (真正写盘的文件数, 已一致跳过的文件数, 隔离的索引数)。
    /// </summary>
    public (int Restored, int Skipped, int Quarantined) Restore(string region)
    {
        if (GameRoot is null)
            throw new InvalidOperationException("没有找到守望先锋的安装目录。");
        var man = ReadManifest(region)
            ?? throw new InvalidOperationException($"还没有保存过「{region}」区服的游戏状态。");
        if (!string.Equals(man.GameRoot, GameRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"这份快照是给「{man.GameRoot}」存的,当前安装在「{GameRoot}」,不还原以免弄坏游戏。");

        // 1) 隔离不属于本快照的 .idx —— 同分桶取序号最高的,留着会挑到另一个区服的索引
        var keep = man.Files.Keys.Where(k => k.EndsWith(".idx", StringComparison.OrdinalIgnoreCase))
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var qdir = Path.Combine(QuarantineDir,
            $"idx-{region}-{DateTime.Now:HHmmss}");
        var quarantined = 0;
        foreach (var sub in IdxDirs)
        {
            var d0 = Path.Combine(GameRoot, sub);
            if (!Directory.Exists(d0)) continue;
            foreach (var f in Directory.EnumerateFiles(d0, "*.idx", SearchOption.AllDirectories))
            {
                var rel = "game/" + Path.GetRelativePath(GameRoot, f).Replace('\\', '/');
                if (keep.Contains(rel)) continue;
                var d = Path.Combine(qdir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.Move(f, d, overwrite: true);
                quarantined++;
            }
        }

        // 2) 从内容池写回
        var restored = 0;
        var skipped = 0;
        foreach (var (rel, meta) in man.Files)
        {
            var pool = PoolPath(meta.Sha256);
            if (!File.Exists(pool)) continue;      // 池里缺文件:跳过,别写半截
            var dst = ResolveTarget(rel);
            if (dst is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                var fi = new FileInfo(dst);
                var attr = fi.Attributes;
                if (attr.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);

                // 已经是这份内容就别白写。这里【只比 size+mtime,绝不算哈希】——
                // 两个区服有一大半文件是相同的,逐个哈希等于每次切号白读几百 MB(实测就是卡在这)。
                // File.Copy 会保留时间戳,所以池里拷回来的文件 mtime 和清单里记的一致,比对得上。
                if (fi.Length == meta.Size && fi.LastWriteTimeUtc.Ticks == meta.MtimeTicks)
                {
                    skipped++;
                    continue;
                }
            }
            File.Copy(pool, dst, overwrite: true);
            restored++;
        }

        return (restored, skipped, quarantined);
    }

    private string? ResolveTarget(string rel)
    {
        var i = rel.IndexOf('/');
        if (i < 0) return null;
        var prefix = rel[..i];
        var tail = rel[(i + 1)..].Replace('/', Path.DirectorySeparatorChar);
        return prefix switch
        {
            "game" => GameRoot is null ? null : Path.Combine(GameRoot, tail),
            "agent" => Path.Combine(AgentDir, tail),
            _ => null,
        };
    }


}
