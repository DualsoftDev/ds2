using System.Security;
using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// **D-S7-3c (s6-r31) — LightHouse PSK 입력 modal dialog**.
/// <para/>
/// ApplicationSettingsDialog 의 DataGrid 의 "PSK 설정..." 버튼이 띄움. DataGrid cell 에 평문 PSK 노출 회피 의도
/// (PasswordBox 의 mask + dialog modal lifetime 안에서만 평문 보존). OK 시 <see cref="Result"/> 에 SecureString 박제,
/// Cancel 시 null. caller (ApplicationSettingsDialog) 가 ShowDialog 결과 true 인 경우만 Result 사용.
/// <para/>
/// **PR2 (2026-05-27) — SecureString 정공 path**. Result 가 SecureString (managed string 0). caller 가 Dispose 의무.
/// 빈 SecureString 도 유효 (caller 의 의미 = PSK 제거 — Length==0 으로 판정).
/// </summary>
public partial class PskEditDialog : Window
{
    /// <summary>**PR2 (2026-05-27)** — OK 후 PSK SecureString. 빈 SecureString 도 유효 (caller 의 의미 = PSK 제거).
    /// caller 가 Dispose 의무 (dialog 가 소유권 이양).</summary>
    public SecureString? Result { get; private set; }

    public PskEditDialog(string serviceDisplayName)
    {
        InitializeComponent();
        ServiceNameText.Text = $"Service: {serviceDisplayName}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // **PR2 (2026-05-27)** — PasswordBox.SecurePassword 가 매 호출마다 신규 SecureString 반환 (WPF 내부 buffer 복사).
        // MakeReadOnly 박제 후 caller 이양 — 후속 mutate 차단.
        var ss = PskBox.SecurePassword;
        ss.MakeReadOnly();
        Result = ss;
        DialogResult = true;
        Close();
    }

    private void PskBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // 입력 길이 hint — 보안 정보 노출 회피 위해 길이만 표시.
        // **PR2 (2026-05-27)** — PasswordBox.SecurePassword 가 매 호출마다 신규 SecureString — using 으로 즉시 Dispose.
        using var ss = PskBox.SecurePassword;
        var len = ss.Length;
        HintText.Text = len == 0
            ? "기존 PSK 가 박제된 경우 빈 값으로 저장하면 PSK 제거됩니다."
            : $"입력 길이: {len} 문자";
    }
}
