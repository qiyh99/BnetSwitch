using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace BnetSwitch.Services;

/// <summary>
/// 免密令牌本体存在 HKCU\Software\Blizzard Entertainment\Battle.net\UnifiedAuth 下:
/// 每个账号占一个「槽」(值名是 8 位十六进制,如 4EB0C645),值是 DPAPI 封装的二进制(绑当前 Windows 用户)。
///
/// 【为什么必须碰它】同一个邮箱在两个区服注册的两个账号(一个 CN、一个 KR)
/// 【共用同一个槽】—— 谁后登录,槽里就是谁的令牌。另一个号再切过去,就是拿着【错区服的令牌】
/// 去敲服务器 → 被拒 → 「无法登录战网」。换文件、换区域指针都救不了,因为坏的是令牌本身。
///
/// 实测坐实:同邮箱两个区服各手动登录一次,所有槽里【只有共用的那一个】值变了;
/// 把先前那份值写回去、再启动客户端,就免密登了回去(客户端随后自己把配置和区域指针也改回来)。
///
/// 【两条铁律】
/// 1. 只【读取】和【写回自己存过的值】,绝不 delete、绝不清空 —— 删槽会把并存的其他账号令牌一起毁掉,
///    那是早期所有切换失败的根因(见 RegistryStore 那套已废弃的方案)。
/// 2. 写回必须在【客户端完全退出之后、启动之前】。让客户端拿着错令牌启动,
///    它会自己把槽删掉(客户端日志:BattleNetLogin::DeleteToken)。
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

    /// <summary>把一个槽的值写回注册表(只写,不删)。成功返回 true。</summary>
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
