using System.Windows;
using Ds2.Core;

namespace Promaker.Dialogs;

/// <summary>접점 선택 항목 — 접힌 상태(ShortText)와 드롭다운(FullText) 표기를 분리한다.</summary>
public sealed class ContactKindChoice
{
    public ContactKindChoice(string shortText, string fullText, ContactKind kind)
    {
        ShortText = shortText;
        FullText = fullText;
        Kind = kind;
    }

    public string ShortText { get; }
    public string FullText { get; }
    public ContactKind Kind { get; }
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
            new ContactKindChoice("부정조건", "부정조건 (─┤/├─) · 참조 신호가 ON 일 때 실행", ContactKind.NcContact),
            new ContactKindChoice("참조건",   "참조건 (─┤├─) · 참조 신호가 OFF 일 때 실행",  ContactKind.NoContact),
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

    /// <summary>SkipAction 일 때만 의미 있는 접점 종류. 그 외 유형이면 null.</summary>
    public ContactKind? SelectedContactKind =>
        SkipActionRadio.IsChecked == true
            ? (ContactKindCombo.SelectedItem as ContactKindChoice)?.Kind ?? ContactKind.NcContact
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
