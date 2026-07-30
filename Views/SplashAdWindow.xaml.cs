using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BnetSwitch.Services;

namespace BnetSwitch;

public partial class SplashAdWindow : Window
{
    private readonly AdSlot _slot;

    /// <summary>用户点了「永久去广告」。</summary>
    public bool RemoveAdsRequested { get; private set; }

    /// <param name="localImagePath">启动预下载好的开屏图本地缓存路径(有就秒显,没有回退在线)。</param>
    public SplashAdWindow(AdSlot slot, string? localImagePath = null)
    {
        InitializeComponent();
        _slot = slot;

        AdImage.Visibility = Visibility.Collapsed;
        var img = LoadImage(localImagePath, slot.ImageUrl);
        if (img != null)
        {
            AdImage.Source = img;
            AdImage.Visibility = Visibility.Visible;   // 有图就盖住占位
        }
        else
        {
            AdTextBlock.Text = string.IsNullOrWhiteSpace(slot.Text) ? "广告素材区" : slot.Text;
        }
    }

    /// <summary>优先本地缓存(OnLoad 同步读取,瞬间);没有缓存才回退在线 URL(异步下载)。</summary>
    private static BitmapImage? LoadImage(string? localPath, string? url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(localPath);
                bmp.EndInit();
                return bmp;
            }
            if (!string.IsNullOrWhiteSpace(url))
                return new BitmapImage(new Uri(url));   // 回退:在线加载
        }
        catch { /* 加载失败就走文字占位 */ }
        return null;
    }

    private void OnAdClick(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_slot.Url)) return;
        BnetSwitch.Services.Analytics.Click("splash");
        BnetSwitch.Services.LinkOpener.Open(_slot.Url);
    }

    private void OnRemoveAds(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RemoveAdsRequested = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    // e.Handled 拦住冒泡:否则点「关闭」会继续冒泡到外层 Grid 的 OnAdClick 而误跳 URL。关闭只关闭,点素材区才跳转。
    private void OnClose(object sender, MouseButtonEventArgs e) { e.Handled = true; Close(); }
}
