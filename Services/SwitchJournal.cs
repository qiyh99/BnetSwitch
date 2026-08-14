using System.IO;
using System.Text.Json;

namespace BnetSwitch.Services;

/// <summary>一次进行中的危险操作的现场记录。</summary>
public sealed class SwitchTxn
{
    /// <summary>switch / relogin / addaccount —— 出问题时告诉用户当时在干什么。</summary>
    public string Op { get; set; } = "";
    public long TargetId { get; set; }
    public string TargetTag { get; set; } = "";
    public DateTime StartedUtc { get; set; }

    /// <summary>动手之前 CachedData.db 里的活跃指针原值,回滚时写回去。</summary>
    public string? PointerBefore { get; set; }
}

/// <summary>
/// 切号事务:动 <c>%APPDATA%\Battle.net</c> 之前先把整份现场拍下来(before-image),
/// 中途出错就原样放回去,程序被杀了下次启动也能收拾干净。
///
/// 【为什么要有这个】2026-08-14 的事故:一个想当然的"增强"在切号中途把状态改坏了,
/// 因为没有回滚,损失变成不可逆的,只能靠人工一个个账号救。
/// 光靠"小心别写错"挡不住这类问题 —— 能回滚才兜得住。
///
/// 只保护 <c>%APPDATA%\Battle.net</c> 和活跃指针:切号真正会改坏的就是这两处。
/// 游戏文件那边本来就是幂等的(每个区服都有完整快照,再还原一次即可),不必进事务。
/// </summary>
public sealed class SwitchJournal
{
    private readonly string _root;
    private string BeforeDir => Path.Combine(_root, "before");
    private string JournalFile => Path.Combine(_root, "journal.json");

    public SwitchJournal()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BnetSwitch", "txn");
    }

    /// <summary>有没有没收尾的事务(上次切到一半崩了 / 被强杀了)。</summary>
    public bool HasPending => File.Exists(JournalFile) && Directory.Exists(BeforeDir);

    public SwitchTxn? Read()
    {
        try
        {
            return File.Exists(JournalFile)
                ? JsonSerializer.Deserialize<SwitchTxn>(File.ReadAllText(JournalFile))
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 记录现场。必须在【优雅退出之后、动任何文件之前】调用 ——
    /// 客户端还活着的时候拍的快照没意义,它退出时还会再写一遍配置。
    /// </summary>
    public void Begin(string op, long targetId, string targetTag, string roamingDir, string? pointerBefore)
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            Directory.CreateDirectory(BeforeDir);

            if (Directory.Exists(roamingDir))
                foreach (var f in Directory.EnumerateFiles(roamingDir))
                    File.Copy(f, Path.Combine(BeforeDir, Path.GetFileName(f)), overwrite: true);

            File.WriteAllText(JournalFile, JsonSerializer.Serialize(new SwitchTxn
            {
                Op = op,
                TargetId = targetId,
                TargetTag = targetTag,
                StartedUtc = DateTime.UtcNow,
                PointerBefore = pointerBefore,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 拍不下来就退化成没有保险,但不该因此拦住切号 */ }
    }

    /// <summary>切换成功,丢掉现场。</summary>
    public void Commit()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }

    /// <summary>
    /// 把现场放回去。会把切换过程中新写进去的文件也清掉 ——
    /// 只覆盖不删除的话,残留的文件仍可能让客户端读到半新半旧的状态。
    /// 返回是否真的回滚了。
    /// </summary>
    public bool Rollback(string roamingDir, Action<string>? writePointer = null)
    {
        try
        {
            if (!HasPending) return false;

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(roamingDir);
            foreach (var f in Directory.EnumerateFiles(BeforeDir))
            {
                var name = Path.GetFileName(f);
                var dst = Path.Combine(roamingDir, name);
                if (File.Exists(dst))
                {
                    var attr = File.GetAttributes(dst);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
                }
                File.Copy(f, dst, overwrite: true);
                kept.Add(name);
            }
            foreach (var f in Directory.EnumerateFiles(roamingDir))
                if (!kept.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch { }

            var ptr = Read()?.PointerBefore;
            if (writePointer is not null && !string.IsNullOrWhiteSpace(ptr))
                writePointer(ptr);

            Commit();
            return true;
        }
        catch { return false; }
    }
}
