using System.Security;
using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// "Local 서비스 설치/재설치" trigger — 단일 비밀번호 입력 modal.
/// <para/>
/// **단일 비밀번호 통합 (2026-05-30)** — 이전 PSK / Cert PFX Password 2개 입력창을 하나로 통합.
/// 입력된 단일 SecureString 이 caller (<see cref="Promaker.Services.LightHouseLocalInstaller"/>) 에서
/// PSK 와 cert PFX password 양쪽에 동일하게 사용된다.
/// <para/>
/// **검증 / 강도 안내 / RNG 버튼 없음** (사용자 결정 2026-05-27). caller (ApplicationSettingsDialog) 가
/// ShowDialog 결과 true 인 경우만 <see cref="PasswordResult"/> 사용. 빈 값만 차단.
/// <para/>
/// **B4 (2026-05-27) — SecureString 정공 채택**: <c>PasswordBox.SecurePassword</c> (SecureString) 그대로
/// caller 에 전달. caller 가 SecureString → UTF-8 byte[] 변환 후 즉시 Array.Clear + ZeroFreeGlobalAllocUnicode 의무.
/// dialog 는 SecureString 소유권 caller 로 이양 — caller 가 Dispose 책임.
/// </summary>
public partial class EnableLocalServiceDialog : Window
{
    /// <summary>**단일 비밀번호 통합 (2026-05-30)** — PasswordBox.SecurePassword 의 평문 미노출 path.
    /// PSK 와 cert PFX password 공용. caller 가 Dispose 의무.</summary>
    public SecureString? PasswordResult { get; private set; }

    public EnableLocalServiceDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 빈 값 차단 (강도 안내는 사용자 결정 정합으로 제외).
        // **B4 (2026-05-27)** — SecurePassword 박제. PasswordBox.SecurePassword 가 매 호출마다 신규 SecureString 반환
        // (WPF 내부 buffer 복사) — Length 검사 후 그대로 caller 에 이양. 평문 변환 없음.
        var pwd = PwdBox.SecurePassword;
        if (pwd.Length == 0)
        {
            pwd.Dispose();
            MessageBox.Show(this,
                "비밀번호를 입력하십시오 (빈 값 거부).",
                "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // immutable 박제 — caller 가 사용 후 Dispose 시 후속 mutate 불가.
        pwd.MakeReadOnly();
        PasswordResult = pwd;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
