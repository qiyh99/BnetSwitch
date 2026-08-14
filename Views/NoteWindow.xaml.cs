using System.Windows;
using System.Windows.Input;

namespace BnetSwitch;

/// <summary>给单个账号写备注的小弹窗。确定后从 <see cref="Note"/> 取结果,空串表示清掉备注。</summary>
public partial class NoteWindow : Window
{
    public NoteWindow(string battleTag, string note)
    {
        InitializeComponent();
        TagText.Text = battleTag;
        NoteBox.Text = note ?? "";
        // 打开就把光标放到末尾:改备注比新写多
        Loaded += (_, _) => { NoteBox.Focus(); NoteBox.CaretIndex = NoteBox.Text.Length; };
    }

    /// <summary>用户填的备注(已去首尾空白)。</summary>
    public string Note => NoteBox.Text.Trim();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
