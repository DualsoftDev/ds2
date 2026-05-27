using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// "Local 서비스 설치/재설치" trigger — PSK / Cert PFX Password 동시 입력 modal.
/// <para/>
/// **검증 / 강도 안내 / RNG 버튼 없음** (사용자 결정 2026-05-27). caller (ApplicationSettingsDialog) 가
/// ShowDialog 결과 true 인 경우만 <see cref="PskResult"/> / <see cref="CertPwdResult"/> 사용.
/// <para/>
/// **메타리뷰 M9 (2026-05-27)** — 빈 값 차단만 추가 (사용자 결정 항목 외라 안전).
/// <para/>
/// **메타리뷰 M4 (2026-05-27) — SecureString lifetime 격하 박제**: 본 dialog 는 WPF <c>PasswordBox.Password</c>
/// (managed string) 사용. <c>PasswordBox.SecurePassword</c> (SecureString) 미사용 = heap 평문 잔존 risk.
/// 정공 path (SecurePassword → byte[] → Array.Clear) 박제는 별 phase 권장 — caller (LightHouseLocalInstaller) 가
/// string 인자 받는 시그니처라 dialog 만 SecureString 박제해도 caller 단계에서 string 변환되어 의미 없음.
/// 전체 chain (UI dialog ↔ installer ↔ ps1 temp file) 의 byte[] / SecureString 정공 박제는 별 phase.
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
        // **M9 (2026-05-27)** — 빈 값 차단 (강도 안내는 사용자 결정 정합으로 제외).
        var psk = PskBox.Password ?? "";
        var cpw = CertPwdBox.Password ?? "";
        if (string.IsNullOrEmpty(psk) || string.IsNullOrEmpty(cpw))
        {
            MessageBox.Show(this,
                "PSK 와 Cert PFX Password 둘 다 입력 필수입니다 (빈 값 거부).",
                "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PskResult = psk;
        CertPwdResult = cpw;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
