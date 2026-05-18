using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Expression;

namespace Promaker.Presentation;

/// <summary>
/// ST 텍스트 편집기 공통 조작 — TextBox caret 삽입, format(라운드트립), 에러 위치 추출.
/// ExpressionEditorView / ConditionEditDialog 양쪽에서 공유.
/// </summary>
public static class StEditorOps
{
    /// <summary>현재 caret 또는 selection 위치에 snippet 삽입. caret 은 snippet 끝으로 이동.</summary>
    public static void InsertAtCaret(TextBox box, string snippet)
    {
        if (box is null || box.IsReadOnly || string.IsNullOrEmpty(snippet)) return;
        int caret = box.SelectionStart;
        int len   = box.SelectionLength;
        var src = box.Text ?? "";
        box.Text = src.Substring(0, caret) + snippet + src.Substring(caret + len);
        box.SelectionStart  = caret + snippet.Length;
        box.SelectionLength = 0;
        box.Focus();
    }

    /// <summary>괄호 페어 snippet 삽입 — caret 을 괄호 안쪽으로 이동.
    /// 예: "R_TRIG(", ")" 호출 시 R_TRIG() 삽입 후 caret 이 ( 와 ) 사이.</summary>
    public static void InsertParenSnippet(TextBox box, string before, string after)
    {
        if (box is null || box.IsReadOnly) return;
        int caret = box.SelectionStart;
        int len   = box.SelectionLength;
        var src = box.Text ?? "";
        box.Text = src.Substring(0, caret) + before + after + src.Substring(caret + len);
        box.SelectionStart  = caret + before.Length;
        box.SelectionLength = 0;
        box.Focus();
    }

    /// <summary>parse → simplify → toSt 라운드트립으로 정규 표기 형식 산출. 실패 시 null.</summary>
    public static string? Format(string input)
    {
        if (!CoilConditionParser.TryParse(input, out var cond, out _) || cond is null) return null;
        return CoilConditionModule.toSt(CoilConditionModule.simplify(cond));
    }

    /// <summary>파서 에러 메시지에서 "position N" 또는 "at N" 패턴 추출. 실패 시 -1.</summary>
    public static int ExtractErrorPosition(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return -1;
        var m = Regex.Match(errorMessage, @"position\s+(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) return p;
        return -1;
    }

    /// <summary>TextBox caret 을 (line, col) 위치로 이동. 1-based 라인/컬럼 반환.</summary>
    public static (int Line, int Col) GetCursorLineCol(TextBox box)
    {
        if (box is null) return (1, 1);
        int caret = box.SelectionStart;
        var text = box.Text ?? "";
        if (caret < 0 || caret > text.Length) return (1, 1);
        int line = 1, col = 1;
        for (int i = 0; i < caret; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }

    /// <summary>식 안의 leaf 변수 개수 (AND/OR/NOT 등 결합자 제외).</summary>
    public static int LeafCount(string text)
    {
        if (!CoilConditionParser.TryParse(text, out var cond, out _) || cond is null) return 0;
        return CoilConditionParser.ExtractIdentifiers(text).Count;
    }

    /// <summary>심볼 후보 필터 — 부분 문자열 (대소문자 무관).</summary>
    public static IEnumerable<string> FilterSymbols(IEnumerable<string> source, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return source;
        return source.Where(s => s.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // ── Inline AutoComplete ────────────────────────────────────────────────

    /// <summary>caret 직전의 식별자(prefix) 추출. (시작 인덱스, 단어). 식별자 시작 문자가 아니면 (-1, "").</summary>
    public static (int Start, string Word) GetWordBeforeCaret(TextBox box)
    {
        if (box is null) return (-1, "");
        var text = box.Text ?? "";
        int caret = box.SelectionStart;
        if (caret <= 0 || caret > text.Length) return (-1, "");
        int s = caret - 1;
        while (s >= 0 && IsIdentChar(text[s])) s--;
        s++;
        if (s >= caret) return (-1, "");
        if (!IsIdentStart(text[s])) return (-1, "");
        return (s, text.Substring(s, caret - s));
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c)  => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    /// <summary>caret 위치를 prefix 시작 ~ caret 사이 텍스트로 교체. caret 은 교체 후 끝으로 이동.</summary>
    public static void ReplaceWordBeforeCaret(TextBox box, int wordStart, string replacement)
    {
        if (box is null) return;
        var src = box.Text ?? "";
        int caret = box.SelectionStart;
        if (wordStart < 0 || wordStart > caret || caret > src.Length) return;
        box.Text = src.Substring(0, wordStart) + replacement + src.Substring(caret);
        box.SelectionStart  = wordStart + replacement.Length;
        box.SelectionLength = 0;
        box.Focus();
    }

    /// <summary>prefix 시작 위치를 prefix 매치하는 후보 목록으로 — 빠른 prefix 우선, 그 다음 부분 매치.</summary>
    public static IEnumerable<string> RankAutoCompleteCandidates(IEnumerable<string> all, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return all;
        var prefixHits = new List<string>();
        var substringHits = new List<string>();
        foreach (var s in all)
        {
            if (s.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) prefixHits.Add(s);
            else if (s.IndexOf(prefix, System.StringComparison.OrdinalIgnoreCase) >= 0) substringHits.Add(s);
        }
        prefixHits.Sort(System.StringComparer.OrdinalIgnoreCase);
        substringHits.Sort(System.StringComparer.OrdinalIgnoreCase);
        return prefixHits.Concat(substringHits);
    }
}
