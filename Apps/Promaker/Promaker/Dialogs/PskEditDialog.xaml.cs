using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// **D-S7-3c (s6-r31) — LightHouse PSK 입력 modal dialog**.
/// <para/>
/// ApplicationSettingsDialog 의 DataGrid 의 "PSK 설정..." 버튼이 띄움. DataGrid cell 에 평문 PSK 노출 회피 의도
/// (PasswordBox 의 mask + dialog modal lifetime 안에서만 평문 보존). OK 시 <see cref="Result"/> 에 평문 박제,
/// Cancel 시 null. caller (ApplicationSettingsDialog) 가 ShowDialog 결과 true 인 경우만 Result 사용.
/// </summary>
public partial class PskEditDialog : Window
{
    /// <summary>OK 후 평문 PSK. 빈 문자열도 유효 (caller 의 의미 = PSK 제거).</summary>
    public string? Result { get; private set; }

    public PskEditDialog(string serviceDisplayName)
    {
        InitializeComponent();
        ServiceNameText.Text = $"Service: {serviceDisplayName}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = PskBox.Password ?? "";
        DialogResult = true;
        Close();
    }

    private void PskBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // 입력 길이 hint — 보안 정보 노출 회피 위해 길이만 표시.
        var len = PskBox.Password?.Length ?? 0;
        HintText.Text = len == 0
            ? "기존 PSK 가 박제된 경우 빈 값으로 저장하면 PSK 제거됩니다."
            : $"입력 길이: {len} 문자";
    }
}
