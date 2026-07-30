using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace BnetSwitch.Services;

/// <summary>
/// 去广告授权:激活码绑定到本机设备(一码一机),调后端 /api/activate、/api/verify。
/// 后端地址、赞助链接在 settings.json 配置(ApiBaseUrl / SponsorUrl)。
/// </summary>
public sealed class LicenseService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>本机设备标识(SHA256(MachineGuid) 前缀),用于把激活码绑到这台机器。</summary>
    public string MachineId { get; }

    public LicenseService() => MachineId = ComputeMachineId();

    private static string ComputeMachineId()
    {
        string raw;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            raw = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        }
        catch { raw = Environment.MachineName; }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("BAM|" + raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private sealed record Resp(bool ok, bool adFree, string? error);

    /// <summary>用激活码激活(首次绑定本机)。返回 (是否去广告, 错误信息)。</summary>
    public async Task<(bool adFree, string? error)> ActivateAsync(string apiBase, string code)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
            return (false, "未配置激活服务器(请在 settings.json 设 ApiBaseUrl)。");
        try
        {
            var url = apiBase.TrimEnd('/') + "/api/activate";
            using var resp = await Http.PostAsJsonAsync(url, new { code, machineId = MachineId });
            var data = await resp.Content.ReadFromJsonAsync<Resp>(JsonOpts);
            if (data is null) return (false, "服务器无响应");
            return (data.adFree, data.error);
        }
        catch (Exception ex)
        {
            return (false, "连接激活服务器失败:" + ex.Message);
        }
    }

    /// <summary>启动时校验已保存的激活码是否仍有效(支持后端作废后失效)。网络失败返回 null(交由本地缓存决定)。</summary>
    public async Task<bool?> VerifyAsync(string apiBase, string code)
    {
        if (string.IsNullOrWhiteSpace(apiBase) || string.IsNullOrWhiteSpace(code)) return null;
        try
        {
            var url = apiBase.TrimEnd('/') + "/api/verify";
            using var resp = await Http.PostAsJsonAsync(url, new { code, machineId = MachineId });
            var data = await resp.Content.ReadFromJsonAsync<Resp>(JsonOpts);
            return data?.adFree ?? false;
        }
        catch
        {
            return null;   // 离线:让调用方沿用本地缓存,不误伤已付费用户
        }
    }
}
