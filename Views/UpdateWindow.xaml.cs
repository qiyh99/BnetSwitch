using System.Windows;
using BnetSwitch.Services;

namespace BnetSwitch;

/// <summary>
/// 新版本提示窗。两种形态由后端的 mandatory 决定:
/// 强制 —— 挡在主界面前,只能「立即更新」或「退出程序」;
/// 非强制 —— 「稍后再说」直接关掉进主界面,同一个版本不会再弹第二次。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VerText.Text = $"新版本 {info.LatestVersion} 已发布";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "修复与优化。" : info.Notes;

        ExitBtn.Content = info.Mandatory ? "退出程序" : "稍后再说";
        HintText.Text = info.Mandatory
            ? "本次为必要更新,完成更新后方可继续使用。"
            : "可稍后更新。标题栏「新版」入口随时可再次打开此窗口。";
    }

    /// <summary>主备源依次下载 → 校验 sha256 → 运行安装包 → 退出让其替换重启。</summary>
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(_info.Url)) sources.Add(_info.Url);
        if (!string.IsNullOrWhiteSpace(_info.UrlBackup)) sources.Add(_info.UrlBackup);
        if (sources.Count == 0) { StatusText.Text = "更新源没配下载地址,请联系开发者。"; return; }

        UpdBtn.IsEnabled = false;
        ExitBtn.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BAM_update_setup.exe");
        string lastErr = "";

        for (int i = 0; i < sources.Count; i++)
        {
            var label = sources.Count > 1 ? $"(源 {i + 1}/{sources.Count})" : "";
            StatusText.Text = $"正在下载新版{label}…";
            var err = await UpdateService.DownloadAsync(sources[i], tmp,
                p => Dispatcher.Invoke(() => { Bar.Value = p * 100; StatusText.Text = $"正在下载新版{label}… {p:P0}"; }));
            if (err != null) { lastErr = err; continue; }   // 下载失败 → 换下一个源

            if (!string.IsNullOrWhiteSpace(_info.Sha256))
            {
                StatusText.Text = "正在校验文件…";
                var actual = await Task.Run(() => UpdateService.Sha256Of(tmp));
                if (!string.Equals(actual, _info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    lastErr = "校验不一致";
                    try { System.IO.File.Delete(tmp); } catch { /* ignore */ }
                    continue;   // 校验不过 → 换下一个源
                }
            }

            StatusText.Text = "下载完成,正在启动安装…";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = tmp, UseShellExecute = true });
                (Owner as MainWindow)?.ForceExitForUpdate();   // 退出让安装包替换并重启
            }
            catch (Exception ex)
            {
                StatusText.Text = "启动安装失败:" + ex.Message;
                UpdBtn.IsEnabled = true;
                ExitBtn.IsEnabled = true;
                Bar.Visibility = Visibility.Collapsed;
            }
            return;
        }

        StatusText.Text = (sources.Count > 1 ? "主备源都下载失败:" : "下载失败:") + lastErr + " —— 检查网络后重试。";
        UpdBtn.IsEnabled = true;
        ExitBtn.IsEnabled = true;
        Bar.Visibility = Visibility.Collapsed;
    }

    /// <summary>强制版本这个键是「退出程序」;非强制是「稍后再说」,只关窗、放人进主界面。</summary>
    private void OnExit(object sender, RoutedEventArgs e)
    {
        if (_info.Mandatory) { Application.Current.Shutdown(); return; }
        DialogResult = false;
        Close();
    }
}
