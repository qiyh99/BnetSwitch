using BnetSwitch.Services.Overwatch;
using BnetSwitch.Stats;
using System.Windows;
using System.Windows.Media;

namespace BnetSwitch.Services;

/// <summary>
/// 按区服去查每个号的 OW 段位,结果写进 <see cref="RankStore"/>。
///
/// 只在用户点「刷新段位」时跑 —— 国服一次要 3 + 2N 个请求还得有大神扫码会话,
/// 国际服每个号要拉 ~1.2 MB HTML,不能挂在启动或轮询上。
/// 单个号失败只跳过它,绝不让一个号的异常毁掉整批。
/// </summary>
public sealed class RankFetcher
{
    /// <summary>要查的一个号。</summary>
    public sealed record Target(long AccountId, string BattleTag, bool IsCn);

    /// <summary>Updated=拿到段位的个数;NoRank=查到了但本赛季未定级;Failed=查失败;CnSkipped=没授权跳过的国服号。</summary>
    public sealed record Result(int Updated, int NoRank, int Failed, int CnSkipped);

    /// <summary>拿网易大神会话(没有就弹扫码)。由界面层提供 —— VM 不该自己 new Window。</summary>
    public Func<Task<DashenClient?>>? CnAuthProvider { get; set; }

    private static Brush? FindBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    public async Task<Result> RefreshAsync(
        IReadOnlyList<Target> targets, RankStore store,
        Action<string>? log = null, CancellationToken ct = default)
    {
        int updated = 0, noRank = 0, failed = 0, cnSkipped = 0;

        var cn = targets.Where(t => t.IsCn).ToList();
        var intl = targets.Where(t => !t.IsCn).ToList();

        // ---- 国服:网易大神。授权只要一次,StatsService 内部会缓存握手结果 ----
        if (cn.Count > 0)
        {
            DashenClient? client = null;
            if (CnAuthProvider != null)
            {
                try { client = await CnAuthProvider(); } catch { client = null; }
            }

            if (client is null)
            {
                cnSkipped = cn.Count;   // 用户没扫码:跳过全部国服号,国际服照查
                log?.Invoke($"未登录网易大神,跳过 {cn.Count} 个国服号");
            }
            else
            {
                var svc = new StatsService(client);
                foreach (var t in cn)
                {
                    ct.ThrowIfCancellationRequested();
                    log?.Invoke($"查询段位:{t.BattleTag}");
                    try
                    {
                        var ps = await svc.LoadAsync(t.AccountId, FindBrush);
                        var entry = FromCnStats(t.AccountId, ps);
                        if (entry is null) { store.Remove(t.AccountId); noRank++; }
                        else { store.Set(entry); updated++; }
                    }
                    catch { failed++; }
                }
            }
        }

        // ---- 国际服 / 亚服:暴雪官方生涯页,免登录 ----
        if (intl.Count > 0)
        {
            var career = new CareerService();
            foreach (var t in intl)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"查询段位:{t.BattleTag}");
                try
                {
                    var outcome = await career.LoadAsync(t.BattleTag, force: true, ct);
                    // 生涯页 404 / 未公开:这号在暴雪侧就是查不到,不算失败,静默当没段位处理
                    if (outcome.NotFound) { store.Remove(t.AccountId); noRank++; continue; }
                    if (outcome.Profile is null) { failed++; continue; }
                    var entry = await FromCareerAsync(t.AccountId, outcome.Profile);
                    if (entry is null) { store.Remove(t.AccountId); noRank++; }
                    else { store.Set(entry); updated++; }
                }
                catch (OperationCanceledException) { throw; }
                catch { failed++; }
            }
        }

        store.Save();
        return new Result(updated, noRank, failed, cnSkipped);
    }

    // ---- 国服:PlayerStats 的四个 RoleRank → 最高的那个 ----
    private static RankEntry? FromCnStats(long accountId, PlayerStats? ps)
    {
        if (ps is null) return null;

        var picks = new List<(string RoleCn, string TierEn, int Div, string Cn, string? Brush, string? Icon)>();
        void Add(string roleCn, RoleRank? rr)
        {
            if (rr is null) return;
            // TierText 形如「钻石 1」;「未定级」没有档位可比
            var parts = rr.TierText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var en = OwMappings.TierEnFromCn(parts[0]);
            if (en is null) return;
            int div = parts.Length > 1 && int.TryParse(parts[1], out var d) ? d : 0;
            var (_, brushKey) = OwMappings.Rank(en);
            picks.Add((roleCn, en, div, parts[0], brushKey, rr.TierIconLocal));
        }

        Add("坦克", ps.Tank);
        Add("输出", ps.Dps);
        Add("治疗", ps.Support);
        Add("开放", ps.OpenRank);

        return Build(accountId, picks);
    }

    // ---- 国际服:生涯页解析结果的 Ranks → 最高的那个 ----
    private static async Task<RankEntry?> FromCareerAsync(long accountId, CareerParser.Profile p)
    {
        // 键鼠优先,键鼠没段位再看手柄
        var ranks = p.Find(CareerService.InputPc)?.Ranks;
        if (ranks is null || ranks.Count == 0) ranks = p.Find(CareerService.InputPad)?.Ranks;
        if (ranks is null || ranks.Count == 0) return null;

        var picks = new List<(string RoleCn, string TierEn, int Div, string Cn, string? Brush, string? Icon)>();
        foreach (var r in ranks)
        {
            var (cn, brushKey) = OwMappings.Rank(r.Tier);
            if (brushKey is null) continue;   // 未定级
            var icon = await OwImageCache.GetAsync(r.TierIconUrl);
            picks.Add((RoleCn(r.Role), r.Tier, r.Division, cn, brushKey, icon));
        }

        return Build(accountId, picks);
    }

    private static string RoleCn(string? role) => role?.ToLowerInvariant() switch
    {
        "tank" => "坦克",
        "offense" or "damage" or "dps" => "输出",
        "support" or "healer" => "治疗",
        _ => "开放",
    };

    private static RankEntry? Build(
        long accountId,
        List<(string RoleCn, string TierEn, int Div, string Cn, string? Brush, string? Icon)> picks)
    {
        if (picks.Count == 0) return null;

        // 档位序 * 10 + (6 - 小段):小段 1 最高,大师以上没小段按最高算
        int ScoreOf((string RoleCn, string TierEn, int Div, string Cn, string? Brush, string? Icon) x)
            => OwMappings.TierOrder(x.TierEn) * 10 + (x.Div > 0 ? 6 - x.Div : 6);

        var sorted = picks.OrderByDescending(ScoreOf).ToList();
        var top = sorted[0];

        return new RankEntry
        {
            AccountId = accountId,
            TierEn = top.TierEn,
            Division = top.Div,
            Role = top.RoleCn,
            Text = top.Div > 0 ? $"{top.Cn}{top.Div}" : top.Cn,
            BrushKey = top.Brush,
            IconPath = top.Icon,
            AllText = string.Join(" · ", sorted
                .Select(x => x.Div > 0 ? $"{x.RoleCn} {x.Cn}{x.Div}" : $"{x.RoleCn} {x.Cn}")),
            // 全部定位:卡片上一个定位一个 pill,不再只显示最高的那个
            Roles = sorted.Select(x => new RankRole
            {
                RoleCn = x.RoleCn,
                TierEn = x.TierEn,
                Division = x.Div,
                Text = x.Div > 0 ? $"{x.Cn}{x.Div}" : x.Cn,
                BrushKey = x.Brush,
                IconPath = x.Icon,
            }).ToList(),
        };
    }
}
