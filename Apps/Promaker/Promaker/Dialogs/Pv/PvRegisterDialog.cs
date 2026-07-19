using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Promaker.Services;

namespace Promaker.Dialogs.Pv;

/// <summary>
/// PV 회원가입 다이얼로그 — Pi5 설치마법사 회원가입 폼과 동일하게 아이디(이메일)/비밀번호/비밀번호 확인만 받는다.
/// 서버 register 는 display_name/company 가 optional 이라 보내지 않는다.
/// </summary>
public static class PvRegisterDialog
{
    /// <summary>모달로 띄운다. 가입 성공 시 true.</summary>
    public static bool Show(IPvClient client, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "회원가입",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        PvDialogTheme.Apply(dialog);

        var idBox = new TextBox { Margin = new Thickness(12, 4, 12, 0) };
        var pwBox = new PasswordBox { Margin = new Thickness(12, 4, 12, 0) };
        var pw2Box = new PasswordBox { Margin = new Thickness(12, 4, 12, 0) };
        var status = new TextBlock
        {
            Margin = new Thickness(12, 8, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed
        };

        var okButton = new Button { Content = "가입", Width = 90, Margin = new Thickness(0, 8, 0, 12), IsDefault = true };
        var cancelButton = new Button { Content = "취소", Width = 90, Margin = new Thickness(8, 8, 12, 12), IsCancel = true };

        var ok = false;
        okButton.Click += (_, _) =>
        {
            var id = idBox.Text?.Trim() ?? "";
            var pw = pwBox.Password ?? "";
            var pw2 = pw2Box.Password ?? "";

            if (id.Length == 0 || pw.Length == 0) { status.Text = "아이디와 비밀번호를 입력하세요."; return; }
            if (pw != pw2) { status.Text = "비밀번호가 일치하지 않습니다."; return; }

            okButton.IsEnabled = false;
            status.Foreground = Brushes.Gray;
            status.Text = "가입 처리 중...";

            var r = client.Register(new PvRegisterRequest(id, pw, "", ""));

            okButton.IsEnabled = true;
            if (r.Ok)
            {
                ok = true;
                dialog.DialogResult = true;
            }
            else
            {
                status.Foreground = Brushes.IndianRed;
                status.Text = r.Message ?? "회원가입에 실패했습니다.";
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel();
        void Field(string label, UIElement input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, panel.Children.Count == 0 ? 12 : 8, 12, 0) });
            panel.Children.Add(input);
        }
        Field("아이디 / 이메일", idBox);
        Field("비밀번호", pwBox);
        Field("비밀번호 확인", pw2Box);
        panel.Children.Add(status);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.Loaded += (_, _) => idBox.Focus();

        dialog.ShowDialog();
        return ok;
    }
}
