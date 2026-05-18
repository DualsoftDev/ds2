using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Promaker.Presentation;

/// <summary>
/// ST 수식 텍스트 → 색상 입힌 WPF Run 인라인 시퀀스.
/// 사용처: 읽기 전용 TextBlock 의 Inlines 에 결과 추가하여 syntax highlighting 표시.
/// </summary>
public static class StSyntaxHighlight
{
    // VS Dark 유사 팔레트 (테마 무관 — 인라인이라 명시 색상 지정).
    private static readonly Brush KeywordBrush  = Frozen(0x56, 0x9C, 0xD6); // NOT/AND/OR/R_TRIG/F_TRIG
    private static readonly Brush LiteralBrush  = Frozen(0x4E, 0xC9, 0xB0); // TRUE/FALSE
    private static readonly Brush OperatorBrush = Frozen(0xCE, 0x91, 0x78); // &&/||/!/~/&/|/*/+
    private static readonly Brush IdentBrush    = Frozen(0xD4, 0xD4, 0xD4); // 식별자 (기본 텍스트보다 약간 밝게)

    /// <summary>괄호 depth 별 색상 — 매칭 쌍 시각화 (rainbow brackets).</summary>
    private static readonly Brush[] ParenBrushes =
    {
        Frozen(0xFF, 0xD7, 0x00),   // gold
        Frozen(0xDA, 0x70, 0xD6),   // orchid
        Frozen(0x87, 0xCE, 0xEB),   // skyblue
        Frozen(0x9A, 0xCD, 0x32),   // yellowgreen
        Frozen(0xFF, 0x8C, 0x69),   // salmon
    };

    private static readonly HashSet<string> Keywords =
        new(System.StringComparer.OrdinalIgnoreCase) { "AND", "OR", "NOT", "R_TRIG", "F_TRIG" };

    private static readonly HashSet<string> Literals =
        new(System.StringComparer.OrdinalIgnoreCase) { "TRUE", "FALSE" };

    /// <summary>text → 인라인 Run 들. 공백/줄바꿈도 보존 (Run 으로 emit).</summary>
    public static IEnumerable<Inline> ToRuns(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int i = 0;
        int depth = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // 공백 / 줄바꿈 — 그대로 emit (색상 없음).
            if (char.IsWhiteSpace(c))
            {
                int s = i;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                yield return new Run(text[s..i]);
                continue;
            }

            // 괄호 — depth 별 색상.
            if (c == '(')
            {
                yield return Bold("(", ParenBrushes[depth % ParenBrushes.Length]);
                depth++; i++; continue;
            }
            if (c == ')')
            {
                depth = System.Math.Max(0, depth - 1);
                yield return Bold(")", ParenBrushes[depth % ParenBrushes.Length]);
                i++; continue;
            }

            // 기호 연산자 (2 글자 우선).
            if (c == '&' || c == '|')
            {
                int s = i;
                if (i + 1 < text.Length && text[i + 1] == c) i += 2;
                else i++;
                yield return Bold(text[s..i], OperatorBrush);
                continue;
            }
            if (c == '!' || c == '~' || c == '*' || c == '+')
            {
                yield return Bold(c.ToString(), OperatorBrush);
                i++; continue;
            }
            if (c == ',')
            {
                yield return new Run(",");
                i++; continue;
            }

            // 식별자 / 키워드.
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;
                var word = text[s..i];
                if (Keywords.Contains(word))      yield return Bold(word, KeywordBrush);
                else if (Literals.Contains(word)) yield return Bold(word, LiteralBrush);
                else                              yield return new Run(word) { Foreground = IdentBrush };
                continue;
            }

            // 알 수 없는 문자 — 그대로.
            yield return new Run(text[i].ToString());
            i++;
        }
    }

    private static Run Bold(string s, Brush brush) =>
        new(s) { Foreground = brush, FontWeight = FontWeights.Bold };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
