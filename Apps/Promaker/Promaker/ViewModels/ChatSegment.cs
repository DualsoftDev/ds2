namespace Promaker.ViewModels;

public enum ChatSegmentKind
{
    Plain,
    Markdown,
}

/// <summary>
/// ChatTurn 의 한 segment. ItemsControl + DataTemplateSelector 가 Kind 별 컨트롤 분기.
/// 현 정책 (done-llm-markdown.md 옵션 1 / A1) — assistant role 의 Text 전체가 단일 Markdown segment.
/// 그 외 role 은 단일 Plain segment. fence-split 은 본 PR 에서 미채택 (LLM 부분-wrap 회귀 회피).
/// </summary>
public sealed record ChatSegment(ChatSegmentKind Kind, string Text);
