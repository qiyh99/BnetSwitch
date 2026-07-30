using System.Windows;
using System.Windows.Input;

namespace BnetSwitch;

public partial class ContactWindow : Window
{
    private readonly string _qqUrl;
    private readonly string _githubUrl;

    public ContactWindow(string qqUrl, string githubUrl)
    {
        InitializeComponent();
        _qqUrl = qqUrl;
        _githubUrl = githubUrl;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnQQ(object sender, MouseButtonEventArgs e) => BnetSwitch.Services.LinkOpener.Open(_qqUrl);
    private void OnGithub(object sender, MouseButtonEventArgs e) => BnetSwitch.Services.LinkOpener.Open(_githubUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
