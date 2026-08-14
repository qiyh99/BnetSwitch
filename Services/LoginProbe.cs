using System.IO;

namespace BnetSwitch.Services;

/// <summary>
/// 判断「战网这次真的免密登录成功了吗」。
///
/// 【为什么不能用 CachedData 的活跃指针判断】那个指针是切换时【我们自己写进去的】,
/// 读回来必然等于目标号 —— 拿它当"登录成功"的证据等于自证,什么都没验到。
/// 2026-08-14 就栽在这:登录被服务端拒绝、令牌已被客户端删掉,工具却判定成功,
/// 把坏令牌存进快照,下次写回去又被拒又被删,雪崩且不可逆。
///
/// 这里改用两个【客户端自己产生、我们绝不写】的证据:
/// 1. 客户端日志里有没有 "rejected by BGS" / "DeleteToken" —— 有就是失败,一票否决;
/// 2. 该账号的 account.db 有没有被刷新 —— 登录成功时客户端会写它。
/// </summary>
public static class LoginProbe
{
    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net");

    private static string LogsDir => Path.Combine(LocalRoot, "Logs");

    /// <summary>本次启动之后,客户端日志里是否出现了「令牌被拒 / 删令牌」。出现过就判失败。</summary>
    public static bool SawTokenRejected(DateTime sinceUtc)
    {
        try
        {
            var dir = new DirectoryInfo(LogsDir);
            if (!dir.Exists) return false;

            foreach (var f in dir.GetFiles("battle.net-*.log"))
            {
                // 只看这次启动之后写的日志
                if (f.LastWriteTimeUtc < sinceUtc.AddSeconds(-5)) continue;
                string text;
                try
                {
                    // 客户端还开着,日志是占用状态 —— 必须共享读
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    text = sr.ReadToEnd();
                }
                catch { continue; }

                if (text.Contains("rejected by BGS", StringComparison.Ordinal) ||
                    text.Contains("DeleteToken", StringComparison.Ordinal))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>该账号的 account.db 在本次启动之后被刷新过 —— 客户端成功登进这个号才会写它。</summary>
    public static bool AccountTouched(long accountId, DateTime sinceUtc)
    {
        try
        {
            var f = new FileInfo(Path.Combine(LocalRoot, "Account", accountId.ToString(), "account.db"));
            return f.Exists && f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-5);
        }
        catch { return false; }
    }

    /// <summary>
    /// 综合判定:没有被拒/删令牌的迹象,且这个号的 account.db 被刷新过,才算真的登进去了。
    /// 拿不准一律算【没成功】—— 存错令牌的代价(雪崩)远大于少存一次。
    /// </summary>
    public static bool LoginConfirmed(long accountId, DateTime sinceUtc) =>
        !SawTokenRejected(sinceUtc) && AccountTouched(accountId, sinceUtc);
}
