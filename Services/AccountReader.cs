using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BnetSwitch.Models;
using Microsoft.Data.Sqlite;

namespace BnetSwitch.Services;

/// <summary>
/// 从战网 CachedData.db 读取账号列表,以及「当前登录的是哪个账号」。
/// </summary>
public sealed class AccountReader
{
    private readonly BattleNetPaths _paths;

    public AccountReader(BattleNetPaths paths) => _paths = paths;

    /// <summary>
    /// 读取所有账号。<paramref name="activeAccountId"/> 返回当前活跃账号的 id(读不到则为 null)。
    /// </summary>
    public IReadOnlyList<BattleAccount> ReadAccounts(out long? activeAccountId)
    {
        activeAccountId = null;
        var list = new List<BattleAccount>();
        if (!File.Exists(_paths.CachedDataDb)) return list;

        // 复制一份再读,避免战网正在运行时的文件锁 / WAL 状态问题。
        var tmp = Path.Combine(Path.GetTempPath(), $"bam_cacheddata_{Guid.NewGuid():N}.db");
        CopyDbWithSidecars(_paths.CachedDataDb, tmp);
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = tmp,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name, environment, battle_tag, account_id_lo FROM login_cache";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new BattleAccount
                    {
                        InternalName = r.IsDBNull(0) ? "" : r.GetString(0),
                        Environment = r.IsDBNull(1) ? "" : r.GetString(1),
                        BattleTag = r.IsDBNull(2) ? "" : r.GetString(2),
                        AccountId = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    });
                }
            }

            activeAccountId = ReadActiveAccountId(conn);
        }
        finally
        {
            TryDeleteWithSidecars(tmp);
        }

        return list;
    }

    /// <summary>
    /// 只读取「当前登录账号」的 id。保存 / 切换前实时调用,避免用界面上缓存的旧值。
    /// </summary>
    public long? ReadActiveAccountId()
    {
        if (!File.Exists(_paths.CachedDataDb)) return null;

        var tmp = Path.Combine(Path.GetTempPath(), $"bam_active_{Guid.NewGuid():N}.db");
        CopyDbWithSidecars(_paths.CachedDataDb, tmp);
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = tmp,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            return ReadActiveAccountId(conn);
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDeleteWithSidecars(tmp);
        }
    }

    /// <summary>
    /// 读当前活跃指针 features_cached_data_points 的原始 JSON(含 account_id / account_region 等)。
    /// 这条在 %LOCALAPPDATA%\Battle.net\CachedData.db,切换只换 %APPDATA% 时不会动它,
    /// 同邮箱 KR/CN 互切会因此残留旧区域指针 → 连接错。保存快照时一并存下,切换时写回。
    /// </summary>
    public string? ReadActivePointerJson()
    {
        if (!File.Exists(_paths.CachedDataDb)) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"bam_ptr_{Guid.NewGuid():N}.db");
        CopyDbWithSidecars(_paths.CachedDataDb, tmp);
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM key_value_store WHERE key = 'features_cached_data_points'";
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
        finally { TryDeleteWithSidecars(tmp); }
    }

    /// <summary>
    /// 把活跃指针 features_cached_data_points 写回 CachedData.db。**调用前战网必须已优雅退出**(否则文件锁 / 被覆盖)。
    /// 直接改主库并 checkpoint 折叠 WAL,确保下次启动即生效。成功返回 true。
    /// </summary>
    public bool WriteActivePointer(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !File.Exists(_paths.CachedDataDb)) return false;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = _paths.CachedDataDb, Mode = SqliteOpenMode.ReadWrite }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE key_value_store SET value = $v WHERE key = 'features_cached_data_points'";
                cmd.Parameters.AddWithValue("$v", json);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    cmd.CommandText = "INSERT INTO key_value_store(key, value) VALUES('features_cached_data_points', $v)";
                    cmd.ExecuteNonQuery();
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 以 %APPDATA%\Battle.net\Battle.net.config(live 文件)为准,判定「当前实时登录状态属于哪个账号」。
    /// 保存快照前用它决定存进哪个号 —— 比 <see cref="ReadActiveAccountId()"/>(读 CachedData.db 指针)可靠:
    /// 那个指针在 %LOCALAPPDATA%,而切换只还原 %APPDATA%,两者会脱钩,导致把当前状态存进【错误号】的快照(快照污染,切换后掉登录页)。
    /// 做法:取 SavedAccountNames 第一项登录名,算 FNV-1a-64(大写)去匹配 login_cache.name;
    /// 同一登录名的多张卡(同邮箱不同区服,如 KR/CN)再用 LastLoginRegion 区分。
    /// 判定不出唯一账号时返回 null —— 宁可这次不更新快照,也绝不往错的快照里灌。
    /// </summary>
    public long? ResolveCurrentAccountFromConfig()
    {
        if (!TryReadCurrentLogin(out var login, out var region) || string.IsNullOrWhiteSpace(login))
            return null;

        var wantHash = Fnv1a64Upper(login!);
        var accounts = ReadAccounts(out _);
        var candidates = accounts
            .Where(a => string.Equals(a.InternalName, wantHash, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].AccountId;

        // 同一登录名多张卡:同邮箱不同区服,用 LastLoginRegion(CN / KR / …)区分。
        // 国服账号的 Environment 含 "battlenet.com.cn",其余(KR/US/EU)不含。
        var liveIsCn = string.Equals(region, "CN", StringComparison.OrdinalIgnoreCase);
        var byRegion = candidates
            .Where(a => a.Environment.Contains("battlenet.com.cn", StringComparison.OrdinalIgnoreCase) == liveIsCn)
            .ToList();
        return byRegion.Count == 1 ? byRegion[0].AccountId : null;
    }

    /// <summary>读 live Battle.net.config 的 SavedAccountNames 第一项登录名 + LastLoginRegion。</summary>
    private bool TryReadCurrentLogin(out string? firstLogin, out string? region)
    {
        firstLogin = null;
        region = null;
        if (!File.Exists(_paths.RoamingConfig)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_paths.RoamingConfig));
            var root = doc.RootElement;
            if (root.TryGetProperty("Client", out var client) &&
                client.TryGetProperty("SavedAccountNames", out var san) &&
                san.ValueKind == JsonValueKind.String)
            {
                var raw = san.GetString() ?? "";
                firstLogin = raw
                    .Split(new[] { ',', ';', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            }
            region = FindFirstString(root, "LastLoginRegion");
            return firstLogin is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>递归找 JSON 里第一个名为 <paramref name="name"/> 的字符串属性值(install 节点名会变,不能写死路径)。</summary>
    private static string? FindFirstString(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.String)
                        return p.Value.GetString();
                    var sub = FindFirstString(p.Value, name);
                    if (sub is not null) return sub;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var sub = FindFirstString(item, name);
                    if (sub is not null) return sub;
                }
                break;
        }
        return null;
    }

    /// <summary>FNV-1a-64(登录名转大写),与 login_cache.name 同口径。</summary>
    private static string Fnv1a64Upper(string s)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(s.ToUpperInvariant()))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return h.ToString("x16");
    }

    /// <summary>
    /// 当前账号指针存在 key_value_store 的 features_cached_data_points 里:
    /// {"account_id":609695527,...}
    /// </summary>
    private static long? ReadActiveAccountId(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT value FROM key_value_store WHERE key = 'features_cached_data_points'";
            if (cmd.ExecuteScalar() is not string val || string.IsNullOrEmpty(val)) return null;

            using var doc = JsonDocument.Parse(val);
            if (doc.RootElement.TryGetProperty("account_id", out var idEl) &&
                idEl.TryGetInt64(out var id))
                return id;
        }
        catch
        {
            // 表或键不存在时忽略,退回到「未知」。
        }
        return null;
    }

    private static void CopyDbWithSidecars(string src, string dst)
    {
        File.Copy(src, dst, overwrite: true);
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(src + ext))
                File.Copy(src + ext, dst + ext, overwrite: true);
    }

    private static void TryDeleteWithSidecars(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { /* 临时文件,删不掉也无妨 */ }
        }
    }
}
