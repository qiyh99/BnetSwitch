using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BnetSwitch.Services;

public sealed record AccountProfileMeta(long AccountId, string BattleTag, DateTime SavedAtUtc);

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
