using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BnetSwitch.Services;

namespace BnetSwitch;

public partial class ActivateWindow : Window
{
    private readonly LicenseService _license;
    private readonly string _apiBase;
    private readonly string _sponsorUrl;

    /// <summary>激活成功后,已激活的激活码。</summary>
    public string? ActivatedCode { get; private set; }

    public ActivateWindow(LicenseService license, string apiBase, string sponsorUrl)
    {
        InitializeComponent();
        _license = license;
        _apiBase = apiBase;
        _sponsorUrl = sponsorUrl;
        MachineText.Text = $"本机标识 {license.MachineId}";
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCopyMachine(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_license.MachineId); StatusText.Foreground = Brushes.Gray; StatusText.Text = "本机标识已复制。"; }
        catch { /* ignore */ }
    }

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code)) { Warn("请输入激活码"); return; }

        StatusText.Foreground = Brushes.Gray;
        StatusText.Text = "正在激活…";
        IsEnabled = false;
        var (adFree, error) = await _license.ActivateAsync(_apiBase, code);
        IsEnabled = true;

        if (adFree)
        {
            ActivatedCode = code;
            DialogResult = true;
            return;
        }
        Warn(error ?? "激活失败");
    }

    private void Warn(string msg)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x55, 0x6A));
        StatusText.Text = msg;
    }

    private void OnSponsor(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sponsorUrl))
        {
            MessageBox.Show("作者还没配置赞助页(settings.json 的 SponsorUrl)。", "赞助去广告",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _sponsorUrl, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
