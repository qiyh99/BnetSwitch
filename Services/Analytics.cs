using System.Net.Http;
using System.Net.Http.Json;

namespace BnetSwitch.Services;

/// <summary>轻量埋点:启动上报活跃(Ping)、广告点击(Click)。全程 fire-and-forget,失败静默,绝不影响主流程。</summary>
public static class Analytics
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static string _apiBase = "";
    private static string _machineId = "";
    private static string _version = "";

    /// <summary>启动时调用一次(有了后端地址/设备ID/版本)。</summary>
    public static void Init(string? apiBase, string? machineId, string? version)
    {
        _apiBase = (apiBase ?? "").TrimEnd('/');
        _machineId = machineId ?? "";
        _version = version ?? "";
    }

    /// <summary>上报一次活跃(每次启动)。用于日活/周活/月活/留存。</summary>
    public static async void Ping()
    {
        if (_apiBase.Length == 0 || _machineId.Length == 0) return;
        try { await Http.PostAsJsonAsync(_apiBase + "/api/ping", new { machineId = _machineId, version = _version }); }
        catch { /* 埋点失败无所谓 */ }
    }

    /// <summary>上报一次广告点击。slot: "splash" | "bottom"。</summary>
    public static async void Click(string slot)
    {
        if (_apiBase.Length == 0) return;
        try { await Http.PostAsJsonAsync(_apiBase + "/api/click", new { slot }); }
        catch { /* 埋点失败无所谓 */ }
    }
}
