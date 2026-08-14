using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BnetSwitch.Services;

/// <summary>
/// <paramref name="Expired"/>:上次切过去没登上。免密令牌在注册表里、本工具从不碰,
/// 令牌被暴雪作废(或用户在战网点了退出登录)之后,快照就只是一个指向空处的指针 ——
/// 切过去战网只会退回登录页或上一个号。老 meta.json 没这个字段,反序列化取默认 false。
/// </summary>
public sealed record AccountProfileMeta(long AccountId, string BattleTag, DateTime SavedAtUtc, bool Expired = false);

/// <summary>
/// 账号切换的正确机制(实测验证):
///
/// - 每个账号的免密令牌都在注册表 UnifiedAuth 里,只要【不登出】就多账号并存;
/// - 「当前登录哪个号」由 %APPDATA%\Battle.net 里的文件(主要是 Battle.net.config 的
///   SavedAccountNames 顺序,第一个=当前号)决定;
/// - 所以【切换 = 只换这些文件,绝不碰注册表、绝不登出】,目标号的令牌本来就在 → 免密登入。
///
/// 本类负责把 %APPDATA%\Battle.net 整个文件夹按账号存档/还原,以及"新建账号"(清指针回登录页)。
/// 全程只操作文件,不需要管理员权限。
/// </summary>
public sealed class AppDataStore
{
    private readonly BattleNetPaths _paths;

    /// <summary>%LOCALAPPDATA%\BnetSwitch\accounts</summary>
    public string Root { get; }

    public AppDataStore(BattleNetPaths paths)
    {
        _paths = paths;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Root = Path.Combine(local, "BnetSwitch", "accounts");
        Directory.CreateDirectory(Root);
    }

    private string Dir(long id) => Path.Combine(Root, id.ToString());
    private string DataDir(long id) => Path.Combine(Dir(id), "BattleNet");
    private string MetaFile(long id) => Path.Combine(Dir(id), "meta.json");

    /// <summary>该账号在 CachedData.db 里的活跃指针(features_cached_data_points)备份,切换时写回 %LOCALAPPDATA%。</summary>
    private string PointerFile(long id) => Path.Combine(Dir(id), "cacheddata_pointer.json");

    /// <summary>把该账号的活跃指针 JSON 存进快照目录(空则不写,保留旧值)。</summary>
    public void SavePointer(long id, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        Directory.CreateDirectory(Dir(id));
        File.WriteAllText(PointerFile(id), json);
    }

    /// <summary>读该账号存下的活跃指针 JSON;没有则 null。</summary>
    public string? ReadPointer(long id)
    {
        var f = PointerFile(id);
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    /// <summary>该账号存快照那一刻的 UnifiedAuth 全部令牌槽(槽名 → base64)。切同邮箱的号时用来把令牌写回。</summary>
    private string TokensFile(long id) => Path.Combine(Dir(id), "uauth.json");

    /// <summary>把当前注册表里的令牌槽随快照一起存下(空则不写,保留旧值)。</summary>
    public void SaveTokens(long id, IReadOnlyDictionary<string, byte[]> slots)
    {
        if (slots.Count == 0) return;
        Directory.CreateDirectory(Dir(id));
        var dto = slots.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value));
        File.WriteAllText(TokensFile(id),
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>读该账号存下的令牌槽;没有就是空字典。</summary>
    public Dictionary<string, byte[]> ReadTokens(long id)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var f = TokensFile(id);
        if (!File.Exists(f)) return map;
        try
        {
            var dto = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f));
            if (dto is not null)
                foreach (var kv in dto)
                    map[kv.Key] = Convert.FromBase64String(kv.Value);
        }
        catch { }
        return map;
    }

    /// <summary>
    /// 该账号自己的令牌槽名(UnifiedAuth 下那个 8 位十六进制的值名)。
    /// 由「登录后哪个槽变了」学出来,见 <see cref="TokenStore.ChangedSince"/>。
    /// 还原令牌时只写这一个槽 —— 写多了会把别的号的有效令牌覆盖成过期的。
    /// </summary>
    private string OwnSlotFile(long id) => Path.Combine(Dir(id), "uauth_slot.txt");

    public void SaveOwnSlot(long id, string slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return;
        Directory.CreateDirectory(Dir(id));
        File.WriteAllText(OwnSlotFile(id), slot.Trim());
    }

    public string? ReadOwnSlot(long id)
    {
        try
        {
            var f = OwnSlotFile(id);
            if (!File.Exists(f)) return null;
            var s = File.ReadAllText(f).Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    /// <summary>该账号快照里的登录名(SavedAccountNames 第一项)。</summary>
    public string? ReadLoginName(long id) => ExtractLoginName(Path.Combine(DataDir(id), "Battle.net.config"));

    /// <summary>当前 live 配置里的登录名(SavedAccountNames 第一项)。</summary>
    public string? ReadLiveLoginName() => ExtractLoginName(_paths.RoamingConfig);

    private static string? ExtractLoginName(string cfg)
    {
        if (!File.Exists(cfg)) return null;
        try
        {
            var m = Regex.Match(File.ReadAllText(cfg), "\"SavedAccountNames\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success) return null;
            var first = m.Groups[1].Value.Split(',')[0].Trim();
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch { return null; }
    }

    public bool HasProfile(long id) => File.Exists(MetaFile(id));

    public AccountProfileMeta? ReadMeta(long id)
    {
        var f = MetaFile(id);
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<AccountProfileMeta>(File.ReadAllText(f)); }
        catch { return null; }
    }

    /// <summary>把当前 %APPDATA%\Battle.net 的全部文件存为该账号的快照。调用前应已优雅退出战网。</summary>
    public void Save(long accountId, string battleTag)
    {
        var data = DataDir(accountId);
        if (Directory.Exists(data))
            try { Directory.Delete(data, true); } catch { }
        Directory.CreateDirectory(data);

        if (Directory.Exists(_paths.RoamingDir))
            foreach (var f in Directory.EnumerateFiles(_paths.RoamingDir))
                ForceCopy(f, Path.Combine(data, Path.GetFileName(f)));

        File.WriteAllText(
            MetaFile(accountId),
            JsonSerializer.Serialize(
                new AccountProfileMeta(accountId, battleTag, DateTime.UtcNow),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>把某账号快照的文件还原回 %APPDATA%\Battle.net(只换文件,不碰注册表)。调用前必须已优雅退出战网。</summary>
    public void Restore(long accountId)
    {
        var data = DataDir(accountId);
        if (!Directory.Exists(data))
            throw new DirectoryNotFoundException($"账号 {accountId} 还没有快照。");

        Directory.CreateDirectory(_paths.RoamingDir);
        foreach (var f in Directory.EnumerateFiles(data))
            ForceCopy(f, Path.Combine(_paths.RoamingDir, Path.GetFileName(f)));
    }

    /// <summary>
    /// 标记 / 清除「登录已过期」。只改 meta.json,不动快照文件本身 ——
    /// 用户可能只是暂时登不上,别把他好不容易存下来的文件删了。
    /// </summary>
    public void SetExpired(long accountId, bool expired)
    {
        var meta = ReadMeta(accountId);
        if (meta is null || meta.Expired == expired) return;
        File.WriteAllText(
            MetaFile(accountId),
            JsonSerializer.Serialize(meta with { Expired = expired },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>删除某账号的快照(从本工具移除,不影响战网)。</summary>
    public void Delete(long accountId)
    {
        var dir = Dir(accountId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// "新建账号":清空 Battle.net.config 的 SavedAccountNames(纯文件层),让下次启动回到登录页。
    /// 这【不是登出】,注册表里已有账号的令牌全部保留 → 登了新号后各账号令牌并存。
    /// </summary>
    public void ClearCurrentPointer()
    {
        var cfg = _paths.RoamingConfig;
        if (!File.Exists(cfg)) return;
        ClearReadOnly(cfg);
        var s = File.ReadAllText(cfg);
        s = Regex.Replace(s, "(\"SavedAccountNames\"\\s*:\\s*)\"[^\"]*\"", "$1\"\"", RegexOptions.Singleline);
        File.WriteAllText(cfg, s);
    }

    private static void ForceCopy(string src, string dst)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        ClearReadOnly(dst);
        File.Copy(src, dst, overwrite: true);
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
    }
}
