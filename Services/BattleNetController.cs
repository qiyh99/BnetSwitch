using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BnetSwitch.Services;

/// <summary>
/// 控制战网客户端:优雅退出(不能强杀!)与启动。
///
/// 为什么不能 taskkill /F:强杀会导致下次启动掉登录页。实测:向主窗口发送
/// WM_QUERYENDSESSION + WM_ENDSESSION(模拟系统关机)可让战网走正常关闭流程、
/// 把登录令牌落盘,主程序与全部 CEF 子进程干净退出——这才是切换/保存的正确前置。
///
/// 注意:只等【客户端进程】退出,忽略 Agent.exe(后台更新代理,常驻/自启,
/// 不锁注册表、不影响令牌导入导出)。
/// </summary>
public sealed class BattleNetController
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    // 客户端进程(不含 Agent)
    private static readonly string[] ClientProcs =
    {
        "Battle.net", "Battle.net Launcher", "Battle.net Helper",
    };

    private readonly BattleNetPaths _paths;

    public BattleNetController(BattleNetPaths paths) => _paths = paths;

    /// <summary>客户端(不含 Agent)是否在运行。</summary>
    public bool IsClientRunning() =>
        ClientProcs.SelectMany(Process.GetProcessesByName).Any();

    /// <summary>
    /// 优雅退出战网:向所有带主窗口的 Battle.net 进程发送会话结束消息,然后等待客户端全部退出。
    /// 返回是否已完全退出。绝不强杀。
    /// </summary>
    public bool GracefulQuit()
    {
        // 收集所有战网客户端进程的 PID。
        var procs = Process.GetProcessesByName("Battle.net");
        var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));
        foreach (var p in procs) p.Dispose();

        if (pids.Count > 0)
        {
            // 枚举【所有顶层窗口】(含最小化到托盘的、不可见的),不依赖不稳定的 MainWindowHandle。
            var targets = new List<IntPtr>();
            EnumWindowsProc collector = (h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pids.Contains(pid)) targets.Add(h);
                return true;
            };
            EnumWindows(collector, IntPtr.Zero);
            GC.KeepAlive(collector);

            foreach (var h in targets)
            {
                SendMessageTimeout(h, WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 5000, out _);
                SendMessageTimeout(h, WM_ENDSESSION, (IntPtr)1, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 5000, out _);
            }
        }

        return WaitUntilStopped();
    }

    /// <summary>轮询直到客户端(不含 Agent)完全退出。</summary>
    public bool WaitUntilStopped(int attempts = 60, int intervalMs = 250)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (!IsClientRunning()) return true;
            Thread.Sleep(intervalMs);
        }
        return !IsClientRunning();
    }

    public void LaunchClient()
    {
        var exe = _paths.ClientExe;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new FileNotFoundException("找不到 Battle.net.exe,请点『设置 exe 路径』手动指定。");

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        });
    }
}
