using System.Windows;
using Ds2.Core;

namespace Promaker.Dialogs;

/// <summary>
/// SkipAction 을 만들 때 고르는 «그룹 부정 초기값». 접힌 상태(ShortText)와 드롭다운(FullText) 표기를 분리한다.
///
/// 예전에는 leaf 접점(ContactKind)을 골랐다. 그러면 화면에 `/A=false` 처럼 접점과 값이 같은 부정을
/// 두 번 말해 읽을 수 없었다. 지금은 부정을 그룹이 지므로, 여기서도 그룹 부정을 고른다 —
/// 속성 패널의 토글과 똑같은 말을 써서 두 곳이 어긋나지 않게 한다.
/// </summary>
public sealed class ContactKindChoice
{
    public ContactKindChoice(string shortText, string fullText, bool inverted)
    {
        ShortText = shortText;
        FullText = fullText;
        Inverted = inverted;
    }

    public string ShortText { get; }
    public string FullText { get; }

    /// <summary>true = 조건이 어긋날 때 건너뜀 (Condition.IsInverted).</summary>
    public bool Inverted { get; }
}

public partial class ConditionTypePickerDialog : Window
{
    public ConditionTypePickerDialog() : this(null)
    {
    }

    /// <param name="lockedType">이미 유형이 정해진 경로(속성창 섹션 드롭, Work 대상 등).
    /// 지정하면 유형 라디오를 잠그고 접점만 고르게 한다.</param>
    /// <param name="lockReason">잠긴 이유 안내문. 없으면 기본 문구.</param>
    public ConditionTypePickerDialog(ConditionType? lockedType, string? lockReason = null)
    {
        InitializeComponent();

        ContactKindCombo.ItemsSource = new[]
        {
            // 속성 패널 토글(InvertLabel)과 같은 문구 — 만든 뒤 그 토글로 언제든 바꿀 수 있다.
            new ContactKindChoice("불만족 시 건너뜀", "불만족 시 건너뜀 · 조건이 만족하지 않으면 액션을 건너뜁니다", true),
            new ContactKindChoice("만족 시 건너뜀",   "만족 시 건너뜀 · 조건이 만족하면 액션을 건너뜁니다",       false),
        };
        ContactKindCombo.SelectedIndex = 0;

        if (lockedType is not { } locked)
            return;

        // 정해진 유형을 선택(SkipAction 이면 Checked 이벤트가 접점 콤보를 활성화한다).
        switch (locked)
        {
            case ConditionType.ComAux:     ComAuxRadio.IsChecked = true; break;
            case ConditionType.SkipAction: SkipActionRadio.IsChecked = true; break;
            default:                       AutoAuxRadio.IsChecked = true; break;
        }

        foreach (var radio in new[] { AutoAuxRadio, ComAuxRadio, SkipActionRadio })
        {
            radio.IsEnabled = false;
            radio.Opacity = radio.IsChecked == true ? 1.0 : 0.4;
        }

        TypeLockHint.Text = lockReason ?? $"조건 유형이 {locked} 으로 고정되어 있습니다.";
        TypeLockHint.Visibility = Visibility.Visible;
    }

    public ConditionType SelectedConditionType =>
        ComAuxRadio.IsChecked == true ? ConditionType.ComAux
        : SkipActionRadio.IsChecked == true ? ConditionType.SkipAction
        : ConditionType.AutoAux;

    /// <summary>SkipAction 일 때 고른 그룹 부정 초기값. 그 외 유형이면 null(부정 없음).</summary>
    public bool? SelectedInverted =>
        SkipActionRadio.IsChecked == true
            ? (ContactKindCombo.SelectedItem as ContactKindChoice)?.Inverted ?? false
            : null;

    private void ConditionType_Changed(object sender, RoutedEventArgs e)
    {
        if (ContactKindCombo is null)
            return;

        // 테마 기본 스타일이 비활성 상태를 흐리게 그리지 않아 Opacity 로 명시적으로 딤 처리한다.
        var isSkipAction = SkipActionRadio.IsChecked == true;
        ContactKindCombo.IsEnabled = isSkipAction;
        ContactKindCombo.Opacity = isSkipAction ? 1.0 : 0.45;
    }

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
