using System.Windows;
using System.Windows.Input;
using BnetSwitch.Services;
using BnetSwitch.Services.Overwatch;
using BnetSwitch.ViewModels;

namespace BnetSwitch;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _loading;
    private UpdateInfo? _pending;   // 检测到的可更新版本

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        _loading = true;
        SwAutoStart.IsChecked = StartupService.IsEnabled();
        SwCloseToTray.IsChecked = vm.Settings.CloseToTray;
        SwKillAgent.IsChecked = vm.Settings.ForceKillAgentOnSwitch;
        SwDark.IsChecked = vm.Settings.DarkMode;
        PathBox.Text = string.IsNullOrWhiteSpace(vm.Settings.ClientExe) ? "(自动探测)" : vm.Settings.ClientExe;
        VerText.Text = $"当前版本 {vm.AppVersion}";
        _loading = false;
        RefreshCacheInfo();
    }

    // ===== 战绩缓存 =====
    private void RefreshCacheInfo()
    {
        CacheSize.Text = $"已用 {FormatSize(OwImageCache.CacheSizeBytes())}(英雄/地图/头像/技能图标 + 配置)";
        CacheDir.Text = OwImageCache.CacheRoot;
    }

    private static string FormatSize(long b)
        => b >= 1 << 20 ? $"{b / 1024.0 / 1024:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        long freed = OwImageCache.ClearCache();
        CacheSize.Text = $"已清除 {FormatSize(freed)},下次查战绩自动重新下载";
        CacheDir.Text = OwImageCache.CacheRoot;
    }

    private void OnOpenCacheDir(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(OwImageCache.CacheRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = OwImageCache.CacheRoot, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnAutoStart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.SetEnabled(SwAutoStart.IsChecked == true);
    }

    private void OnCloseToTray(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.CloseToTray = SwCloseToTray.IsChecked == true;
        _vm.Settings.CloseChoiceMade = true;   // 手动设过就不再弹首次二选一
        _vm.Settings.Save();
    }

    private void OnKillAgent(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.ForceKillAgentOnSwitch = SwKillAgent.IsChecked == true;
        _vm.Settings.Save();
    }

    private void OnDark(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var dark = SwDark.IsChecked == true;
        _vm.Settings.DarkMode = dark;
        _vm.Settings.Save();
        ThemeManager.Apply(dark);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        // 已是「立即更新」态:下载 → 校验 → 装 → 退出重启
        if (_pending is { HasUpdate: true })
        {
            await DoUpdateAsync(_pending);
            return;
        }

        BtnUpd.IsEnabled = false;
        VerText.Text = "正在检查新版本…";
        var info = await UpdateService.CheckAsync(_vm.Settings.UpdateUrl, _vm.AppVersion);
        BtnUpd.IsEnabled = true;

        if (info is null)
        {
            VerText.Text = string.IsNullOrWhiteSpace(_vm.Settings.UpdateUrl)
                ? $"当前版本 {_vm.AppVersion}(未配置更新源)"
                : "检查失败,请稍后再试";
            return;
        }
        if (info.HasUpdate)
        {
            _pending = info;
            VerText.Text = $"发现新版本 {info.LatestVersion},点右侧更新";   // 详细更新日志放到更新弹窗里,这行别塞
            BtnUpd.Content = "立即更新";
            BtnUpd.Style = (Style)FindResource("AccentButton");
            BtnUpd.Height = 30;
            BtnUpd.Padding = new Thickness(12, 0, 12, 0);
        }
        else
        {
            VerText.Text = $"已是最新版本 {_vm.AppVersion}";
        }
    }

    /// <summary>依次尝试主/备下载源:下载 → 校验 sha256 → 运行升级 → 退出让安装包替换。某个源失败就自动换下一个。</summary>
    private async Task DoUpdateAsync(UpdateInfo info)
    {
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Url)) sources.Add(info.Url);
        if (!string.IsNullOrWhiteSpace(info.UrlBackup)) sources.Add(info.UrlBackup);
        if (sources.Count == 0) { VerText.Text = "更新源没配下载地址"; return; }

        BtnUpd.IsEnabled = false;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BAM_update_setup.exe");
        string lastErr = "";

        for (int i = 0; i < sources.Count; i++)
        {
            var label = sources.Count > 1 ? $"(源 {i + 1}/{sources.Count})" : "";
            VerText.Text = $"正在下载新版{label}…";
            var err = await UpdateService.DownloadAsync(sources[i], tmp,
                p => Dispatcher.Invoke(() => VerText.Text = $"正在下载新版{label}… {p:P0}"));
            if (err != null) { lastErr = err; continue; }   // 下载失败 → 换下一个源

            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                VerText.Text = "正在校验文件…";
                var actual = await Task.Run(() => UpdateService.Sha256Of(tmp));
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    lastErr = "校验不一致";
                    try { System.IO.File.Delete(tmp); } catch { /* ignore */ }
                    continue;   // 校验不过 → 换下一个源
                }
            }

            // 成功
            VerText.Text = "下载完成,正在启动安装…";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = tmp, UseShellExecute = true });
                (Owner as MainWindow)?.ForceExitForUpdate();   // 退出让安装包替换文件并重启
            }
            catch (Exception ex)
            {
                VerText.Text = "启动安装失败:" + ex.Message;
                BtnUpd.IsEnabled = true;
            }
            return;
        }

        VerText.Text = (sources.Count > 1 ? "主备源都下载失败:" : "下载失败:") + lastErr;
        BtnUpd.IsEnabled = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        _vm.SetExePath();
        PathBox.Text = string.IsNullOrWhiteSpace(_vm.Settings.ClientExe) ? "(自动探测)" : _vm.Settings.ClientExe;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
