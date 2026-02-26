using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ValueSpecDialog : Window
{
    // 타입 인덱스 명시 버전 — 기존 ValueSpec에서 열 때 float32/float64 정확하게 복원
    public ValueSpecDialog(string initialValueSpec, int typeIndex, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
            Title = title;

        SpecEditor.LoadFrom(initialValueSpec, typeIndex);
    }

    // 텍스트 추론 버전 — 사용자 직접 입력 텍스트 재편집 시
    public ValueSpecDialog(string initialValueSpec, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
            Title = title;

        SpecEditor.LoadFromText(initialValueSpec);
    }

    public string ValueSpecText => SpecEditor.GetText();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
