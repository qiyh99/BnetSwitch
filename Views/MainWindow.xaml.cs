using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BnetSwitch.Services;
using BnetSwitch.ViewModels;

namespace BnetSwitch;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExit;

    /// <summary>盯着战网的 CachedData.db:在战网里登了新号/换了号就自动并进列表,不用手点刷新、更不用重开本工具。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _watchTimer =
        new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SetupTray();

        // 记住用户上次调的窗口大小;首次启动默认最小尺寸。
        Width = _vm.Settings.WindowWidth >= MinWidth ? _vm.Settings.WindowWidth : MinWidth;
        Height = _vm.Settings.WindowHeight >= MinHeight ? _vm.Settings.WindowHeight : MinHeight;
        if (_vm.Settings.WindowMaximized) WindowState = WindowState.Maximized;

        Loaded += async (_, _) =>
        {
            if (!await UpdateGateAsync()) return;   // 强制更新门:有新版必须更新完才进主界面
            StartShowListener();
            await _vm.RefreshAsync();
            _watchTimer.Tick += async (_, _) => await _vm.PollAccountsAsync();
            _watchTimer.Start();
            await _vm.InitLicenseAsync();
            await _vm.LoadServerAdsAsync();   // 服务器广告覆盖本地,再决定开屏
            if (ShouldStartHidden())
                HideToTray();
            else
                ShowSplashIfAny();
        };
    }

    /// <summary>开启时检测更新:有新版就弹强制更新窗(必须更新或退出),没有/检查失败则放行。返回 true=放行进主界面。</summary>
    private async Task<bool> UpdateGateAsync()
    {
        DimOverlay.Visibility = Visibility.Visible;   // 检查期间盖住主界面
        UpdateInfo? info = null;
        try { info = await UpdateService.CheckAsync(_vm.Settings.UpdateUrl, _vm.AppVersion); }
        catch { info = null; }

        if (info is not { HasUpdate: true })
        {
            DimOverlay.Visibility = Visibility.Collapsed;
            return true;   // 没更新 / 检查失败(连不上)→ 放行,别把人锁在外面
        }

        new ForcedUpdateWindow(info) { Owner = this }.ShowDialog();   // 更新成功会退出装新版;退出会关掉 app
        Application.Current.Shutdown();   // 强制:有更新就不进主界面(兜底再退一次,防 Alt+F4 绕过)
        return false;
    }

    // ===== 标题栏 =====
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowDimmed(new SettingsWindow(_vm));
    private void OnContact(object sender, RoutedEventArgs e) => ShowDimmed(new ContactWindow(_vm.QQGroupUrl, _vm.GithubUrl));

    // 查战绩:当前登录号(身份卡按钮)/ 指定号(账号卡悬停按钮)。
    private void OnOpenStats(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentAccount is { } row) OpenStatsFor(row);
    }

    private void OnRowStats(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountRow row }) OpenStatsFor(row);
    }

    // 按区服分流:国服走网易大神(要扫码,roleId = account_id_lo);国际服/亚服走暴雪官方生涯页(免登录,吃 BattleTag)。
    private void OpenStatsFor(AccountRow row)
    {
        if (row.IsCnRegion) Stats.StatsWindow.ShowFor(this, row.AccountId);
        else Stats.CareerWindow.ShowFor(this, row.BattleTag);
    }

    /// <summary>弹模态窗:主窗盖一层 dim 遮罩(对齐原型 .ov 的 mask)。</summary>
    private bool? ShowDimmed(Window dlg)
    {
        dlg.Owner = this;
        DimOverlay.Visibility = Visibility.Visible;
        try { return dlg.ShowDialog(); }
        finally { DimOverlay.Visibility = Visibility.Collapsed; }
    }

    // ===== 主操作 =====
    private async void OnRefresh(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private async void OnLaunchClient(object sender, RoutedEventArgs e) => await _vm.LaunchClientAsync();

    private async void OnSaveCurrent(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentAccount is null) return;
        if (ShowDimmed(new SnapshotConfirmWindow(_vm.CurrentAccount.BattleTag)) == true)
            await _vm.SaveCurrentAsync();
    }

    private async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        if (ShowDimmed(new LoginNewWindow()) == true)
            await _vm.AddAccountAsync();
    }

    private async void OnSwitchCard(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountRow row })
            await _vm.SwitchToAsync(row);
    }

    private void OnSaveHint(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("要保存这个账号,需要先在战网里登录它,再点左侧「保存当前为快照」。",
            "还没有快照", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>卡片上的删除按钮:弹二次确认,确认后删快照。</summary>
    private async void OnDeleteCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountRow row })
        {
            if (ShowDimmed(new DeleteConfirmWindow(row.BattleTag)) == true)
                await _vm.DeleteProfileAsync(row);
        }
    }

    /// <summary>"需先保存"卡片上的移除按钮:确认后把该检测号从本工具列表隐藏(不动战网、不登出)。</summary>
    private void OnHideCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountRow row })
        {
            if (ShowDimmed(new DeleteConfirmWindow(row.BattleTag, hideMode: true)) == true)
                _vm.HideAccount(row);
        }
    }

    private void OnBottomBannerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RotatingAdVM b })
        {
            BnetSwitch.Services.Analytics.Click("bottom");
            _vm.OpenAdUrl(b.Url);
        }
    }

    private void OnRemoveAd(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenActivate();
    }

    private void OpenActivate()
    {
        var dlg = new ActivateWindow(_vm.License, _vm.ApiBaseUrl, _vm.SponsorUrl);
        if (ShowDimmed(dlg) == true && dlg.ActivatedCode is not null)
            _vm.ApplyActivation(dlg.ActivatedCode);
    }

    // ===== 开屏广告 =====
    private void ShowSplashIfAny()
    {
        var slot = _vm.SplashAd;
        if (slot is { Enabled: true } && slot.HasContent && !_vm.AdFree)
        {
            var w = new SplashAdWindow(slot, _vm.SplashImagePath);
            ShowDimmed(w);
            if (w.RemoveAdsRequested) OpenActivate();
        }
    }

    // ===== 托盘 / 关闭 / 单实例 =====
    private bool ShouldStartHidden() =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))
        || _vm.Settings.StartMinimized;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 无边框窗口最大化时别盖住任务栏。
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);

        // Win11:让 DWM 给无边框窗口画系统圆角(带原生阴影);Win10 不支持该属性,保持直角,无副作用。
        try
        {
            int pref = 2;   // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { /* dwmapi 不可用就算了 */ }

        if (ShouldStartHidden()) { Hide(); ShowInTaskbar = false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- 最大化限制在工作区(WM_GETMINMAXINFO)----
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024)   // WM_GETMINMAXINFO
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2 /*NEAREST*/);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    var work = mi.rcWork;
                    var area = mi.rcMonitor;
                    mmi.ptMaxPosition.X = work.left - area.left;
                    mmi.ptMaxPosition.Y = work.top - area.top;
                    mmi.ptMaxSize.X = work.right - work.left;
                    mmi.ptMaxSize.Y = work.bottom - work.top;
                    // 最小尺寸跟随 XAML 的 MinWidth/MinHeight,按当前 DPI 换算成物理像素,
                    // 避免高分屏下逻辑/物理像素不一致导致还能拖得比布局下限更小。
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                    mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
                    mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    private void SetupTray()
    {
        var menu = TrayMenuFactory.Create(
            ("显示主界面", (_, _) => ShowFromTray()),
            ("-", null),
            ("退出", (_, _) => ExitApp()));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "战网账号切换",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info?.Stream is { } s) return new System.Drawing.Icon(s);
        }
        catch { /* 兜底 */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void HideToTray() { Hide(); ShowInTaskbar = false; }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    private void ExitApp() { _reallyExit = true; Close(); }

    /// <summary>为自动更新彻底退出:释放托盘、真正关闭(不进托盘),好让安装包替换被占用的文件。</summary>
    public void ForceExitForUpdate()
    {
        _reallyExit = true;
        _watchTimer.Stop();
        DisposeTray();
        Application.Current.Shutdown();
    }

    private void DisposeTray()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    /// <summary>把当前窗口尺寸(最大化时取还原尺寸)写进设置,下次启动还原。</summary>
    private void SaveWindowSize()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        var w = WindowState == WindowState.Normal ? Width : RestoreBounds.Width;
        var h = WindowState == WindowState.Normal ? Height : RestoreBounds.Height;
        if (w >= MinWidth && h >= MinHeight) { s.WindowWidth = w; s.WindowHeight = h; }
        s.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowSize();   // 无论收托盘还是退出,都记住当前大小

        if (_reallyExit) { _watchTimer.Stop(); DisposeTray(); base.OnClosing(e); return; }

        // 首次关闭:弹一次「托盘 / 退出」二选一
        if (!_vm.Settings.CloseChoiceMade)
        {
            e.Cancel = true;
            var dlg = new CloseChoiceWindow();
            if (ShowDimmed(dlg) != true) return;

            _vm.Settings.CloseToTray = dlg.MinimizeToTray;
            if (dlg.RememberChoice) _vm.Settings.CloseChoiceMade = true;
            _vm.Settings.Save();

            if (dlg.MinimizeToTray) { HideToTray(); return; }
            _reallyExit = true;                 // 选了退出
            _watchTimer.Stop();
            DisposeTray();
            Application.Current.Shutdown();
            return;
        }

        if (_vm.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            _tray?.ShowBalloonTip(1500, "仍在后台运行", "已最小化到托盘,双击图标恢复,右键可退出。",
                System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        _watchTimer.Stop();
        DisposeTray();
        base.OnClosing(e);
    }

    private void StartShowListener()
    {
        try
        {
            var handle = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, App.ShowEventName);
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try { handle.WaitOne(); } catch { break; }
                    Dispatcher.Invoke(ShowFromTray);
                }
            })
            { IsBackground = true, Name = "ShowListener" };
            t.Start();
        }
        catch { /* ignore */ }
    }
}
