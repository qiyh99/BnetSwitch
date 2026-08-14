using System.IO;
using System.Text.Json;
using BnetSwitch.Services.Overwatch;

namespace BnetSwitch.Services;

/// <summary>一个定位的段位。卡片上每个定位单独一个 pill。</summary>
public sealed class RankRole
{
    /// <summary>坦克 / 输出 / 治疗 / 开放。</summary>
    public string RoleCn { get; set; } = "";

    /// <summary>英文档位(Diamond),比高低用它。</summary>
    public string TierEn { get; set; } = "";

    /// <summary>1~5,1 最高;大师以上没小段是 0。</summary>
    public int Division { get; set; }

    /// <summary>直接显示的档位文案,如「钻石1」。</summary>
    public string Text { get; set; } = "";

    public string? BrushKey { get; set; }
    public string? IconPath { get; set; }
}

/// <summary>
/// 一个账号缓存下来的段位。
/// 顶部字段是「最高的那个定位」(排序/单 pill 用),<see cref="Roles"/> 是全部定位(卡片上全展开)。
/// </summary>
public sealed class RankEntry
{
    public long AccountId { get; set; }

    /// <summary>全部定位,已按高低排好。老版本的 ranks.json 没有这个字段 —— 读出来是空表,由 VM 兜底成单条。</summary>
    public List<RankRole> Roles { get; set; } = new();

    /// <summary>英文档位(Diamond)。比高低、换算图标都用它,别用中文。</summary>
    public string TierEn { get; set; } = "";

    /// <summary>1~5,1 最高。大师以上没有小段就是 0。</summary>
    public int Division { get; set; }

    /// <summary>tank / offense / support / open —— 最高的是哪个定位。</summary>
    public string Role { get; set; } = "";

    /// <summary>卡片上直接显示的文案,如「钻石1」。</summary>
    public string Text { get; set; } = "";

    /// <summary>Stats.Tier* 画刷 key。</summary>
    public string? BrushKey { get; set; }

    /// <summary>段位图标的本地绝对路径(OwImageCache 落的盘)。有它主界面才不用联网。</summary>
    public string? IconPath { get; set; }

    /// <summary>Tooltip 用的全量文案,如「坦克 白金3 · 输出 钻石1 · 治疗 黄金4」。</summary>
    public string AllText { get; set; } = "";

    public DateTime UpdatedUtc { get; set; }

    /// <summary>档位高低分:档位序 * 10 + (6 - 小段)。定位之间比谁高用这个。</summary>
    public int Score => OwMappings.TierOrder(TierEn) * 10 + (Division > 0 ? 6 - Division : 6);
}

/// <summary>
/// 段位缓存,存 %LOCALAPPDATA%\BnetSwitch\ranks.json。
///
/// 单独一个文件、不塞进 settings.json:刷新段位是整批重写,和用户设置混在一起没必要,
/// 而且这份数据坏掉/删掉都无所谓 —— 读不出来当空表处理,绝不能影响切号主流程。
/// </summary>
public sealed class RankStore
{
    private Dictionary<string, RankEntry> _map = new();

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ranks.json");
        }
    }

    public static RankStore Load()
    {
        var s = new RankStore();
        try
        {
            if (File.Exists(FilePath))
                s._map = JsonSerializer.Deserialize<Dictionary<string, RankEntry>>(File.ReadAllText(FilePath))
                         ?? new Dictionary<string, RankEntry>();
        }
        catch { s._map = new Dictionary<string, RankEntry>(); }
        return s;
    }

    public RankEntry? Get(long accountId) =>
        _map.TryGetValue(accountId.ToString(), out var e) ? e : null;

    public void Set(RankEntry entry)
    {
        entry.UpdatedUtc = DateTime.UtcNow;
        _map[entry.AccountId.ToString()] = entry;
    }

    public void Remove(long accountId) => _map.Remove(accountId.ToString());

    public void Save()
    {
        try
        {
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不了就算了,下次刷新再写 */ }
    }
}
