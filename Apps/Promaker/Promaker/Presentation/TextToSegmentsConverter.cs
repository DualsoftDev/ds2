using System;
using System.Globalization;
using System.Windows.Data;
using Promaker.ViewModels;

namespace Promaker.Presentation;

/// <summary>
/// ChatTurn (Text, IsStreaming, Role) MultiBinding → 단일 ChatSegment.
/// 정책 (todo-llm-markdown.md 옵션 1 / A1 전환):
///  - streaming 중 (IsStreaming=true) → Plain (token 단위 MdXaml 재파싱 회피)
///  - assistant role + 종료 → Markdown (전체 응답 MdXaml 렌더, fence-split 없음)
///  - 그 외 role (user/system/tool/thinking/error) → Plain
///  - model-doc-button role 은 LlmChatPanel.xaml 에서 ItemsControl 전체 Visibility=Collapsed.
/// 향후 fence-split 재도입이 필요해지면 본 converter 만 교체 (ItemsControl 구조 유지). 따라서
/// 결과 collection 인터페이스는 length=1 의 다중 segment 형식을 유지 — 본 컨버터는 의도적 wrapper.
/// </summary>
public sealed class TextToSegmentsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var text        = values is [string t, ..]                 ? t : "";
        var isStreaming = values is [_, bool s, ..]      && s;
        var role        = values is [_, _, string r, ..]           ? r : "";

        var kind = (!isStreaming && role == ChatTurn.Roles.Assistant)
            ? ChatSegmentKind.Markdown
            : ChatSegmentKind.Plain;
        return new[] { new ChatSegment(kind, text) };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException(
               $"{nameof(TextToSegmentsConverter)} 는 OneWay 전용 — ConvertBack 호출되어선 안 됨.");
}
