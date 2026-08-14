using System.IO;
using System.Text.Json;

namespace BnetSwitch.Services;

/// <summary>一个广告位的配置。</summary>
public sealed class AdSlot
{
    /// <summary>广告文字(没设图片时显示)。</summary>
    public string Text { get; set; } = "";

    /// <summary>点击打开的链接(推广/联盟/打赏)。</summary>
    public string Url { get; set; } = "";

    /// <summary>广告图片 URL(可选,http/https;设了就显示图片,可随时在服务器换图)。</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>是否启用该广告位。</summary>
    public bool Enabled { get; set; }

    public bool HasContent => !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(ImageUrl);
}

/// <summary>轮播广告里的一条(文字或图片 + 跳转链接)。</summary>
public sealed class AdItem
{
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    public bool HasContent => !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(ImageUrl);
}

/// <summary>底部轮播广告位:多条广告每隔几秒轮换一条。</summary>
public sealed class RotatingAd
{
    public bool Enabled { get; set; }

    /// <summary>轮换间隔(秒)。</summary>
    public int IntervalSec { get; set; } = 6;

    /// <summary>广告条目(轮换显示)。</summary>
    public List<AdItem> Items { get; set; } = new();
}

/// <summary>本工具自身的设置,存 %LOCALAPPDATA%\BnetSwitch\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>用户手动指定的 Battle.net.exe 路径(自动探测失败时使用)。</summary>
    public string? ClientExe { get; set; }

    // ---- 界面 / 行为 ----

    /// <summary>点关闭按钮时最小化到托盘(而不是退出)。默认开。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>是否已经问过「关闭时怎么处理」(首次关闭弹一次二选一)。</summary>
    public bool CloseChoiceMade { get; set; } = false;

    /// <summary>启动时直接最小化到托盘(配合开机自启用)。</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// 跨区服切号时,等不及 Agent.exe 自己退出就直接结束它。
    ///
    /// 换游戏文件必须在 Agent 退干净之后做 —— 它内存里存着 product.db,活着时写进去会被它覆盖回来。
    /// 而它比客户端慢一拍才退,实测平均要等 15 秒,这是跨区服切号唯一的耗时来源
    /// (换文件本身是 0.0 秒)。结束它的风险很低:Agent 只管下载/安装,不持有任何登录状态
    /// (令牌在注册表、登录态在 %APPDATA%),战网启动时会自己把它拉起来。
    /// 默认关 —— 动别人的进程这件事,交给用户自己决定。
    /// </summary>
    public bool ForceKillAgentOnSwitch { get; set; } = false;

    /// <summary>深色模式。</summary>
    public bool DarkMode { get; set; } = false;

    /// <summary>上次窗口尺寸(逻辑像素)。0 = 未记录,首次启动用最小尺寸。</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>上次关闭时是否处于最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>用户在列表里手动隐藏的账号 id(战网本地检测到、但不想在本工具里看到的旧号)。
    /// 只影响本工具显示,绝不动战网 / 不登出;该号若重新登录会自动从这里移除并再次出现。</summary>
    public List<long> HiddenAccountIds { get; set; } = new();

    /// <summary>用户给账号写的备注,key = account_id 的字符串形式(JSON 对象键只能是字符串)。
    /// 存这儿而不是快照的 meta.json:没存过快照的号也要能写备注、也要能被搜到。</summary>
    public Dictionary<string, string> AccountNotes { get; set; } = new();

    /// <summary>置顶的账号 id。只影响排序,在各自分组内排到最前。</summary>
    public List<long> PinnedAccountIds { get; set; } = new();

    /// <summary>「联系开发者」弹窗:QQ 交流群链接。</summary>
    public string QQGroupUrl { get; set; } = "https://qm.qq.com/q/3SeTEXIIGI";

    /// <summary>「联系开发者」弹窗:GitHub 开源仓库(GPLv3 源码 + 安装包发行,自 v2.0.3 起合并到这一个仓)。</summary>
    public string GithubUrl { get; set; } = "https://github.com/qiyh99/BnetSwitch";

    /// <summary>版本更新检测接口:返回 {version,notes,url} 的 JSON 地址。留空则「检测更新」只显示当前版本。</summary>
    public string UpdateUrl { get; set; } = "https://api.qiyonghan.icu/api/version";

    /// <summary>
    /// 已经弹过「有新版」提示的版本号。非强制的新版每个只打断用户一次,
    /// 之后就只在标题栏挂个小标 —— 每次启动都弹,和强制更新没差多少。
    /// </summary>
    public string? UpdateNoticeShownFor { get; set; }

    // ---- 广告位(改 settings.json 即可,不用重编译;正式由后端 /api/ads 下发)----

    /// <summary>开屏弹窗广告(启动弹一次,可关)。</summary>
    public AdSlot SplashAd { get; set; } = new()
    { Text = "开屏广告位 —— 配 settings.json 的 SplashAd(Text/Url/ImageUrl)并把 Enabled 设 true", Enabled = false };

    /// <summary>底部轮播横幅广告(多条轮换)。</summary>
    public RotatingAd BottomAd { get; set; } = new()
    {
        Enabled = true,
        IntervalSec = 6,
        Items = { new AdItem { Text = "💡 广告位招租 —— 后端 /api/admin/ads 配置底部轮播" } }
    };

    // ---- 去广告 / 激活码 ----

    /// <summary>激活服务器地址(你的后端)。留空则去广告/广告下发/更新不可用。</summary>
    public string ApiBaseUrl { get; set; } = "https://api.qiyonghan.icu";

    /// <summary>「赞助获取激活码」打开的页面(你的发卡平台/收款码说明页)。</summary>
    public string SponsorUrl { get; set; } = "";

    /// <summary>已激活的激活码(本地缓存,启动时会向后端复核)。</summary>
    public string? LicenseCode { get; set; }

    /// <summary>本地缓存的去广告状态(离线时沿用,避免误伤已付费用户)。</summary>
    public bool AdFreeCached { get; set; }

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BnetSwitch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = (File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null)
                ?? new AppSettings();
        }
        catch { s = new AppSettings(); }
        s.Save();   // 规范化:确保 settings.json 含所有(含新增的)字段,方便你直接编辑
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不了就算了 */ }
    }
}
