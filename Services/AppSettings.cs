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

    /// <summary>「联系开发者」弹窗:QQ 交流群链接。</summary>
    public string QQGroupUrl { get; set; } = "https://qm.qq.com/q/3SeTEXIIGI";

    /// <summary>「联系开发者」弹窗:GitHub 开源仓库(GPLv3 源码,引导 Star;安装包发行仍在 BnetSwitch-releases)。</summary>
    public string GithubUrl { get; set; } = "https://github.com/qiyh99/BnetSwitch";

    /// <summary>版本更新检测接口:返回 {version,notes,url} 的 JSON 地址。留空则「检测更新」只显示当前版本。</summary>
    public string UpdateUrl { get; set; } = "https://api.qiyonghan.icu/api/version";

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
