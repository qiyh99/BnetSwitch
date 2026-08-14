using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace BnetSwitch.Services;

/// <summary>
/// 免密令牌本体存在 HKCU\Software\Blizzard Entertainment\Battle.net\UnifiedAuth 下:
/// 每个账号占一个「槽」(值名是 8 位十六进制,如 4EB0C645),值是 DPAPI 封装的二进制(绑当前 Windows 用户)。
///
/// 同一个邮箱在两个区服注册的两个账号(一个 CN、一个 KR)【共用同一个槽】,谁后登录槽里就是谁的令牌。
///
/// 【本类只读,绝不写回 —— 这是 2026-08-14 用一串坏掉的账号换来的结论】
/// 曾经尝试过「把快照里存的令牌写回槽」来解决同邮箱那一对,当场看着是成功的,实际是灾难:
/// 暴雪的免密令牌【一次性轮换】,每次成功登录都会发新的、作废旧的。快照里那份只要该号之后
/// 又登录过就已经作废,写回去必然被服务端拒绝:
///     W [LoginController] Tassadar token rejected by BGS: web_auth_url
///     I [BNLogin] DeleteToken(): Deleting registry token because !m_lastEmailUsed.empty()
/// 客户端一被拒就【把整个槽删掉】—— 死令牌没用上,槽里原本那个【活的】也一起赔进去。
/// 实测:开启写回后每次启动删 4 个令牌,半小时报废了三个账号的免密。
///
/// 所以:令牌可以【读】(存进快照供排障、判断某个号是否还有免密),但【任何情况下都不写】。
/// 同邮箱那一对目前无解 —— 只能接受「在这两个号之间来回切,要手动登一次密码」。
/// </summary>
public sealed class TokenStore
{
    private const string KeyPath = @"Software\Blizzard Entertainment\Battle.net\UnifiedAuth";

    /// <summary>读出所有槽(槽名 → 原始字节)。读不到就返回空字典,绝不抛 —— 令牌读不到只是没法修同邮箱那一对,不该拖垮切换。</summary>
    public Dictionary<string, byte[]> ReadAll()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key is null) return map;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is byte[] b && b.Length > 0)
                    map[name] = b;
        }
        catch { }
        return map;
    }

    // 【故意不提供写入方法】2026-08-14 实测:暴雪令牌一次性轮换,把快照里的旧令牌写回注册表
    // 会被服务器拒绝(Tassadar token rejected by BGS),客户端随即把整个槽删掉 ——
    // 旧令牌没用上,槽里原本的活令牌也一起没了。所以本类【只读】。

    /// <summary>
    /// 全局「上次见到的槽状态」(槽名 → sha256),用来学出【哪个槽属于哪个号】:
    /// 某个号登录之后,和上次相比变化的那个槽,就是它的。
    ///
    /// 【为什么必须学出来】早先的做法是拿两个号的快照互相 diff 取「有争议的槽」,
    /// 结果第三个号中途登录过、它的槽在两份快照里也不一样,于是会被一起写回旧值 ——
    /// 修一个号的同时把另一个号的有效令牌覆盖成过期的。精确到「这个号自己的槽」才安全。
    /// </summary>
    private static string LastSeenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BnetSwitch", "uauth_lastseen.json");

    public static Dictionary<string, string> ReadLastSeen()
    {
        try
        {
            if (File.Exists(LastSeenFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(LastSeenFile)) ?? new();
        }
        catch { }
        return new();
    }

    public static void WriteLastSeen(IReadOnlyDictionary<string, byte[]> slots)
    {
        try
        {
            var dto = slots.ToDictionary(kv => kv.Key, kv => Hash(kv.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(LastSeenFile)!);
            File.WriteAllText(LastSeenFile, JsonSerializer.Serialize(dto));
        }
        catch { }
    }

    public static string Hash(byte[] v) => Convert.ToHexString(SHA256.HashData(v));

    /// <summary>
    /// 和上次相比变化(或新增)的槽。刚好只变一个时,那就是刚登录的这个号自己的槽。
    /// 变了多个说明中间夹了别的登录,学不出来 —— 宁可不学,也别记错。
    /// </summary>
    public static List<string> ChangedSince(
        IReadOnlyDictionary<string, string> lastSeen,
        IReadOnlyDictionary<string, byte[]> current)
    {
        var list = new List<string>();
        foreach (var kv in current)
            if (!lastSeen.TryGetValue(kv.Key, out var old) || old != Hash(kv.Value))
                list.Add(kv.Key);
        return list;
    }
}
