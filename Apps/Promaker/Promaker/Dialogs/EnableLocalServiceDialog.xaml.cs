using System.Security;
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
/// **B4 (2026-05-27) — SecureString 정공 채택**: 이전 (M4) 의 managed string path 폐기. <c>PasswordBox.SecurePassword</c>
/// (SecureString) 그대로 caller (<see cref="Promaker.Services.LightHouseLocalInstaller"/>) 에 전달.
/// caller 가 SecureString → UTF-8 byte[] 변환 후 즉시 Array.Clear + ZeroFreeGlobalAllocUnicode 의무.
/// dialog 는 SecureString 소유권 caller 로 이양 — caller 가 Dispose 책임.
/// </summary>
public partial class EnableLocalServiceDialog : Window
{
    /// <summary>**B4 (2026-05-27)** — PasswordBox.SecurePassword 의 평문 미노출 path. caller 가 Dispose 의무.</summary>
    public SecureString? PskResult { get; private set; }
    /// <summary>**B4 (2026-05-27)** — Cert PFX password SecureString. caller 가 Dispose 의무.</summary>
    public SecureString? CertPwdResult { get; private set; }

    public EnableLocalServiceDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // **M9 (2026-05-27)** — 빈 값 차단 (강도 안내는 사용자 결정 정합으로 제외).
        // **B4 (2026-05-27)** — SecurePassword 박제. PasswordBox.SecurePassword 가 매 호출마다 신규 SecureString 반환
        // (WPF 내부 buffer 복사) — Length 검사 후 그대로 caller 에 이양. 평문 변환 없음.
        var psk = PskBox.SecurePassword;
        var cpw = CertPwdBox.SecurePassword;
        if (psk.Length == 0 || cpw.Length == 0)
        {
            psk.Dispose();
            cpw.Dispose();
            MessageBox.Show(this,
                "PSK 와 Cert PFX Password 둘 다 입력 필수입니다 (빈 값 거부).",
                "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // immutable 박제 — caller 가 사용 후 Dispose 시 후속 mutate 불가.
        psk.MakeReadOnly();
        cpw.MakeReadOnly();
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
