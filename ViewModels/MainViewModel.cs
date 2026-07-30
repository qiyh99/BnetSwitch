using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using BnetSwitch.Services;
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
}

public sealed class MainViewModel : ObservableObject
{
    private readonly BattleNetPaths _paths;
    private readonly AccountReader _reader;
    private readonly AppDataStore _profiles;
    private readonly BattleNetController _controller;
    private readonly AppSettings _settings;
    private readonly LicenseService _license;

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

    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;

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
            Raise(nameof(AdFreeBadgeVisibility));
            BottomBanner.RefreshVisibility();
        }
    }

    public Visibility AdFreeBadgeVisibility => _adFree ? Visibility.Visible : Visibility.Collapsed;

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
        Analytics.Ping();   // 启动活跃上报(日活/留存)
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

        BottomBanner = new RotatingAdVM(_settings.BottomAd, () => _adFree);

        // 埋点初始化(设备ID/后端地址/版本);启动活跃上报在 LoadServerAdsAsync 里发一次。
        Analytics.Init(_settings.ApiBaseUrl, _license.MachineId, AppVersion);
    }

    /// <summary>按「当前号 / 可秒切 / 需保存」重建三个分组 + 计数。</summary>
    private void RebuildGroups()
    {
        CurrentAccount = Accounts.FirstOrDefault(a => a.IsActive);

        // 当前登录的号若在隐藏名单里,自动取消隐藏(用户显然又在用它)。
        if (CurrentAccount != null && _settings.HiddenAccountIds.Remove(CurrentAccount.AccountId))
            _settings.Save();

        var hidden = new HashSet<long>(_settings.HiddenAccountIds);

        ReadyAccounts.Clear();
        foreach (var a in Accounts.Where(a => a.HasProfile && !a.IsActive && !hidden.Contains(a.AccountId))
                                  .OrderByDescending(a => a.SavedAtUtc ?? DateTime.MinValue))
            ReadyAccounts.Add(a);

        UnsavedAccounts.Clear();
        foreach (var a in Accounts.Where(a => !a.HasProfile && !hidden.Contains(a.AccountId))
                                  .OrderBy(a => a.BattleTag, StringComparer.CurrentCulture))
            UnsavedAccounts.Add(a);

        var total = Accounts.Count(a => !hidden.Contains(a.AccountId));
        var saved = Accounts.Count(a => a.HasProfile && !hidden.Contains(a.AccountId));
        ReadyCountText = $"{ReadyAccounts.Count} 个已存快照";
        UnsavedCountText = $"需先保存快照 · {UnsavedAccounts.Count}";
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

            var (accounts, activeId) = await Task.Run(() =>
            {
                var list = _reader.ReadAccounts(out var act);
                return (list, act);
            });

            Accounts.Clear();
            foreach (var a in accounts)
            {
                var meta = _profiles.ReadMeta(a.AccountId);
                Accounts.Add(new AccountRow
                {
                    AccountId = a.AccountId,
                    BattleTag = string.IsNullOrWhiteSpace(a.BattleTag) ? a.AccountId.ToString() : a.BattleTag,
                    IsActive = activeId.HasValue && a.AccountId == activeId.Value,
                    HasProfile = meta != null,
                    SavedAtUtc = meta?.SavedAtUtc,
                });
            }
            RebuildGroups();

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

    /// <summary>把当前登录账号保存为快照(确认对话框在界面层弹,这里只干活)。</summary>
    public async Task SaveCurrentAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var activeId = await Task.Run(() => _reader.ReadActiveAccountId());
            foreach (var a in Accounts)
                a.IsActive = activeId.HasValue && a.AccountId == activeId.Value;

            var active = activeId is null ? null : Accounts.FirstOrDefault(a => a.AccountId == activeId.Value);
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
            active.HasProfile = true;
            active.SavedAtUtc = DateTime.UtcNow;
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
            StatusText = "正在关闭战网…";
            var stopped = await Task.Run(() => _controller.GracefulQuit());
            if (!stopped)
                throw new InvalidOperationException("战网未能完全退出,已中止切换。请从托盘右键『退出』战网后重试。");

            if (currentId is long cur && cur != target.AccountId)
            {
                var curRow = Accounts.FirstOrDefault(a => a.AccountId == cur);
                if (curRow is { HasProfile: true })
                {
                    StatusText = $"正在更新当前号「{curRow.BattleTag}」的快照…";
                    await Task.Run(() => _profiles.Save(cur, curRow.BattleTag));
                    curRow.SavedAtUtc = DateTime.UtcNow;
                }
            }

            StatusText = $"正在还原「{target.BattleTag}」的账号文件…";
            await Task.Run(() => _profiles.Restore(target.AccountId));

            StatusText = "正在启动战网…";
            await Task.Run(() => _controller.LaunchClient());

            foreach (var a in Accounts)
                a.IsActive = a.AccountId == target.AccountId;
            RebuildGroups();
            StatusText = $"已切换到「{target.BattleTag}」,战网正在启动。若仍是上一个号,请刷新/重开战网。";
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
            StatusText = "已回到登录页。请在战网里登录新账号,登录后回来点『保存当前为快照』。";
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
