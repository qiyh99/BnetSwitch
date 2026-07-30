using System.Windows;
using System.Windows.Threading;
using BnetSwitch.Services;

namespace BnetSwitch.ViewModels;

/// <summary>底部轮播广告的展示逻辑:多条广告每隔 IntervalSec 秒轮换一条。绑定到界面的横幅。</summary>
public sealed class RotatingAdVM : ObservableObject
{
    private readonly RotatingAd _ad;
    private readonly Func<bool> _adFree;
    private readonly DispatcherTimer _timer;
    private List<AdItem> _items = new();
    private int _index;
    private bool _hidden;

    public RotatingAdVM(RotatingAd ad, Func<bool> adFree)
    {
        _ad = ad;
        _adFree = adFree;
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => Next();
        Rebuild();
    }

    private AdItem? Current => (_items.Count > 0 && _index < _items.Count) ? _items[_index] : null;

    public string Text => Current?.Text ?? "";
    public string Url => Current?.Url ?? "";
    public string? ImageUrl => string.IsNullOrWhiteSpace(Current?.ImageUrl) ? null : Current!.ImageUrl;

    public Visibility ImageVisibility => !string.IsNullOrWhiteSpace(Current?.ImageUrl) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => string.IsNullOrWhiteSpace(Current?.ImageUrl) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"2/3" 位置标签;只有一条时留空。</summary>
    public string CountLabel => _items.Count > 1 ? $"{_index + 1}/{_items.Count}" : "";
    public Visibility CountVisibility => _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>启用 且 至少一条有内容 且 未被本次关闭 且 未去广告 才显示。</summary>
    public Visibility BannerVisibility =>
        (_ad.Enabled && _items.Count > 0 && !_hidden && !_adFree())
            ? Visibility.Visible : Visibility.Collapsed;

    private void Rebuild()
    {
        _items = _ad.Items?.Where(i => i is { HasContent: true }).ToList() ?? new List<AdItem>();
        _index = 0;
        _timer.Stop();
        var sec = _ad.IntervalSec > 0 ? _ad.IntervalSec : 6;
        _timer.Interval = TimeSpan.FromSeconds(sec);
        if (_items.Count > 1 && BannerVisibility == Visibility.Visible) _timer.Start();
    }

    private void Next()
    {
        if (_items.Count == 0) return;
        _index = (_index + 1) % _items.Count;
        RaiseCurrent();
    }

    private void RaiseCurrent()
    {
        Raise(nameof(Text));
        Raise(nameof(Url));
        Raise(nameof(ImageUrl));
        Raise(nameof(ImageVisibility));
        Raise(nameof(TextVisibility));
        Raise(nameof(CountLabel));
        Raise(nameof(CountVisibility));
    }

    public void Hide()
    {
        _hidden = true;
        _timer.Stop();
        Raise(nameof(BannerVisibility));
    }

    public void RefreshVisibility()
    {
        Raise(nameof(BannerVisibility));
        if (BannerVisibility == Visibility.Visible && _items.Count > 1) _timer.Start();
        else _timer.Stop();
    }

    /// <summary>广告内容变了(服务器下发新广告)后重建并刷新。</summary>
    public void Refresh()
    {
        Rebuild();
        RaiseCurrent();
        Raise(nameof(BannerVisibility));
    }
}
