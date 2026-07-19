using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Promaker.Services;

namespace Promaker.Dialogs.Pv;

/// <summary>
/// 아이디·비밀번호 찾기 다이얼로그. 가입한 아이디 또는 이메일을 입력받아
/// <see cref="IPvClient.FindCredentials"/> 를 호출한다(복구 안내 발송 등). 성공해도 안내만 띄우고
/// 사용자가 직접 닫는다. 서버 정보는 모른다 — public 저장소 안전.
/// </summary>
public static class PvFindDialog
{
    public static void Show(IPvClient client, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "아이디·비밀번호 찾기",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        PvDialogTheme.Apply(dialog);

        var queryBox = new TextBox { Margin = new Thickness(12, 4, 12, 0) };
        var status = new TextBlock
        {
            Margin = new Thickness(12, 8, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed
        };

        var okButton = new Button { Content = "찾기", Width = 90, Margin = new Thickness(0, 8, 0, 12), IsDefault = true };
        var cancelButton = new Button { Content = "닫기", Width = 90, Margin = new Thickness(8, 8, 12, 12), IsCancel = true };
        if (Application.Current?.TryFindResource("DarkButton") is Style dark)
        {
            okButton.Style = dark;
            cancelButton.Style = dark;
        }

        okButton.Click += (_, _) =>
        {
            var q = queryBox.Text?.Trim() ?? "";
            if (q.Length == 0) { status.Foreground = Brushes.IndianRed; status.Text = "아이디 또는 이메일을 입력하세요."; return; }

            okButton.IsEnabled = false;
            status.Foreground = Brushes.Gray;
            status.Text = "요청 처리 중...";

            var r = client.FindCredentials(q);

            okButton.IsEnabled = true;
            status.Foreground = r.Ok ? Brushes.SeaGreen : Brushes.IndianRed;
            status.Text = r.Message ?? (r.Ok ? "복구 안내를 발송했습니다." : "요청을 처리하지 못했습니다.");
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "가입한 아이디 또는 이메일", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(queryBox);
        panel.Children.Add(status);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.Loaded += (_, _) => queryBox.Focus();

        dialog.ShowDialog();
    }
}
