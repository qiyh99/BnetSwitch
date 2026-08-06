using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BnetSwitch.Services;

/// <summary>版本检测结果。<paramref name="Mandatory"/>=true 才把人挡在主界面外。</summary>
public sealed record UpdateInfo(bool HasUpdate, string LatestVersion, string Notes, string Url, string UrlBackup, string Sha256, bool Mandatory);

/// <summary>向 UpdateUrl 拉 {version,notes,url,sha256},和当前版本比较;并负责下载新版安装包 + 校验。</summary>
public static class UpdateService
{
    public static async Task<UpdateInfo?> CheckAsync(string apiUrl, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) return null;   // 没配更新源
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latest = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var urlBackup = root.TryGetProperty("urlBackup", out var u2) ? u2.GetString() ?? "" : "";
            var sha = root.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
            // 字段缺失(老服务端)按「不强制」算:漏挡一次远好过把人白白挡在门外
            var mandatory = root.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(latest)) return null;

            return new UpdateInfo(Compare(latest, currentVersion) > 0, latest, notes, url, urlBackup, sha, mandatory);
        }
        catch { return null; }
    }

    /// <summary>下载新版安装包到 destPath;onProgress 回报 0~1 进度。成功返回 null,失败返回错误信息。</summary>
    public static async Task<string?> DownloadAsync(string url, string destPath, Action<double>? onProgress, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);
            var buffer = new byte[81920];
            long read = 0;
            int len;
            while ((len = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, len), ct);
                read += len;
                if (total > 0) onProgress?.Invoke((double)read / total);
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>算文件 SHA256(小写十六进制)。</summary>
    public static string Sha256Of(string file)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>比较 "1.2.0" / "v1.2.0" 形式的版本号,a&gt;b 返回正数。</summary>
    private static int Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x - y;
        }
        return 0;
    }

    private static int[] Parse(string s) =>
        s.TrimStart('v', 'V').Split('.')
         .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
         .ToArray();
}
