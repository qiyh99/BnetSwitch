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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private const int SW_RESTORE = 9;

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

    /// <summary>Agent.exe 是否在运行。</summary>
    public static bool IsAgentRunning() => Process.GetProcessesByName("Agent").Any();

    /// <summary>
    /// 等 Agent.exe 也退出(它比客户端慢一拍,自己会退)。
    ///
    /// 只有【换区服游戏状态】才需要等它:Agent 内存里存着
    /// <c>C:\ProgramData\Battle.net\Agent\product.db</c>,它活着的时候写进去会被它覆盖回来
    /// (实测:写入 CN 一分半后被改回 KR)。切账号本身不需要等 —— 别拿这个拖慢普通切换。
    /// 绝不强杀,等不到就让调用方放弃换游戏状态。
    /// </summary>
    public static bool WaitUntilAgentStopped(int attempts = 80, int intervalMs = 250)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (!IsAgentRunning()) return true;
            Thread.Sleep(intervalMs);
        }
        return !IsAgentRunning();
    }

    /// <summary>
    /// 结束战网的 Agent.exe。只在用户显式打开「强制关闭 Agent」时调用。
    ///
    /// 这里【严格按可执行文件路径认人】—— "Agent.exe" 是个烂大街的进程名,
    /// 别的软件也叫这个,认错了就是杀别人的进程。只认路径里带 Battle.net 的那个。
    /// Agent 不持有登录状态,战网启动时会自己重新拉起它。
    /// </summary>
    public static int KillAgent()
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("Agent"))
        {
            try
            {
                var path = p.MainModule?.FileName ?? "";
                if (path.Contains("Battle.net", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    killed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return killed;
    }

    /// <summary>
    /// 战网已经在跑就把它的窗口唤到前台,返回是否唤到了。
    /// 缩在托盘里的时候顶层窗是隐藏的,这里拿不到可见窗 → 返回 false,交给调用方再跑一次 exe
    /// (战网是单实例,第二次启动会把已有窗口自己拉出来)。
    /// </summary>
    public bool TryFocusClient()
    {
        var procs = Process.GetProcessesByName("Battle.net");
        var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));
        foreach (var p in procs) p.Dispose();
        if (pids.Count == 0) return false;

        var hit = IntPtr.Zero;
        EnumWindowsProc collector = (h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            // 只认「可见 + 有标题」的顶层窗:CEF 会开一堆不可见的辅助窗,唤那些没有任何效果
            if (!pids.Contains(pid) || !IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            hit = h;
            return false;
        };
        EnumWindows(collector, IntPtr.Zero);
        GC.KeepAlive(collector);
        if (hit == IntPtr.Zero) return false;

        ShowWindow(hit, SW_RESTORE);
        return SetForegroundWindow(hit);
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
