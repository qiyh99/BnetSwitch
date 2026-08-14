using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BnetSwitch.Models;
using BnetSwitch.Services;
using BnetSwitch.Services.Overwatch;
using Microsoft.Win32;

namespace BnetSwitch.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>列表里的一行:一个账号 + 它的快照/当前状态。</summary>
public sealed class AccountRow : ObservableObject
{
    public long AccountId { get; init; }
    public string BattleTag { get; init; } = "";

    /// <summary>login_cache.environment,如 cn.actual.battlenet.com.cn / kr.actual.battle.net。占位行拿不到,留空。</summary>
    public string Environment { get; init; } = "";

    /// <summary>国服?空环境(占位行)按国服算 —— 保持老行为,查战绩仍走现有国服窗。</summary>
    public bool IsCnRegion => IsCn(Environment) || string.IsNullOrWhiteSpace(Environment);

    public static bool IsCn(string? environment)
        => environment?.Contains("battlenet.com.cn", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>卡片上的区服短标。环境串拿不到就返回空 —— 宁可不标,也别把不知道的号硬说成国服。</summary>
    public string RegionText => Region(Environment);
    public Visibility RegionVisibility => RegionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>environment 形如 kr.actual.battle.net / cn.actual.battlenet.com.cn,第一段就是区服代码。</summary>
    public static string Region(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment)) return "";
        if (IsCn(environment)) return "国服";
        return environment.Split('.')[0].ToLowerInvariant() switch
        {
            "kr" => "亚服",
            "us" => "美服",
            "eu" => "欧服",
            "tw" => "台服",
            _ => "国际服",      // 暴雪新开的区/测试环境,别显示成空白
        };
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { Set(ref _isActive, value); Raise(nameof(CanSwitch)); }
    }

    private bool _hasProfile;
    public bool HasProfile
    {
        get => _hasProfile;
        set { Set(ref _hasProfile, value); Raise(nameof(ProfileText)); Raise(nameof(SavedRelative)); Raise(nameof(CanSwitch)); }
    }

    private DateTime? _savedAtUtc;
    public DateTime? SavedAtUtc
    {
        get => _savedAtUtc;
        set { Set(ref _savedAtUtc, value); Raise(nameof(ProfileText)); Raise(nameof(SavedRelative)); }
    }

    private bool _isExpired;
    /// <summary>
    /// 上次切过去没登上 —— 这个号的免密令牌已经失效,快照再切也没用,得在战网里重新登录一次再存。
    /// 由切换后的核对写入,存在 meta.json 里,重开工具仍在;重新保存快照时清掉。
    /// </summary>
    public bool IsExpired
    {
        get => _isExpired;
        set { Set(ref _isExpired, value); Raise(nameof(ExpiredVisibility)); Raise(nameof(SwitchText)); }
    }

    public Visibility ExpiredVisibility => _isExpired ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>过期的号点了也是白点,按钮直接改成把人带去登录页。</summary>
    public string SwitchText => _isExpired ? "重新登录" : "切换";

    // ---- 展示派生 ----
    public string NameOnly { get { var i = BattleTag.IndexOf('#'); return i < 0 ? BattleTag : BattleTag[..i]; } }
    public string HashTag { get { var i = BattleTag.IndexOf('#'); return i < 0 ? "" : BattleTag[i..]; } }
    public string AvatarText => string.IsNullOrEmpty(NameOnly) ? "?" : NameOnly.Substring(0, 1);
    public string AccountIdText => AccountId.ToString();

    private (Brush bg, Brush fg)? _av;
    private (Brush bg, Brush fg) Av => _av ??= Avatar.For(AccountId);
    public Brush AvatarBg => Av.bg;
    public Brush AvatarFg => Av.fg;

    public string ProfileText => HasProfile ? $"已保存 · {SavedAtUtc?.ToLocalTime():MM-dd HH:mm}" : "未保存";

    /// <summary>相对时间:今天/昨天/日期。</summary>
    public string SavedRelative
    {
        get
        {
            if (SavedAtUtc is null) return "未保存";
            var t = SavedAtUtc.Value.ToLocalTime();
            var d = DateTime.Now.Date - t.Date;
            if (d.Days == 0) return $"今天 {t:HH:mm}";
            if (d.Days == 1) return $"昨天 {t:HH:mm}";
            return $"{t:MM-dd HH:mm}";
        }
    }

    /// <summary>已有快照且不是当前账号,才能一键切换。</summary>
    public bool CanSwitch => HasProfile && !IsActive;

    // ---- 备注 / 置顶 / 段位:都存在 settings.json 与 ranks.json 里,
    //      ApplyAccounts 每次重建行时重新灌进来(行对象本身是一次性的)。----

    private string _note = "";
    /// <summary>用户给这个号写的备注。没存快照的号也能写。</summary>
    public string Note
    {
        get => _note;
        set { Set(ref _note, value ?? ""); Raise(nameof(NoteVisibility)); Raise(nameof(SearchFields)); }
    }

    public Visibility NoteVisibility => _note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { Set(ref _isPinned, value); Raise(nameof(PinVisibility)); Raise(nameof(PinMenuText)); }
    }

    public Visibility PinVisibility => _isPinned ? Visibility.Visible : Visibility.Collapsed;
    public string PinMenuText => _isPinned ? "取消置顶" : "置顶";

    private RankEntry? _rank;
    /// <summary>缓存下来的段位(只留最高的那个定位)。null = 还没查过 / 未定级。</summary>
    public RankEntry? Rank
    {
        get => _rank;
        set
        {
            Set(ref _rank, value);
            Raise(nameof(RankText)); Raise(nameof(RankVisibility)); Raise(nameof(RankBrush));
            Raise(nameof(RankIcon)); Raise(nameof(RankTip)); Raise(nameof(SearchFields));
            Raise(nameof(RankRoles));
        }
    }

    /// <summary>卡片上一个定位一个 pill 的展示对象。</summary>
    public sealed class RankRoleVM
    {
        public string RoleCn { get; init; } = "";
        public string Text { get; init; } = "";
        public Brush? Brush { get; init; }
        public ImageSource? Icon { get; init; }
    }

    /// <summary>
    /// 全部定位,卡片上挨个展开。
    /// 老版本 ranks.json 里没有 Roles 字段 —— 兜底用顶部那份合成一条,免得升级后卡片上段位整片消失。
    /// </summary>
    public RankRoleVM[] RankRoles
    {
        get
        {
            if (_rank is null) return [];

            var src = _rank.Roles;
            if (src.Count == 0 && _rank.Text.Length > 0)
                src = [new RankRole
                {
                    RoleCn = _rank.Role, Text = _rank.Text,
                    BrushKey = _rank.BrushKey, IconPath = _rank.IconPath,
                }];

            return src.Select(r => new RankRoleVM
            {
                RoleCn = r.RoleCn,
                Text = r.Text,
                Brush = (r.BrushKey is { } k ? Application.Current?.TryFindResource(k) as Brush : null)
                        ?? Application.Current?.TryFindResource("Tx3") as Brush,
                Icon = LoadIcon(r.IconPath),
            }).ToArray();
        }
    }

    public string RankText => _rank?.Text ?? "";
    public Visibility RankVisibility => _rank is null ? Visibility.Collapsed : Visibility.Visible;

    public Brush? RankBrush =>
        (_rank?.BrushKey is { } k ? Application.Current?.TryFindResource(k) as Brush : null)
        ?? Application.Current?.TryFindResource("Tx3") as Brush;

    public ImageSource? RankIcon => LoadIcon(_rank?.IconPath);

    /// <summary>悬停时给出三个定位的全量段位 —— 卡片上只放得下最高的那个。</summary>
    public string RankTip => _rank is null ? "" : (_rank.AllText.Length > 0 ? _rank.AllText : _rank.Text);

    /// <summary>
    /// 搜索用的各个字段(名字 / 备注 / 段位 / 区服 / 账号 id),已转小写。
    /// 【必须分字段,不能拼成一整段】:子序列匹配跨字段能乱拼 —— 拼成一段的话,
    /// 关键词「111」会把名字里的 1 和段位「黄金1」里的 1 凑够三个,匹配出一堆无关的号。
    /// </summary>
    public string[] SearchFields =>
    [
        BattleTag.ToLowerInvariant(),
        _note.ToLowerInvariant(),
        RankText.ToLowerInvariant(),
        (_rank?.AllText ?? "").ToLowerInvariant(),
        RegionText.ToLowerInvariant(),
        AccountIdText,      // 纯数字,只会走精确包含(见 Fuzzy),不会乱命中
    ];

    // 行每次轮询都重建,图标不缓存的话会反复读盘解码。冻结后可跨线程共享。
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;

        ImageSource? made = null;
        try
        {
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 32;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                made = bmp;
            }
        }
        catch { made = null; }

        IconCache[path] = made;
        return made;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly BattleNetPaths _paths;
    private readonly AccountReader _reader;
    private readonly AppDataStore _profiles;
    private readonly BattleNetController _controller;
    private readonly AppSettings _settings;
    private readonly LicenseService _license;
    private readonly RankStore _ranks;
    private readonly TokenStore _tokens = new();
    private readonly GameStateStore _gameState = new();

    /// <summary>全部账号(主表)。</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = new();

    /// <summary>可秒切(已存快照、非当前号)。</summary>
    public ObservableCollection<AccountRow> ReadyAccounts { get; } = new();

    /// <summary>需先保存快照(没有快照)。</summary>
    public ObservableCollection<AccountRow> UnsavedAccounts { get; } = new();

    private AccountRow? _current;
    /// <summary>当前登录账号(左侧身份卡)。</summary>
    public AccountRow? CurrentAccount { get => _current; set { Set(ref _current, value); Raise(nameof(HasCurrent)); Raise(nameof(HasCurrentVisibility)); Raise(nameof(NoCurrentVisibility)); } }

    public bool HasCurrent => _current != null;
    public Visibility HasCurrentVisibility => _current != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoCurrentVisibility => _current == null ? Visibility.Visible : Visibility.Collapsed;

    private string _readyCountText = "";
    public string ReadyCountText { get => _readyCountText; set => Set(ref _readyCountText, value); }

    private string _unsavedCountText = "";
    public string UnsavedCountText { get => _unsavedCountText; set => Set(ref _unsavedCountText, value); }
    public Visibility UnsavedVisibility => UnsavedAccounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _totalCountText = "";
    public string TotalCountText { get => _totalCountText; set => Set(ref _totalCountText, value); }

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _rankStatusText = "";
    /// <summary>
    /// 「刷新段位」的进度/结果,显示在列表表头右侧。
    /// 不复用 <see cref="StatusText"/> 是因为它在主界面上根本没有落点(底栏只放了计数和广告位),
    /// 而刷段位要跑十几秒还会灰掉左边的按钮,不给反馈用户只会以为卡死了。
    /// </summary>
    public string RankStatusText
    {
        get => _rankStatusText;
        set { Set(ref _rankStatusText, value); Raise(nameof(RankStatusVisibility)); }
    }

    public Visibility RankStatusVisibility => _rankStatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(NotBusy)); Raise(nameof(BusyVisibility)); } }
    public bool NotBusy => !_busy;

    /// <summary>
    /// 忙碌遮罩。跨区服切号要等战网后台进程退出(十几秒),没有反馈的话看着就像卡死了;
    /// 遮罩里直接显示 <see cref="StatusText"/>,当前在做哪一步一目了然。
    /// </summary>
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;

    private bool _clientRunning;
    /// <summary>战网客户端(不含 Agent)是否在跑。轮询刷新,身份卡上那个按钮的文案跟着它变。</summary>
    public bool ClientRunning
    {
        get => _clientRunning;
        private set { Set(ref _clientRunning, value); Raise(nameof(LaunchText)); }
    }

    public string LaunchText => _clientRunning ? "打开战网窗口" : "启动战网";

    public string AppVersion => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    // ---- 广告位(底部轮播横幅 + 开屏)----
    public RotatingAdVM BottomBanner { get; }
    public AdSlot SplashAd => _settings.SplashAd;

    private bool _adFree;
    public bool AdFree
    {
        get => _adFree;
        set
        {
            Set(ref _adFree, value);
            Raise(nameof(RemoveAdVisibility));
            BottomBanner.RefreshVisibility();
        }
    }

    /// <summary>状态栏那个「去广告」入口。付过钱的人右下角就该是干净的,不留任何字。</summary>
    public Visibility RemoveAdVisibility => _adFree ? Visibility.Collapsed : Visibility.Visible;

    public void OpenAdUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { StatusText = "这个广告位还没配链接(settings.json)。"; return; }
        OpenUrl(url);
    }

    // ---- 设置 / 去广告 / 激活码 / 开发者 ----
    public AppSettings Settings => _settings;
    public LicenseService License => _license;
    public string ApiBaseUrl => _settings.ApiBaseUrl;
    public string SponsorUrl => _settings.SponsorUrl;
    public string QQGroupUrl => _settings.QQGroupUrl;
    public string GithubUrl => _settings.GithubUrl;

    // ---- 新版提示 ----
    private UpdateInfo? _pendingUpdate;
    /// <summary>检测到但还没装的新版(仅非强制版本会走到这)。标题栏那个小标靠它显示。</summary>
    public UpdateInfo? PendingUpdate
    {
        get => _pendingUpdate;
        set
        {
            Set(ref _pendingUpdate, value);
            Raise(nameof(UpdateBadgeVisibility));
            Raise(nameof(UpdateBadgeText));
        }
    }

    public Visibility UpdateBadgeVisibility => _pendingUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    public string UpdateBadgeText => _pendingUpdate is null ? "" : "新版 " + _pendingUpdate.LatestVersion;

    public void ApplyActivation(string code)
    {
        _settings.LicenseCode = code;
        _settings.AdFreeCached = true;
        _settings.Save();
        AdFree = true;
        StatusText = "已去广告,感谢支持 ❤";
    }

    public async Task InitLicenseAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.LicenseCode)) return;
        AdFree = _settings.AdFreeCached;
        var ok = await _license.VerifyAsync(_settings.ApiBaseUrl, _settings.LicenseCode!);
        if (ok is null) return;
        AdFree = ok.Value;
        _settings.AdFreeCached = ok.Value;
        if (!ok.Value) _settings.LicenseCode = null;
        _settings.Save();
    }

    /// <summary>从服务器拉广告配置覆盖本地(可后台随时换广告);拉不到就沿用本地缓存(离线兜底)。</summary>
    public async Task LoadServerAdsAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiBaseUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var json = await http.GetStringAsync(_settings.ApiBaseUrl.TrimEnd('/') + "/api/ads");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                ApplyServerSlot(root, "splash", _settings.SplashAd);
                ApplyServerRotating(root, "bottom", _settings.BottomAd);
                ApplyServerConfig(root);   // 赞助页/QQ群/GitHub 也从服务器下发
                _settings.Save();   // 缓存到本地,下次离线也有

                Raise(nameof(SplashAd));
                BottomBanner.Refresh();
            }
            catch { /* 离线:沿用本地缓存的广告 */ }
        }
        await PreloadSplashImageAsync();   // 预下载开屏图到本地缓存,弹窗时秒显(不再现下)
    }

    /// <summary>开屏图的本地缓存路径(启动时预下载)。开屏弹窗从这读,瞬间显示。</summary>
    public string? SplashImagePath { get; private set; }

    private async Task PreloadSplashImageAsync()
    {
        SplashImagePath = null;
        var url = _settings.SplashAd?.ImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BnetSwitch", "cache");
            Directory.CreateDirectory(dir);
            var name = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
            var file = Path.Combine(dir, "splash_" + name + ".img");
            if (!File.Exists(file))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(url));
            }
            SplashImagePath = file;
        }
        catch { SplashImagePath = null; }
    }

    private static void ApplyServerSlot(JsonElement root, string key, AdSlot slot)
    {
        if (!root.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return;
        if (o.TryGetProperty("enabled", out var e)) slot.Enabled = e.ValueKind == JsonValueKind.True;
        if (o.TryGetProperty("text", out var t)) slot.Text = t.GetString() ?? "";
        if (o.TryGetProperty("url", out var u)) slot.Url = u.GetString() ?? "";
        if (o.TryGetProperty("imageUrl", out var i)) slot.ImageUrl = i.GetString() ?? "";
    }

    /// <summary>解析后端下发的链接配置 config:{sponsorUrl,qqGroup,githubUrl}。非空才覆盖,空则保留客户端内置默认。</summary>
    private void ApplyServerConfig(JsonElement root)
    {
        if (!root.TryGetProperty("config", out var c) || c.ValueKind != JsonValueKind.Object) return;
        string? Get(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var sponsor = Get("sponsorUrl"); if (!string.IsNullOrWhiteSpace(sponsor)) _settings.SponsorUrl = sponsor!;
        var qq = Get("qqGroup"); if (!string.IsNullOrWhiteSpace(qq)) _settings.QQGroupUrl = qq!;
        var gh = Get("githubUrl"); if (!string.IsNullOrWhiteSpace(gh)) _settings.GithubUrl = gh!;
    }

    /// <summary>解析后端下发的轮播广告:{enabled, intervalSec, items:[{text,url,imageUrl}]}。</summary>
    private static void ApplyServerRotating(JsonElement root, string key, RotatingAd ad)
    {
        if (!root.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return;
        if (o.TryGetProperty("enabled", out var e)) ad.Enabled = e.ValueKind == JsonValueKind.True;
        if (o.TryGetProperty("intervalSec", out var iv) && iv.TryGetInt32(out var sec) && sec > 0) ad.IntervalSec = sec;
        if (o.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var list = new List<AdItem>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                list.Add(new AdItem
                {
                    Text = it.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    Url = it.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    ImageUrl = it.TryGetProperty("imageUrl", out var im) ? im.GetString() ?? "" : "",
                });
            }
            ad.Items = list;
        }
    }

    private void OpenUrl(string url) => LinkOpener.Open(url);

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _paths = new BattleNetPaths();
        if (!string.IsNullOrEmpty(_settings.ClientExe) && File.Exists(_settings.ClientExe))
            _paths.ClientExe = _settings.ClientExe;

        _reader = new AccountReader(_paths);
        _profiles = new AppDataStore(_paths);
        _controller = new BattleNetController(_paths);
        _license = new LicenseService();
        _ranks = RankStore.Load();   // 纯读本地文件,不联网

        // 先认本地缓存的授权状态。InitLicenseAsync 排在更新检查和账号刷新后面,
        // 等它跑完状态栏已经晃过一次「去广告」了 —— 付了钱的人不该看到这一下。
        _adFree = _settings.AdFreeCached;

        BottomBanner = new RotatingAdVM(_settings.BottomAd, () => _adFree);

        // 埋点初始化(设备ID/后端地址/版本);启动活跃上报在 LoadServerAdsAsync 里发一次。
        Analytics.Init(_settings.ApiBaseUrl, _license.MachineId, AppVersion);
    }

    // ---- 搜索(名字 / 备注 / 段位,模糊)----

    private string _searchText = "";
    private string[] _searchTerms = Array.Empty<string>();

    /// <summary>顶部搜索框。纯内存过滤 —— 只重排现有行,不重读数据库、不联网。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == (value ?? "")) return;
            _searchText = value ?? "";
            _searchTerms = _searchText
                .Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLowerInvariant())
                .ToArray();
            Raise();
            Raise(nameof(SearchActive));
            Raise(nameof(ClearSearchVisibility));
            RebuildGroups();
        }
    }

    public bool SearchActive => _searchTerms.Length > 0;
    public Visibility ClearSearchVisibility => SearchActive ? Visibility.Visible : Visibility.Collapsed;

    private bool Matches(AccountRow a)
    {
        if (_searchTerms.Length == 0) return true;
        var fields = a.SearchFields;
        // 多个关键词是「与」:输入「大号 钻石」时备注和段位要同时命中。
        // 但单个关键词只需要在【某一个字段内】命中 —— 不允许跨字段拼凑。
        foreach (var t in _searchTerms)
        {
            var hit = false;
            foreach (var f in fields)
                if (Fuzzy(f, t)) { hit = true; break; }
            if (!hit) return false;
        }
        return true;
    }

    /// <summary>
    /// 先直接包含(最常见也最快),不中再按子序列匹配(「zs」命中「Zhang San」、「钻1」命中「钻石1」)。
    /// 【纯数字的关键词不做子序列】:输数字的人要找的就是那串数字,
    /// 放开子序列的话「111」会命中任何散着三个 1 的文本,全是噪音。
    /// </summary>
    private static bool Fuzzy(string field, string term)
    {
        if (term.Length == 0) return true;
        if (field.Contains(term, StringComparison.Ordinal)) return true;
        if (IsAllDigits(term)) return false;

        int i = 0;
        foreach (var c in field)
        {
            if (c == term[i] && ++i == term.Length) return true;
        }
        return false;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>按「当前号 / 可秒切 / 需保存」重建三个分组 + 计数。搜索过滤与置顶排序也都收口在这。</summary>
    private void RebuildGroups()
    {
        CurrentAccount = Accounts.FirstOrDefault(a => a.IsActive);

        // 当前登录的号若在隐藏名单里,自动取消隐藏(用户显然又在用它)。
        if (CurrentAccount != null && _settings.HiddenAccountIds.Remove(CurrentAccount.AccountId))
            _settings.Save();

        var hidden = new HashSet<long>(_settings.HiddenAccountIds);

        var readyAll = Accounts.Where(a => a.HasProfile && !a.IsActive && !hidden.Contains(a.AccountId)).ToList();
        var unsavedAll = Accounts.Where(a => !a.HasProfile && !hidden.Contains(a.AccountId)).ToList();

        ReadyAccounts.Clear();
        foreach (var a in readyAll.Where(Matches)
                                  .OrderByDescending(a => a.IsPinned)
                                  .ThenByDescending(a => a.SavedAtUtc ?? DateTime.MinValue))
            ReadyAccounts.Add(a);

        UnsavedAccounts.Clear();
        foreach (var a in unsavedAll.Where(Matches)
                                    .OrderByDescending(a => a.IsPinned)
                                    .ThenBy(a => a.BattleTag, StringComparer.CurrentCulture))
            UnsavedAccounts.Add(a);

        var total = Accounts.Count(a => !hidden.Contains(a.AccountId));
        var saved = Accounts.Count(a => a.HasProfile && !hidden.Contains(a.AccountId));

        // 过滤态下要把「筛出几个 / 共几个」都写出来,不然用户会以为号丢了
        ReadyCountText = SearchActive
            ? $"{ReadyAccounts.Count} / {readyAll.Count} 个已存快照"
            : $"{ReadyAccounts.Count} 个已存快照";
        UnsavedCountText = SearchActive
            ? $"需先保存快照 · {UnsavedAccounts.Count} / {unsavedAll.Count}"
            : $"需先保存快照 · {UnsavedAccounts.Count}";
        TotalCountText = $"共 {total} 个 · 已保存 {saved} 个";
        Raise(nameof(UnsavedVisibility));
    }

    /// <summary>把某个"需先保存"的检测号从本工具列表隐藏(加入忽略名单)。
    /// 不动战网、不登出、不删任何东西;以后再次登录该号会自动重新出现。</summary>
    public void HideAccount(AccountRow row)
    {
        if (row.IsActive) return;   // 当前登录号不隐藏
        if (!_settings.HiddenAccountIds.Contains(row.AccountId))
            _settings.HiddenAccountIds.Add(row.AccountId);
        _settings.Save();
        RebuildGroups();
        StatusText = $"已从列表移除「{row.BattleTag}」。它仍在战网里,重新登录该号会再次出现。";
    }

    // ---- 备注 / 置顶 ----

    /// <summary>写备注。传空串 = 删掉备注。存 settings.json,和快照无关,没存快照的号也能写。</summary>
    public void SetNote(AccountRow row, string? note)
    {
        var key = row.AccountId.ToString();
        var text = (note ?? "").Trim();
        if (text.Length == 0) _settings.AccountNotes.Remove(key);
        else _settings.AccountNotes[key] = text;
        _settings.Save();

        row.Note = text;
        RebuildGroups();
        StatusText = text.Length == 0 ? $"已清除「{row.BattleTag}」的备注" : $"已保存备注:{text}";
    }

    /// <summary>置顶 / 取消置顶。只影响本工具里的排序。</summary>
    public void TogglePin(AccountRow row)
    {
        if (!_settings.PinnedAccountIds.Remove(row.AccountId))
            _settings.PinnedAccountIds.Add(row.AccountId);
        _settings.Save();

        row.IsPinned = _settings.PinnedAccountIds.Contains(row.AccountId);
        RebuildGroups();
        StatusText = row.IsPinned ? $"已置顶「{row.BattleTag}」" : $"已取消置顶「{row.BattleTag}」";
    }

    // ---- 段位 ----

    /// <summary>拿网易大神会话(国服查段位要);由界面层注入,VM 不自己 new Window。</summary>
    public Func<Task<DashenClient?>>? CnAuthProvider { get; set; }

    /// <summary>
    /// 点「刷新段位」才会走到这:按区服分别查一遍,结果写进 ranks.json。
    /// 启动、轮询、切号都不碰这里 —— 段位一律走本地缓存。
    /// </summary>
    public async Task RefreshRanksAsync()
    {
        if (Busy) return;
        var hidden = new HashSet<long>(_settings.HiddenAccountIds);
        var targets = Accounts
            .Where(a => !hidden.Contains(a.AccountId) && a.BattleTag.Contains('#'))
            .Select(a => new RankFetcher.Target(a.AccountId, a.BattleTag, a.IsCnRegion))
            .ToList();

        if (targets.Count == 0) { StatusText = RankStatusText = "没有可查段位的账号。"; AutoClearRankStatus(); return; }

        Busy = true;
        try
        {
            // 进度回调是从 RankFetcher 内部的 await 之后打出来的,那儿不一定还在 UI 线程,
            // 而 RankStatusText 已经绑到界面上了 —— 必须自己切回 Dispatcher 再赋值。
            var disp = Application.Current?.Dispatcher;
            void Log(string msg)
            {
                if (disp is null || disp.CheckAccess()) { StatusText = msg; RankStatusText = msg; }
                else disp.Invoke(() => { StatusText = msg; RankStatusText = msg; });
            }

            var fetcher = new RankFetcher { CnAuthProvider = CnAuthProvider };
            var r = await fetcher.RefreshAsync(targets, _ranks, Log);

            foreach (var a in Accounts) a.Rank = _ranks.Get(a.AccountId);
            RebuildGroups();

            var parts = new List<string> { $"已更新 {r.Updated} 个号的段位" };
            if (r.NoRank > 0) parts.Add($"{r.NoRank} 个未定级");
            if (r.CnSkipped > 0) parts.Add($"{r.CnSkipped} 个国服号未授权跳过");
            if (r.Failed > 0) parts.Add($"{r.Failed} 个查询失败");
            StatusText = RankStatusText = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            StatusText = RankStatusText = "刷新段位失败:" + ex.Message;
        }
        finally { Busy = false; AutoClearRankStatus(); }
    }

    private int _rankStatusSeq;

    /// <summary>结果提示看几秒就够了,别让它一直挂在表头上。后来的提示会作废前一个的定时清除。</summary>
    private async void AutoClearRankStatus()
    {
        var seq = ++_rankStatusSeq;
        await Task.Delay(8000);
        if (seq == _rankStatusSeq && !Busy) RankStatusText = "";
    }

    // ---- 自动检测「战网里登了新号 / 换了号」(避免必须手点刷新或重启本工具)----
    private string _dbStamp = "";
    private long? _lastActiveId;
    private string _lastIdSet = "";
    private bool _polling;

    /// <summary>切换后留多久等战网真正登进去。冷启动 + 登录握手,20 秒偏紧,给足。</summary>
    private const int SwitchVerifySeconds = 45;

    private long? _pendingSwitchId;      // 刚切过去、还没确认登录结果的号
    private DateTime _pendingSwitchUntil;

    /// <summary>从磁盘读一次账号列表 + 当前号指针。</summary>
    private Task<(IReadOnlyList<BattleAccount> list, long? active)> ReadAllAsync() =>
        Task.Run(() =>
        {
            var l = _reader.ReadAccounts(out var act);
            return (l, act);
        });

    /// <summary>
    /// 把读到的账号列表 + 当前号指针套用到界面。RefreshAsync / 自动轮询 / 保存前 共用,
    /// 保证「刚登录、列表里从没出现过的号」(换区登录最常见)也能立刻成为当前号。
    /// </summary>
    private void ApplyAccounts(IReadOnlyList<BattleAccount> accounts, long? activeId)
    {
        Accounts.Clear();
        // login_cache 的唯一键是 (account_id, environment):同一个号在两个区服登录过就会有两行。
        // 切换是按 account_id 走的,这里按 id 去重,免得列表里出现两张一模一样的卡。
        var seen = new HashSet<long>();

        // 区服(决定「查战绩」走国服网易还是国际服暴雪):同一个号两行时,只要有一行是国服就按国服算——
        // 国服那边数据更全(有对局记录),而且国服号在暴雪侧查不到。
        var envs = accounts.GroupBy(a => a.AccountId).ToDictionary(
            g => g.Key,
            g => g.FirstOrDefault(a => AccountRow.IsCn(a.Environment))?.Environment
                 ?? g.Select(a => a.Environment).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "");

        foreach (var a in accounts)
        {
            if (!seen.Add(a.AccountId)) continue;
            var meta = _profiles.ReadMeta(a.AccountId);
            var row = new AccountRow
            {
                AccountId = a.AccountId,
                Environment = envs.TryGetValue(a.AccountId, out var env) ? env : a.Environment,
                BattleTag = string.IsNullOrWhiteSpace(a.BattleTag) ? a.AccountId.ToString() : a.BattleTag,
                IsActive = activeId.HasValue && a.AccountId == activeId.Value,
                HasProfile = meta != null,
                SavedAtUtc = meta?.SavedAtUtc,
                IsExpired = meta?.Expired == true,
            };
            HydrateRow(row);
            Accounts.Add(row);
        }

        // 指针已经指向新号,但战网还没把这一行写进 login_cache(刚登录/换区时有这个空档)。
        // 补一行占位,保证「保存当前为快照」可用;战网写好后下一次刷新会自动换成真实 BattleTag。
        if (activeId is long id && !seen.Contains(id))
        {
            var meta = _profiles.ReadMeta(id);
            var row = new AccountRow
            {
                AccountId = id,
                BattleTag = string.IsNullOrWhiteSpace(meta?.BattleTag) ? id.ToString() : meta!.BattleTag,
                IsActive = true,
                HasProfile = meta != null,
                SavedAtUtc = meta?.SavedAtUtc,
                IsExpired = meta?.Expired == true,
            };
            HydrateRow(row);
            Accounts.Add(row);
        }

        _lastActiveId = activeId;
        _lastIdSet = string.Join(",", Accounts.Select(r => r.AccountId).OrderBy(x => x));
        RebuildGroups();
    }

    /// <summary>行是每轮询重建一次的一次性对象,备注/置顶/段位得从各自的 store 里重新灌回去。</summary>
    private void HydrateRow(AccountRow row)
    {
        row.Note = _settings.AccountNotes.TryGetValue(row.AccountId.ToString(), out var n) ? n : "";
        row.IsPinned = _settings.PinnedAccountIds.Contains(row.AccountId);
        row.Rank = _ranks.Get(row.AccountId);
    }

    /// <summary>
    /// 核对上一次切换到底登上了谁。战网自己会把真正登录的账号写进 CachedData.db 的指针
    /// (那个库在 %LOCALAPPDATA%,不在快照还原的 %APPDATA% 里,所以读到的是战网的结果、
    /// 不是我们刚写进去的愿望)。到点还没登上目标号 = 令牌失效,把这个号标成过期。
    /// </summary>
    private async Task VerifySwitchAsync(long targetId)
    {
        var active = await Task.Run(() => _reader.ReadActiveAccountId());

        if (active == targetId)
        {
            _pendingSwitchId = null;
            var row = Accounts.FirstOrDefault(a => a.AccountId == targetId);
            if (row is { IsExpired: true })
            {
                await Task.Run(() => _profiles.SetExpired(targetId, false));
                row.IsExpired = false;
                RebuildGroups();
            }
            return;
        }

        if (DateTime.UtcNow < _pendingSwitchUntil) return;   // 还在等,战网可能只是起得慢
        _pendingSwitchId = null;

        // 战网压根没起来就别下结论(用户可能自己把它关了),下次再说
        if (!ClientRunning) return;

        var target = Accounts.FirstOrDefault(a => a.AccountId == targetId);
        await Task.Run(() => _profiles.SetExpired(targetId, true));
        if (target is not null)
        {
            target.IsExpired = true;
            RebuildGroups();
            StatusText = $"「{target.BattleTag}」的登录已过期,免密令牌失效了。点它的『重新登录』,登进去后再存一次快照。";
        }
    }

    /// <summary>记下 CachedData.db 当前的文件戳,避免轮询白读一遍。</summary>
    private void StampDb()
    {
        try
        {
            var fi = new FileInfo(_paths.CachedDataDb);
            _dbStamp = fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "";
        }
        catch { _dbStamp = ""; }
    }

    /// <summary>
    /// 定时轮询(界面层每 2 秒调一次):战网里登了新号或换了号就自动并进列表。
    /// 先比文件戳(便宜),变了才真读库;再比「账号集合 + 当前号」,内容真变了才重建 UI。
    /// </summary>
    public async Task PollAccountsAsync()
    {
        // 按钮文案得跟着战网开没开走,不能被下面那一串提前 return 挡掉
        ClientRunning = await Task.Run(() => _controller.IsClientRunning());

        // 切换结果的核对也得放在这里:切成功时 activeId 正好等于轮询基线,下面那句
        // 「没变就 return」会把它整个跳过,核对永远等不到结果、最后误判成过期。
        if (_pendingSwitchId is long pending && !Busy)
            await VerifySwitchAsync(pending);

        if (_polling || Busy || !_paths.Exists) return;
        _polling = true;
        try
        {
            string stamp;
            try
            {
                var fi = new FileInfo(_paths.CachedDataDb);
                if (!fi.Exists) return;
                stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
            }
            catch { return; }

            if (stamp == _dbStamp) return;
            _dbStamp = stamp;

            var (list, activeId) = await ReadAllAsync();
            // 读的过程中用户开始了保存/切换:交给它去做,同时把文件戳清掉,下一拍重读一次别漏掉变化。
            if (Busy) { _dbStamp = ""; return; }

            // 战网写库很频繁(目录缓存、统计等),只有账号集合或当前号真变了才动界面。
            var idSet = string.Join(",", list.Select(a => a.AccountId)
                                             .Concat(activeId.HasValue ? new[] { activeId.Value } : Array.Empty<long>())
                                             .Distinct().OrderBy(x => x));
            if (activeId == _lastActiveId && idSet == _lastIdSet) return;

            var knownBefore = new HashSet<long>(Accounts.Select(r => r.AccountId));
            ApplyAccounts(list, activeId);

            // 过期的号刚被重新登进来:快照还是旧的,不重存下次切过去照样失败,这句得说清楚
            if (CurrentAccount is { IsExpired: true } exp)
                StatusText = $"「{exp.BattleTag}」已重新登录。请点左侧『保存当前为快照』更新它的快照,否则下次切换仍会失败。";
            else if (CurrentAccount is { } cur && !knownBefore.Contains(cur.AccountId))
                StatusText = $"检测到新登录的账号「{cur.BattleTag}」,点左侧『保存当前为快照』把它存下来。";
            else if (CurrentAccount is { } c2)
                StatusText = $"当前登录账号已变为「{c2.BattleTag}」。";
        }
        catch { /* 后台轮询,出错静默,下次再来 */ }
        finally { _polling = false; }
    }

    public async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            StatusText = "读取账号列表…";

            if (!_paths.Exists)
            {
                Accounts.Clear();
                RebuildGroups();
                StatusText = "未找到战网数据目录。请确认战网已安装并至少登录过一次。";
                return;
            }

            StampDb();
            var (accounts, activeId) = await ReadAllAsync();
            ApplyAccounts(accounts, activeId);

            var hidden = new HashSet<long>(_settings.HiddenAccountIds);
            var visibleTotal = Accounts.Count(r => !hidden.Contains(r.AccountId));
            var saved = Accounts.Count(r => r.HasProfile && !hidden.Contains(r.AccountId));
            if (Accounts.Count == 0)
                StatusText = "没读到账号。请先登录一次战网再回来刷新。";
            else if (_paths.ClientExe is null)
                StatusText = "⚠ 未找到 Battle.net.exe,请到设置里指定路径。";
            else if (saved == 0)
                StatusText = "还没保存任何账号。先在战网登录一个号→点『保存当前为快照』。";
            else
                StatusText = $"共 {visibleTotal} 个账号,已保存 {saved} 个。点右侧卡片即可免密秒切。";
        }
        catch (Exception ex)
        {
            StatusText = "读取失败:" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 启动战网 / 把已经在跑的战网唤到前台。
    /// 不碰任何账号文件 —— 当前指针是谁就登谁,所以开了工具能直接上号,不用再自己去找战网图标。
    /// </summary>
    public async Task LaunchClientAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            if (await Task.Run(() => _controller.TryFocusClient()))
            {
                StatusText = "战网已在运行,已唤到前台。";
                return;
            }

            StatusText = "正在启动战网…";
            await Task.Run(() => _controller.LaunchClient());
            ClientRunning = true;
            StatusText = CurrentAccount is { } cur
                ? $"战网启动中,稍等几秒会自动登录「{cur.BattleTag}」。"
                : "战网启动中。";
        }
        catch (Exception ex)
        {
            StatusText = "启动失败:" + ex.Message;
            MessageBox.Show(ex.Message, "启动战网失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>把当前登录账号保存为快照(确认对话框在界面层弹,这里只干活)。</summary>
    public async Task SaveCurrentAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            // 重读整张表(不能只读指针再去内存旧列表里找):刚登录的新号 —— 尤其是换区登录的号 ——
            // 界面上那份列表里根本没有,只查内存会误报「没有检测到当前登录的账号」。
            StampDb();
            var (list, activeId) = await ReadAllAsync();
            ApplyAccounts(list, activeId);

            // 存进哪个号,以 live config(FNV+区服)为准,不信 CachedData 指针 ——
            // 同邮箱 KR/CN 时指针会指错,一点保存就把当前状态存进【另一个区】的号(KR 号的快照被存成 CN 就是这么来的)。
            // 判不出唯一账号(null)才退回指针。
            var trueId = await Task.Run(() => _reader.ResolveCurrentAccountFromConfig()) ?? activeId;
            var active = trueId is null ? null : Accounts.FirstOrDefault(a => a.AccountId == trueId.Value);
            if (active is null)
            {
                MessageBox.Show(
                    "没有检测到当前登录的账号。\n请先在战网里登录一个账号并确认进入,再回来保存。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StatusText = "正在关闭战网以保存账号文件…";
            var stopped = await Task.Run(() => _controller.GracefulQuit());
            if (!stopped)
                throw new InvalidOperationException("战网未能完全退出,已中止保存。请从托盘右键『退出』战网后重试。");

            StatusText = $"正在保存「{active.BattleTag}」的登录快照…";
            await Task.Run(() => _profiles.Save(active.AccountId, active.BattleTag));
            // 顺带把该号在 CachedData.db 的活跃指针(account_id/region)也存下,切换时写回,避免同邮箱 KR/CN 残留旧区域
            _profiles.SavePointer(active.AccountId, await Task.Run(() => _reader.ReadActivePointerJson()));
            // 令牌本体也存一份:同邮箱的两个区服共用一个槽,不存下来就没法切回去(见 TokenStore)
            await Task.Run(() => CaptureTokens(active.AccountId));
            active.HasProfile = true;
            active.SavedAtUtc = DateTime.UtcNow;
            active.IsExpired = false;   // 刚存的快照配的是刚登进去的令牌,过期标记作废
            RebuildGroups();

            StatusText = "正在重新启动战网…";
            await Task.Run(() => _controller.LaunchClient());
            StatusText = $"已保存「{active.BattleTag}」的登录快照,战网正在重启。";
        }
        catch (Exception ex)
        {
            StatusText = "保存失败:" + ex.Message;
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 目标号和刚才那个号是【同一个登录名(邮箱)】时,把目标号存下的令牌写回注册表。
    ///
    /// 背景:同邮箱在两个区服注册的两个账号共用同一个 UnifiedAuth 槽,谁后登录槽里就是谁的令牌。
    /// 只换文件和区域指针救不了 —— 客户端会拿着【错区服的令牌】去敲服务器,被拒后报「无法登录战网」。
    /// 2026-08-14 实测:把旧值写回去就能免密登回去(见 <see cref="TokenStore"/>)。
    ///
    /// 判定条件故意收得很紧(登录名相同才动),不同邮箱的号一律不碰注册表 —— 那条路本来就是好的,不该有任何回归。
    /// 调用时机必须是【优雅退出之后、LaunchClient 之前】。
    /// </summary>
    private void RestoreTokenIfSameLogin(long targetId, long? fromId)
    {
        try
        {
            // 1) 只有同邮箱才可能抢同一个槽。不同邮箱各有各的槽、本来就并存,一律不碰注册表。
            var targetLogin = _profiles.ReadLoginName(targetId);
            if (targetLogin is null || fromId is null) return;
            if (!string.Equals(targetLogin, _loginNameBeforeSwitch, StringComparison.OrdinalIgnoreCase)) return;

            // 2) 只写【这个号自己的那一个槽】。槽名是登录后「哪个槽变了」学出来的,学不出来就不动。
            //    早先用「两个号的快照互相 diff 取有争议的槽」——那是错的:第三个号中途登录过的话,
            //    它的槽在两份快照里也不一样,会被一并写回旧值,把它的有效令牌覆盖成过期的。
            var slot = _profiles.ReadOwnSlot(targetId);
            if (slot is null) return;

            var saved = _profiles.ReadTokens(targetId);
            if (!saved.TryGetValue(slot, out var want)) return;   // 没存到它的令牌(存快照时槽正好是空的)

            var current = _tokens.ReadAll();
            if (current.TryGetValue(slot, out var now) && now.AsSpan().SequenceEqual(want)) return;  // 已经是它

            _tokens.Write(slot, want);
        }
        catch { /* 令牌写回是增强,失败不该让整个切换失败 */ }
    }

    /// <summary>
    /// 存快照时把令牌一起记下,并顺便学出「这个号自己的槽是哪个」:
    /// 和全局上次见到的状态相比,刚好只变了一个槽时,那个槽就是刚登录的这个号的。
    /// 变了多个(中间夹了别的登录)就不学 —— 宁可学不出来,也别记错把别人的槽认成自己的。
    /// </summary>
    private void CaptureTokens(long accountId)
    {
        try
        {
            var current = _tokens.ReadAll();
            if (current.Count == 0) return;

            var changed = TokenStore.ChangedSince(TokenStore.ReadLastSeen(), current);
            if (changed.Count == 1)
                _profiles.SaveOwnSlot(accountId, changed[0]);

            _profiles.SaveTokens(accountId, current);
            TokenStore.WriteLastSeen(current);
        }
        catch { }
    }

    /// <summary>切换开始时(还没还原文件)记下的当前登录名,用来判断目标号是不是同邮箱。</summary>
    private string? _loginNameBeforeSwitch;

    /// <summary>
    /// 跨区服切号时,把守望先锋的游戏文件也换成目标区服那套,省掉每次一百多兆的重新下载。
    ///
    /// 国服用网易 NEAC 反作弊、国际服用暴雪原版,是两套不同的二进制;战网【不校验磁盘文件】,
    /// 只认 Agent 数据库里记的构建,所以 <see cref="GameStateStore"/> 会把两边一起换(见其类注释)。
    ///
    /// 只在【已经存过目标区服快照】时才动手;没存过就什么都不做 —— 让战网照旧下载,不制造新问题。
    /// 调用时机必须是优雅退出之后、启动客户端之前。
    /// </summary>
    private async Task<string?> RestoreGameStateIfCrossRegionAsync(AccountRow target)
    {
        try
        {
            if (_gameState.GameRoot is null) return null;

            // 目标账号该用哪个区服的游戏:环境串第一段就是区服代码(cn/kr/us/eu)
            var want = AccountRow.IsCn(target.Environment) ? "CN"
                     : target.Environment.Split('.').FirstOrDefault()?.ToUpperInvariant();
            if (string.IsNullOrEmpty(want)) return null;

            var now = await Task.Run(() => _gameState.ReadCurrentRegion());
            if (string.Equals(now, want, StringComparison.OrdinalIgnoreCase)) return null;  // 同区服,一个字节都不碰

            // Agent 内存里存着 product.db,活着时读到的可能是半写状态、写进去又会被它覆盖回来。
            // 所以【存和还原都放在它退干净之后】。只有跨区服才等,普通切号不受影响。
            StatusText = "正在等待战网后台进程退出…";
            // 先给它 2 秒自己退(最省事也最干净);超时了才看用户有没有允许强制结束。
            var stopped = await Task.Run(() => BattleNetController.WaitUntilAgentStopped(attempts: 8));
            if (!stopped && _settings.ForceKillAgentOnSwitch)
            {
                StatusText = "正在结束战网后台进程(Agent)…";
                await Task.Run(() => BattleNetController.KillAgent());
                stopped = await Task.Run(() => BattleNetController.WaitUntilAgentStopped(attempts: 12));
            }
            else if (!stopped)
            {
                stopped = await Task.Run(() => BattleNetController.WaitUntilAgentStopped(attempts: 68));
            }
            if (!stopped)
                return "游戏文件未切换:战网后台进程未退出";   // 切号照常进行

            // 先把【要切走的这个区服】自动存一份:此刻客户端刚优雅退出,用户上一秒还在正常用,
            // 这是最可信的时机。状态不自洽(下了一半 / 正在修复)就跳过,宁可用旧快照。
            if (await Task.Run(() => _gameState.IsConsistent(out _)))
            {
                StatusText = $"正在记录{RegionLabel(now ?? "")}的游戏状态…";
                try { await Task.Run(() => _gameState.Capture()); }
                catch { /* 存不上不影响切换,继续 */ }
            }

            if (!_gameState.Has(want))
                // 目标区服还没存过 —— 这次只能让战网自己下载,下完再切回来时就有了
                return $"还没记录过{RegionLabel(want)}的游戏文件,这次仍需战网下载(下次就不用了)";

            StatusText = $"正在切换游戏文件到{RegionLabel(want)}(省去重新下载)…";
            var (restored, _, _) = await Task.Run(() => _gameState.Restore(want));

            // 结果要带回去让调用方拼进最终那句 —— 方法返回后 SwitchToAsync 会立刻把状态改成
            // 「正在启动战网…」,在这里写 StatusText 只能存在几毫秒,用户根本看不见。
            return $"游戏文件已切到{RegionLabel(want)}({restored} 个文件,省去重新下载)";
        }
        catch (Exception ex)
        {
            // 换游戏文件只是省下载的增强,失败了不该让切号本身失败 —— 大不了让战网自己更新一次。
            return "游戏文件未切换(" + ex.Message + "),战网可能需要重新下载";
        }
    }

    private static string RegionLabel(string region) => region.ToUpperInvariant() switch
    {
        "CN" => "国服",
        "KR" => "亚服",
        "US" => "美服",
        "EU" => "欧服",
        _ => region,
    };


    /// <summary>切换到目标账号:关战网 → 更新当前号快照 → 还原目标 → 重启。点卡片直接切,不再二次确认。</summary>
    public async Task SwitchToAsync(AccountRow target)
    {
        if (Busy || !target.HasProfile) return;

        var currentId = await Task.Run(() => _reader.ReadActiveAccountId());
        if (currentId == target.AccountId)
        {
            foreach (var a in Accounts) a.IsActive = a.AccountId == target.AccountId;
            RebuildGroups();
            StatusText = $"「{target.BattleTag}」已经是当前登录账号。";
            return;
        }

        Busy = true;
        try
        {
            // 先记下当前登录名(等会 Restore 会把 live 配置覆盖掉,那时就读不到了)——
            // 用来判断目标号是不是和它同邮箱,只有同邮箱才需要写回令牌。
            _loginNameBeforeSwitch = _profiles.ReadLiveLoginName();

            StatusText = "正在关闭战网…";
            var stopped = await Task.Run(() => _controller.GracefulQuit());
            if (!stopped)
                throw new InvalidOperationException("战网未能完全退出,已中止切换。请从托盘右键『退出』战网后重试。");

            // 存进哪个号的快照,以 live config(FNV+区服)为准,不再信 CachedData 指针 ——
            // 指针在 %LOCALAPPDATA%、还原换不到,脱钩时会把当前状态灌进错误号的快照(切换后掉登录页)。
            // 判定不出唯一账号(null)就跳过这次保存:宁可不更新,也不污染。
            var saveInto = await Task.Run(() => _reader.ResolveCurrentAccountFromConfig());
            if (saveInto is long cur && cur != target.AccountId)
            {
                var curRow = Accounts.FirstOrDefault(a => a.AccountId == cur);
                if (curRow is { HasProfile: true })
                {
                    StatusText = $"正在更新当前号「{curRow.BattleTag}」的快照…";
                    await Task.Run(() => _profiles.Save(cur, curRow.BattleTag));
                    _profiles.SavePointer(cur, await Task.Run(() => _reader.ReadActivePointerJson()));
                    await Task.Run(() => CaptureTokens(cur));
                    curRow.SavedAtUtc = DateTime.UtcNow;
                    curRow.IsExpired = false;   // 它刚才还登着,令牌显然是好的
                }
            }

            StatusText = $"正在还原「{target.BattleTag}」的账号文件…";
            await Task.Run(() => _profiles.Restore(target.AccountId));
            // 关键:把目标号的活跃指针写回 CachedData.db(%LOCALAPPDATA%,Restore 不覆盖这块)——
            // 否则同邮箱 KR/CN 互切时,残留的旧区域指针会和还原的配置打架,导致「无法登录战网」连接错。
            var targetPtr = _profiles.ReadPointer(target.AccountId);
            if (targetPtr is not null)
                await Task.Run(() => _reader.WriteActivePointer(targetPtr));

            // 同邮箱的两个区服账号(一个 CN、一个 KR)【共用一个 UnifiedAuth 令牌槽】,后登的会把先登的覆盖掉。
            // 只有这种情况才需要把令牌写回 —— 不同邮箱各有各的槽,本来就并存,一个字节都不动。
            // 必须卡在【客户端已退出、还没启动】这个空档:让客户端拿着错令牌启动,它会自己把槽删掉。
            await Task.Run(() => RestoreTokenIfSameLogin(target.AccountId, saveInto));

            // 跨区服切换时把游戏文件也换过去,省掉每次一百多兆的重新下载。
            var gameNote = await RestoreGameStateIfCrossRegionAsync(target);

            StatusText = "正在启动战网…";
            await Task.Run(() => _controller.LaunchClient());

            foreach (var a in Accounts)
                a.IsActive = a.AccountId == target.AccountId;
            _lastActiveId = target.AccountId;   // 对齐轮询基线,免得战网写完指针后又报一次"当前账号已变"
            RebuildGroups();

            // 还原的只是「当前是哪个号」的指针,能不能免密登进去取决于注册表里的令牌还在不在。
            // 交给轮询去核对战网真正登上了谁 —— 令牌没了的话这里一切正常,人是在几十秒后才发现的。
            _pendingSwitchId = target.AccountId;
            _pendingSwitchUntil = DateTime.UtcNow.AddSeconds(SwitchVerifySeconds);
            StatusText = $"已切换到「{target.BattleTag}」,战网正在启动,正在确认登录结果…"
                       + (gameNote is null ? "" : "  |  " + gameNote);
        }
        catch (Exception ex)
        {
            StatusText = "切换失败:" + ex.Message;
            MessageBox.Show(ex.Message, "切换失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 过期账号的「重新登录」:先把当前号存好,再回登录页让用户手动登一次那个号。
    /// 过期标记不在这里清 —— 得等用户真的重存了快照才算修好,不然只是把警告藏起来。
    /// </summary>
    public async Task ReloginAsync(AccountRow row)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            StatusText = "正在关闭战网…";
            var stopped = await Task.Run(() => _controller.GracefulQuit());
            if (!stopped)
                throw new InvalidOperationException("战网未能完全退出,请从托盘右键『退出』战网后重试。");

            // 顺手把当前号存一遍:等下要清指针回登录页,现在不存,它的快照就停在上一次保存的状态。
            // 存进哪个号以 live config(FNV+区服)为准,不信 CachedData 指针 —— 脱钩会污染错误号的快照。
            var saveInto = await Task.Run(() => _reader.ResolveCurrentAccountFromConfig());
            if (saveInto is long cur && cur != row.AccountId)
            {
                var curRow = Accounts.FirstOrDefault(a => a.AccountId == cur);
                if (curRow is { HasProfile: true })
                {
                    StatusText = $"正在保存当前号「{curRow.BattleTag}」…";
                    await Task.Run(() => _profiles.Save(cur, curRow.BattleTag));
                    _profiles.SavePointer(cur, await Task.Run(() => _reader.ReadActivePointerJson()));
                    await Task.Run(() => CaptureTokens(cur));
                    curRow.SavedAtUtc = DateTime.UtcNow;
                    curRow.IsExpired = false;
                }
            }

            StatusText = "正在回到登录页(未登出)…";
            await Task.Run(() => _profiles.ClearCurrentPointer());
            await Task.Run(() => _controller.LaunchClient());

            foreach (var a in Accounts) a.IsActive = false;
            _lastActiveId = null;
            _dbStamp = "";
            _pendingSwitchId = null;   // 这不是一次切换,别让核对逻辑把它算成失败
            RebuildGroups();

            StatusText = $"已回到登录页。请在战网里登录「{row.BattleTag}」,登录成功后点左侧『保存当前为快照』更新它的快照。";
        }
        catch (Exception ex)
        {
            StatusText = "出错:" + ex.Message;
            MessageBox.Show(ex.Message, "重新登录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>登录新号:关战网 → 清本地指针回登录页(不登出)→ 启动。确认框在界面层弹。</summary>
    public async Task AddAccountAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            StatusText = "正在关闭战网…";
            var stopped = await Task.Run(() => _controller.GracefulQuit());
            if (!stopped)
                throw new InvalidOperationException("战网未能完全退出,请从托盘右键『退出』战网后重试。");

            StatusText = "正在回到登录页(未登出)…";
            await Task.Run(() => _profiles.ClearCurrentPointer());
            await Task.Run(() => _controller.LaunchClient());

            // 身份卡还挂着上一个号会误导人(它已经不是"当前登录"了),先摘掉;
            // 顺便清掉轮询基线,等用户在战网登完新号,PollAccountsAsync 会自动把它并进列表。
            foreach (var a in Accounts) a.IsActive = false;
            _lastActiveId = null;
            _dbStamp = "";
            RebuildGroups();

            StatusText = "已回到登录页。请在战网里登录新账号(换区也行),登录成功后本工具会自动识别。";
        }
        catch (Exception ex)
        {
            StatusText = "出错:" + ex.Message;
            MessageBox.Show(ex.Message, "登录新号失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>删除某账号在本工具里的快照(确认弹窗在界面层;不影响战网登录)。</summary>
    public async Task DeleteProfileAsync(AccountRow row)
    {
        if (Busy || !row.HasProfile) return;

        Busy = true;
        try
        {
            await Task.Run(() => _profiles.Delete(row.AccountId));
            row.HasProfile = false;
            row.SavedAtUtc = null;
            RebuildGroups();
            StatusText = $"已删除「{row.BattleTag}」的快照。";
        }
        catch (Exception ex)
        {
            StatusText = "删除失败:" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public void SetExePath()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Battle.net.exe",
            Filter = "Battle.net.exe|Battle.net.exe|可执行文件 (*.exe)|*.exe",
            FileName = "Battle.net.exe",
        };
        if (!string.IsNullOrEmpty(_paths.ClientExe))
            dlg.InitialDirectory = Path.GetDirectoryName(_paths.ClientExe);

        if (dlg.ShowDialog() == true)
        {
            _paths.ClientExe = dlg.FileName;
            _settings.ClientExe = dlg.FileName;
            _settings.Save();
            StatusText = "已设置 Battle.net.exe 路径:" + dlg.FileName;
        }
    }
}
