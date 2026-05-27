using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// "Local 서비스 설치/재설치" trigger — PSK / Cert PFX Password 동시 입력 modal.
/// <para/>
/// 검증 / 강도 안내 / RNG 버튼 없음 (사용자 결정 2026-05-27). caller (ApplicationSettingsDialog) 가
/// ShowDialog 결과 true 인 경우만 <see cref="PskResult"/> / <see cref="CertPwdResult"/> 사용.
/// </summary>
public partial class EnableLocalServiceDialog : Window
{
    public string? PskResult { get; private set; }
    public string? CertPwdResult { get; private set; }

    public EnableLocalServiceDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        PskResult = PskBox.Password ?? "";
        CertPwdResult = CertPwdBox.Password ?? "";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
