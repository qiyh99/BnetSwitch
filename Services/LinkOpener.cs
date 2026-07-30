using System.Diagnostics;
using System.IO;

namespace BnetSwitch.Services;

/// <summary>统一打开外部链接:标准 ShellExecute,失败退回 explorer。记一行日志便于排错。</summary>
public static class LinkOpener
{
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { Log("skip: empty url"); return; }
        if (Try(new ProcessStartInfo { FileName = url, UseShellExecute = true }, "shell", url)) return;
        if (Try(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true }, "explorer", url)) return;
        Log("FAILED: " + url);
    }

    private static bool Try(ProcessStartInfo psi, string how, string url)
    {
        try { Process.Start(psi); Log($"OK via {how}: {url}"); return true; }
        catch (Exception ex) { Log($"THREW via {how}: {ex.Message}"); return false; }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BnetSwitch");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "open.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { /* 记不了也不影响打开 */ }
    }
}
