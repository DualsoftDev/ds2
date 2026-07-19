using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Promaker.Services;

namespace Promaker.Dialogs.Pv;

/// <summary>
/// PV(클라우드) 로그인 다이얼로그. ID/PW 를 받아 <see cref="IPvClient.Login"/> 을 호출하고,
/// 성공 시 세션 토큰이 담긴 <see cref="PvLoginResult"/> 를, 취소 시 null 을 반환한다.
///
/// 이 다이얼로그는 UI 폼일 뿐이고 서버 URL·스키마는 모른다 — 실제 통신은 IPvClient 뒤의
/// 네이티브 PvClient.dll(git 제외)이 담당하므로 이 파일은 public 저장소에 안전하다.
/// 기존 <c>DialogHelpers.PromptName</c> 의 코드-구성 Window 패턴을 따른다.
/// </summary>
public static class PvLoginDialog
{
    /// <summary>모달로 띄운다. 로그인 성공 시 토큰 담은 결과, 취소/실패-후-취소 시 null.</summary>
    public static PvLoginResult? Show(IPvClient client, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "클라우드 로그인",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        PvDialogTheme.Apply(dialog);

        var idBox = new TextBox { Margin = new Thickness(12, 4, 12, 0) };
        var pwBox = new PasswordBox { Margin = new Thickness(12, 4, 12, 0) };
        var status = new TextBlock
        {
            Margin = new Thickness(12, 8, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed
        };

        var okButton = new Button { Content = "로그인", Width = 90, Margin = new Thickness(0, 8, 0, 12), IsDefault = true };
        var cancelButton = new Button { Content = "취소", Width = 90, Margin = new Thickness(8, 8, 12, 12), IsCancel = true };
        if (Application.Current?.TryFindResource("DarkButton") is Style darkStyle)
        {
            okButton.Style = darkStyle;
            cancelButton.Style = darkStyle;
        }

        PvLoginResult? result = null;
        okButton.Click += (_, _) =>
        {
            var id = idBox.Text?.Trim() ?? "";
            var pw = pwBox.Password ?? "";
            if (id.Length == 0 || pw.Length == 0)
            {
                status.Text = "아이디와 비밀번호를 입력하세요.";
                return;
            }

            okButton.IsEnabled = false;
            status.Foreground = Brushes.Gray;
            status.Text = "로그인 중...";

            var r = client.Login(id, pw);

            okButton.IsEnabled = true;
            if (r.Ok)
            {
                result = r;
                dialog.DialogResult = true;
            }
            else
            {
                status.Foreground = Brushes.IndianRed;
                status.Text = r.Message ?? "로그인에 실패했습니다.";
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "아이디", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock { Text = "비밀번호", Margin = new Thickness(12, 8, 12, 0) });
        panel.Children.Add(pwBox);
        panel.Children.Add(status);

        // 회원가입 / 아이디·비밀번호 찾기 진입점 (같은 client 로 각 다이얼로그를 띄운다)
        var links = new TextBlock { Margin = new Thickness(12, 10, 12, 0), FontSize = 12 };
        var joinLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("회원가입"));
        joinLink.TextDecorations = null;
        joinLink.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentBrush");
        joinLink.Click += (_, _) => PvRegisterDialog.Show(client, dialog);
        var findLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("아이디·비밀번호 찾기"));
        findLink.TextDecorations = null;
        findLink.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentBrush");
        findLink.Click += (_, _) => PvFindDialog.Show(client, dialog);
        links.Inlines.Add(joinLink);
        links.Inlines.Add(new System.Windows.Documents.Run("          "));
        links.Inlines.Add(findLink);
        panel.Children.Add(links);

        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.Loaded += (_, _) => idBox.Focus();

        return dialog.ShowDialog() == true ? result : null;
    }
}
