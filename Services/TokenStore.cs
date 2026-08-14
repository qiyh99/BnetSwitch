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
/// 另一个号切过去时,把它自己那份令牌写回槽,即可免密登入(见 <see cref="Write"/> 的调用方)。
///
/// 【2026-08-14 的事故与真相,别再误删这块】
/// 当天一度出现「切号后账号丢失免密」,我误以为是「写回旧令牌被服务端拒→客户端删槽」,
/// 把写回整个删掉了。实际根因是那一版新加的【切号时强杀 Agent.exe】破坏了客户端会话,
/// 导致服务端拒绝令牌(日志 Tassadar token rejected by BGS)、客户端随即删槽 ——
/// 用「等 Agent 自己退出」的版本切很多次都没事。强杀 Agent 已改回默认关。
/// 写回本身是对的:只写「这个号自己的槽」的「当前最新令牌」、且在客户端退出时写。
///
/// 【铁律】只写自己存过的值,绝不 delete / 绝不清空 —— 删槽会把并存的其他账号令牌一起毁掉。
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

    /// <summary>
    /// 把一个槽的值写回注册表(只写,不删)。成功返回 true。
    ///
    /// 【用途】同邮箱两个区服共用一个槽,切过去时把这个号自己那份令牌写回去,免密登入。
    /// 【前提】必须由调用方保证:只写「这个号自己的槽」的「最新令牌」,且在客户端已退出时写。
    /// 2026-08-14 曾出事,但根因查明是【切号时强杀 Agent】破坏了会话导致服务端拒绝令牌,
    /// 不是写回本身的错;强杀 Agent 已改回默认关。
    /// </summary>
    public bool Write(string slot, byte[] value)
    {
        if (string.IsNullOrWhiteSpace(slot) || value.Length == 0) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(slot, value, RegistryValueKind.Binary);
            return true;
        }
        catch { return false; }
    }

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
