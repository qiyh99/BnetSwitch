using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BnetSwitch.Services.Overwatch;

// 守望先锋战绩 —— HTTP 链路(C# 移植自 ow_query.py，另加 searchBnetAccount / getUserConfig）。
// 链路：网易大神扫码登录 → SSO 到 ds 域 → getReportToken(roleId) 换 bigdata token → 查战绩。
// ds 域 POST 需 gl-* 头 + GL-CheckSum=sha1(body+GL-XSRF-TOKEN cookie)；datamsapi 的 GET 只认 URL token。
public sealed class DashenClient
{
    // ---- 常量（来自网易DD抓包）----
    private const string Product = "cc_team";
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36 app/df_client";
    private const string Origin = "https://ccact.ds.163.com";
    private const string Referer = "https://ccact.ds.163.com/m/daily/df_homepage/owCard.html";
    private const string Q = "https://q.reg.163.com/qrcode";
    private const string Callback = "https://api.cc.163.com/v1/mixteamauth/cookie2LoginToken";
    private const string DS = "https://inf.ds.163.com/v1/web";
    private const string DATAMS = "https://datamsapi.ds.163.com/v1/a19ld5tool";
    public const string AppKeyOw = "bn";
    public const string Dts = "2026";

    private readonly HttpClient _http;
    private readonly CookieContainer _jar = new();
    private readonly string _deviceId = Guid.NewGuid().ToString();

    private string? _qrUuid;          // 当前二维码 uuid（轮询用）
    private string? _setCookieUrl;     // 扫码确认后拿登录态 cookie 的地址

    public DashenClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _jar,
            UseCookies = true,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // 等价 requests verify=False
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
    }

    // ============ cookie 会话导出/导入（免重复扫码用，DashenAuth 调）============
    public IReadOnlyList<(string Name, string Value, string Domain, string Path)> ExportCookies()
    {
        var list = new List<(string, string, string, string)>();
        foreach (Cookie c in _jar.GetAllCookies())
            list.Add((c.Name, c.Value, c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path));
        return list;
    }

    public void ImportCookies(IEnumerable<(string Name, string Value, string Domain, string Path)> cookies)
    {
        foreach (var (name, value, domain, path) in cookies)
        {
            try { _jar.Add(new Cookie(name, value, string.IsNullOrEmpty(path) ? "/" : path, domain)); }
            catch { /* 个别 cookie 格式问题忽略 */ }
        }
    }

    private string? FindCookie(string name)
    {
        foreach (Cookie c in _jar.GetAllCookies())
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return c.Value;
        return null;
    }

    // ============ 通用工具 ============
    // URS 接口响应首行是内部状态码，从第一个 { 开始解析
    private static JsonElement UrsJson(string text)
    {
        int i = text.IndexOf('{');
        if (i < 0) return default;
        using var doc = JsonDocument.Parse(text[i..]);
        return doc.RootElement.Clone();
    }

    private static string Checksum(string body, string xsrf)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(body + xsrf))).ToLowerInvariant();

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> kv)
    {
        var sb = new StringBuilder();
        foreach (var p in kv)
        {
            sb.Append(sb.Length == 0 ? '?' : '&');
            sb.Append(Uri.EscapeDataString(p.Key)).Append('=').Append(Uri.EscapeDataString(p.Value));
        }
        return sb.ToString();
    }

    private static string Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    // 带 gl-* 头 + 签名的 POST(ds 域 & datamsapi 鉴权 POST 都走它),返回原始响应文本。
    public async Task<string> AuthedPostRawAsync(string url, object obj)
    {
        string body = JsonSerializer.Serialize(obj);   // 紧凑 JSON；checksum 与 body 用同一字符串即自洽
        string xsrf = FindCookie("GL-XSRF-TOKEN") ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("gl-clienttype", "60");
        req.Headers.TryAddWithoutValidation("gl-deviceid", _deviceId);
        req.Headers.TryAddWithoutValidation("gl-uid", FindCookie("GOD_UUID") ?? "");
        req.Headers.TryAddWithoutValidation("gl-x-xsrf-token", xsrf);
        req.Headers.TryAddWithoutValidation("gl-checksum", Checksum(body, xsrf));
        req.Headers.TryAddWithoutValidation("Origin", Origin);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        using var resp = await _http.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    // 带 gl-* 头的 GET(getFriendModule 这类"自己会话"鉴权的 GET;无 body 不需 checksum)
    public async Task<string> AuthedGetRawAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("gl-clienttype", "60");
        req.Headers.TryAddWithoutValidation("gl-deviceid", _deviceId);
        req.Headers.TryAddWithoutValidation("gl-uid", FindCookie("GOD_UUID") ?? "");
        req.Headers.TryAddWithoutValidation("gl-x-xsrf-token", FindCookie("GL-XSRF-TOKEN") ?? "");
        req.Headers.TryAddWithoutValidation("Origin", Origin);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        using var resp = await _http.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>好友列表(getFriendModule,roleId 驱动)。reportToken=该roleId的报告token;mode 如 SportPreset。</summary>
    public Task<string> GetFriendModuleRawAsync(string reportToken, long roleId, int season, string mode, int page, int size)
        => AuthedGetRawAsync($"{DATAMS}/getFriendModule?token={reportToken}&roleId={roleId}&season={season}&dts={Dts}&server=1&mode={mode}&size={size}&page={page}&sortKey=match_cnt&sortType=0");

    /// <summary>好友排行榜(bnFriend/getBillboard),比 getFriendModule 更全(billboardList),但需该玩家 oauth=true。</summary>
    public Task<string> GetFriendBillboardRawAsync(string reportToken, long roleId, int season, string mode)
        => AuthedGetRawAsync($"{DATAMS}/bnFriend/getBillboard?season={season}&roleId={roleId}&token={reportToken}&dts={Dts}&server=1&mode={mode}");

    private async Task<JsonElement> DsPostAsync(string url, object obj)
    {
        using var doc = JsonDocument.Parse(await AuthedPostRawAsync(url, obj));
        return doc.RootElement.Clone();
    }

    private async Task<string> DataGetAsync(string endpoint, params (string, string)[] query)
    {
        var kv = query.Select(t => new KeyValuePair<string, string>(t.Item1, t.Item2));
        return await _http.GetStringAsync($"{DATAMS}/{endpoint}{BuildQuery(kv)}");
    }

    // ============ ①~④ 扫码登录 ============
    public sealed record QrResult(byte[] ImageBytes, string Uuid);

    /// <summary>生成登录二维码（返回 PNG 字节 + uuid）。</summary>
    public async Task<QrResult> CreateLoginQrAsync()
    {
        string idText = await _http.GetStringAsync($"{Q}/getqrcodeid{BuildQuery(new[] { new KeyValuePair<string, string>("product", Product) })}");
        var idJson = UrsJson(idText);
        _qrUuid = idJson.GetProperty("l").GetProperty("i").GetString();
        _setCookieUrl = null;

        byte[] png = await _http.GetByteArrayAsync($"{Q}/getGeneralUrlQrcode" + BuildQuery(new[]
        {
            new KeyValuePair<string, string>("uuid", _qrUuid!),
            new KeyValuePair<string, string>("size", "240"),
            new KeyValuePair<string, string>("format", "png"),
            new KeyValuePair<string, string>("product", Product),
            new KeyValuePair<string, string>("rtid", ""),
            new KeyValuePair<string, string>("url", Callback),
            new KeyValuePair<string, string>("url2", Callback),
        }));
        return new QrResult(png, _qrUuid!);
    }

    public enum ScanState { Waiting, Confirmed, Expired }

    public sealed record PollResult(ScanState State, string UserName = "");

    /// <summary>轮询一次扫码状态。Confirmed 时内部已记下 setCookieUrl，随后调 CompleteLoginAsync。</summary>
    public async Task<PollResult> PollOnceAsync()
    {
        if (_qrUuid is null) return new PollResult(ScanState.Expired);
        string text = await _http.GetStringAsync($"{Q}/qrcodeauth" + BuildQuery(new[]
        {
            new KeyValuePair<string, string>("product", Product),
            new("client", "pc"), new("newQrCode", "1"), new("uuid", _qrUuid),
        }));
        var d = UrsJson(text);
        string code = Str(d, "retCode");
        if (code == "200")
        {
            _setCookieUrl = (d.TryGetProperty("crossSetCookieUrl", out var u) ? u.GetString() : null) ?? $"{Q}/qrcodeSetCookie";
            return new PollResult(ScanState.Confirmed, Str(d, "userName"));
        }
        if (code is "401" or "402" or "403" or "411") return new PollResult(ScanState.Expired);
        return new PollResult(ScanState.Waiting);   // 408 等待中
    }

    /// <summary>扫码确认后：拿登录态 cookie（NTES_YD_SESS）+ SSO 到 ds 域。成功后会话即可用。</summary>
    public async Task CompleteLoginAsync()
    {
        if (_setCookieUrl is null || _qrUuid is null) throw new InvalidOperationException("未确认扫码");
        string setUrl = _setCookieUrl.Replace("http://", "https://");
        await _http.GetStringAsync(setUrl + BuildQuery(new[]
        {
            new KeyValuePair<string, string>("product", Product),
            new("uuid", _qrUuid), new("url", Callback), new("url2", Callback),
        }));
        await SsoAsync();
    }

    // ============ ⑤ SSO 到 ds 域 ============
    public async Task SsoAsync()
    {
        var d = await DsPostAsync($"{DS}/base/login", new { });
        if (Str(d, "code") != "200" && (!d.TryGetProperty("code", out var c) || c.GetInt32() != 200))
            throw new InvalidOperationException("ds SSO 失败: " + d);
    }

    /// <summary>会话是否还有效（用 base/login 探一下）。</summary>
    public async Task<bool> IsSessionAliveAsync()
    {
        try { await SsoAsync(); return true; } catch { return false; }
    }

    // ============ ⑥ 扫码号自己的 OW 角色（可选，主流程用不到）============
    public async Task<(long RoleId, string Server)?> GetOwnRoleAsync()
    {
        var d = await DsPostAsync($"{DS}/role-list-query/getBindList", new { });
        if (!d.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var role in arr.EnumerateArray())
            if (Str(role, "appKey") == AppKeyOw)
                return (long.Parse(Str(role, "roleId")), role.TryGetProperty("server", out var s) ? s.ToString() : "1");
        return null;
    }

    // ============ ⑦ 换 bigdata token（按 roleId，token 与该 roleId 绑定）============
    public async Task<string> GetReportTokenAsync(long roleId, string server = "1")
    {
        var d = await DsPostAsync($"{DS}/game/report/getReportToken",
            new { appKey = AppKeyOw, roleId, server, source = 1, type = "yearly" });
        if (!d.TryGetProperty("result", out var r) || !r.TryGetProperty("token", out var t))
            throw new InvalidOperationException("换 token 失败: " + d);
        return t.GetString() ?? throw new InvalidOperationException("token 为空");
    }

    // ============ ⑧ 战绩查询（GET，只认 URL token）============
    /// <summary>概览卡：名字/称号/等级/时长。season=1 生涯。返回原始 JSON。</summary>
    public Task<string> QueryCardRawAsync(long roleId, string token, string season = "1", string server = "1")
        => DataGetAsync("queryCard", ("roleId", roleId.ToString()), ("season", season), ("token", token), ("server", server), ("dts", Dts));

    /// <summary>详细战绩:段位/场均/近12场/英雄使用。season 传当前赛季号,gameMode=sport。返回原始 JSON。</summary>
    public Task<string> QueryCountInfoRawAsync(long roleId, string token, string season, string gameMode = "sport", string server = "1")
        => DataGetAsync("queryCountInfo", ("roleId", roleId.ToString()), ("season", season), ("token", token),
            ("server", server), ("dts", Dts), ("gameMode", gameMode));

    /// <summary>用户配置(含当前赛季等)。返回原始 JSON。</summary>
    public Task<string> GetUserConfigRawAsync(long roleId, string token, string server = "1")
        => DataGetAsync("getUserConfig", ("roleId", roleId.ToString()), ("token", token), ("server", server), ("dts", Dts));

    // ============ 查别人:customer/* + customerToken ============
    // customerToken = base64("sign=<自己的sign>&bnetId=<目标>&timestamp=<毫秒>")。sign 来自自己 queryCard 的 customerToken。
    public static string BuildCustomerToken(string sign, long bnetId)
    {
        string raw = $"sign={sign}&bnetId={bnetId}&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>解码自己的 customerToken,取出 sign(用于给任意目标构造 customerToken)。</summary>
    public static string ExtractSign(string ownCustomerToken)
    {
        try
        {
            string dec = Encoding.UTF8.GetString(Convert.FromBase64String(ownCustomerToken));
            foreach (var part in dec.Split('&'))
                if (part.StartsWith("sign=")) return part[5..];
        }
        catch { }
        return "";
    }

    /// <summary>customer/* 查任意 bnetId:URL 带 customerToken,头带 GL-Bigdata-*(用自己的 bigdata token + roleId)。</summary>
    public Task<string> CustomerGetRawAsync(string endpoint, string customerToken, string bigdataToken, long ownRoleId, params (string, string)[] extra)
        => CustomerGetPathRawAsync($"customer/{endpoint}", customerToken, bigdataToken, ownRoleId, extra);

    /// <summary>同上但传完整子路径(如 billboard/customGetUserHeroBillboard),GL-Bigdata-* 鉴权一致。</summary>
    public async Task<string> CustomerGetPathRawAsync(string path, string customerToken, string bigdataToken, long ownRoleId, params (string, string)[] extra)
    {
        var kv = new List<KeyValuePair<string, string>> { new("token", customerToken) };
        foreach (var (k, v) in extra) kv.Add(new(k, v));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DATAMS}/{path}{BuildQuery(kv)}");
        req.Headers.TryAddWithoutValidation("GL-Bigdata-Auth-Token", bigdataToken);
        req.Headers.TryAddWithoutValidation("GL-Bigdata-Role-Id", ownRoleId.ToString());
        req.Headers.TryAddWithoutValidation("GL-Bigdata-Server", "1");
        req.Headers.TryAddWithoutValidation("GL-Bigdata-Dts", Dts);
        req.Headers.TryAddWithoutValidation("Origin", Origin);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        using var resp = await _http.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>按 BattleTag 搜账号(查别人)。POST,token/roleId/name 全在 body。返回含 bnetId + customerToken。</summary>
    public async Task<string> SearchBnetAccountRawAsync(string bigdataToken, long ownRoleId, string name)
    {
        var body = new { token = bigdataToken, roleId = ownRoleId, dts = Dts, server = "1", name };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{DATAMS}/searchBnetAccount")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Origin", Origin);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        using var resp = await _http.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }
}
