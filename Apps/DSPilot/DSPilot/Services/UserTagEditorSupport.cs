// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using System.Text;
using DSPilot.Models.UserTagAlerts;

namespace DSPilot.Services;

/// <summary>
/// 설정▸수동등록TAG 편집기의 공통 규칙 — 허용 값 타입/매칭 조건 표, 항목 검증, CSV 양식(내보내기/가져오기).
///
/// 규칙은 Promaker UserTagEditDialog / UserTagPanel(CSV) 과 맞춘다:
///   · LogLevel 은 항상 Error(Warning/Info 미사용) — CSV 의 '로그 레벨' 컬럼은 읽되 무시.
///   · 값 타입별 매칭 조건: Bit=Rising/Falling/Changed/Eq/Neq, String=Changed/Eq/Neq, 수치=Changed+비교 6종.
///   · CSV 6컬럼 헤더 `이름,로그 레벨,태그 주소,값 타입,매칭 조건,기준값` + UTF-8 BOM(Excel 한글 호환).
///     DSPilot 은 다중 System 을 한 파일로 다루므로 맨 앞에 `System` 컬럼을 둔 7컬럼이 기본이며,
///     Promaker 가 내보낸 6컬럼 파일도 그대로 읽는다(System 컬럼 부재 = 호출 측이 지정한 System 으로).
/// </summary>
public static class UserTagEditorSupport
{
    static UserTagEditorSupport()
    {
        // Excel 이 '기본 CSV' 로 저장한 파일은 한국어 Windows 에서 CP949(ANSI) 인 경우가 많다.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 이미 등록 */ }
    }

    public static readonly string[] ValueTypes = ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    public static readonly string[] BitMatchOps = ["RisingEdge", "FallingEdge", "Changed", "Eq", "Neq"];
    public static readonly string[] StringMatchOps = ["Changed", "Eq", "Neq"];
    public static readonly string[] NumericMatchOps = ["Changed", "Eq", "Neq", "Gt", "Gte", "Lt", "Lte"];

    /// <summary>기준값이 의미를 갖는(필수인) 매칭 조건.</summary>
    private static readonly HashSet<string> OpsNeedingValue =
        new(["Eq", "Neq", "Gt", "Gte", "Lt", "Lte"], StringComparer.OrdinalIgnoreCase);

    public static string[] MatchOpsFor(string valueType) => NormalizeValueType(valueType) switch
    {
        "Bit" => BitMatchOps,
        "String" => StringMatchOps,
        _ => NumericMatchOps,
    };

    /// <summary>F# UserTagHelpers.parseValueType 과 같은 별칭을 받아 표준 표기로. 미일치 시 null.</summary>
    public static string? NormalizeValueType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToUpperInvariant() switch
        {
            "BIT" or "BOOL" => "Bit",
            "BYTE" => "Byte",
            "WORD" or "UINT16" => "Word",
            "DWORD" or "UINT32" => "DWord",
            "INT16" or "INT" or "SHORT" => "Int16",
            "INT32" or "DINT" or "LONG" => "Int32",
            "REAL" or "FLOAT" => "Real",
            "STRING" or "STR" => "String",
            _ => null,
        };
    }

    /// <summary>F# UserTagHelpers.parseMatchOp 과 같은 별칭을 받아 표준 표기로. 빈값=타입 기본(Bit→RisingEdge/그 외→Changed). 미일치 시 null.</summary>
    public static string? NormalizeMatchOp(string? s, string valueType)
    {
        if (string.IsNullOrWhiteSpace(s))
            return NormalizeValueType(valueType) == "Bit" ? "RisingEdge" : "Changed";
        return s.Trim().ToUpperInvariant() switch
        {
            "EQ" or "==" or "=" => "Eq",
            "NEQ" or "!=" or "<>" => "Neq",
            "GT" or ">" => "Gt",
            "GTE" or ">=" => "Gte",
            "LT" or "<" => "Lt",
            "LTE" or "<=" => "Lte",
            "RISINGEDGE" or "RISING" => "RisingEdge",
            "FALLINGEDGE" or "FALLING" => "FallingEdge",
            "CHANGED" => "Changed",
            _ => null,
        };
    }

    public static bool NeedsMatchValue(string matchOp) => OpsNeedingValue.Contains(matchOp);

    /// <summary>
    /// 항목 1건 정규화+검증. 성공 시 정규화된 항목과 null, 실패 시 (null, 사유).
    /// 이름/주소 공백, 타입·조건 미일치, 조건-타입 불일치, 기준값 누락/수치 아님을 잡는다.
    /// 이름 중복은 목록 단위 규칙이라 <see cref="FindDuplicateNames"/> 에서 따로 본다.
    /// </summary>
    public static (UserTagWriteEntry? Entry, string? Error) Normalize(
        string? name, string? tagAddress, string? valueType, string? matchOp, string? matchValue)
    {
        var n = (name ?? string.Empty).Trim();
        var a = (tagAddress ?? string.Empty).Trim();
        if (n.Length == 0) return (null, "이름이 비어 있습니다.");
        if (a.Length == 0) return (null, "태그 주소가 비어 있습니다.");
        if (a.Any(char.IsWhiteSpace)) return (null, "태그 주소에 공백이 있습니다.");
        var vt = NormalizeValueType(string.IsNullOrWhiteSpace(valueType) ? "Bit" : valueType);
        if (vt is null) return (null, $"알 수 없는 값 타입 '{valueType}' (허용: {string.Join("/", ValueTypes)}).");
        var op = NormalizeMatchOp(matchOp, vt);
        if (op is null) return (null, $"알 수 없는 매칭 조건 '{matchOp}'.");
        if (!MatchOpsFor(vt).Contains(op, StringComparer.Ordinal))
            return (null, $"매칭 조건 '{op}' 는 값 타입 {vt} 에 쓸 수 없습니다 (허용: {string.Join("/", MatchOpsFor(vt))}).");
        var mv = (matchValue ?? string.Empty).Trim();
        if (NeedsMatchValue(op))
        {
            if (mv.Length == 0) return (null, $"매칭 조건 '{op}' 에는 기준값이 필요합니다.");
            if (vt is not ("Bit" or "String")
                && !double.TryParse(mv, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return (null, $"기준값 '{mv}' 는 숫자가 아닙니다 (값 타입 {vt}).");
            if (vt == "Bit" && mv is not ("0" or "1" or "true" or "false" or "True" or "False" or "TRUE" or "FALSE"))
                return (null, $"Bit 기준값은 0/1(true/false) 만 허용합니다 ('{mv}').");
        }
        else mv = string.Empty; // edge/Changed 는 기준값 무의미 — 저장 시 비운다(Promaker 와 동일).
        return (new UserTagWriteEntry(n, a, vt, op, mv), null);
    }

    /// <summary>같은 System 안에서 대소문자 무시로 겹치는 이름 목록.</summary>
    public static List<string> FindDuplicateNames(IEnumerable<string> names) =>
        names.GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase)
             .Where(g => g.Count() > 1)
             .Select(g => g.Key)
             .ToList();

    // ── CSV ─────────────────────────────────────────────────────────────────

    /// <summary>Promaker UserTagPanel.CsvHeaderColumns 와 동일한 6컬럼 + DSPilot 확장 System 컬럼(맨 앞).</summary>
    public static readonly string[] CsvHeader = ["System", "이름", "로그 레벨", "태그 주소", "값 타입", "매칭 조건", "기준값"];

    public const string CsvMimeType = "text/csv; charset=utf-8";

    /// <summary>UTF-8 BOM 포함 CSV 바이트. rows 가 비면 헤더만(양식).</summary>
    public static byte[] BuildCsv(IEnumerable<UtEditorTagDto> rows, bool includeExample)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvHeader));
        var any = false;
        foreach (var r in rows)
        {
            any = true;
            sb.AppendLine(string.Join(",",
                Esc(r.SystemName), Esc(r.Name), "Error", Esc(r.TagAddress), Esc(r.ValueType), Esc(r.MatchOp), Esc(r.MatchValue ?? string.Empty)));
        }
        if (!any && includeExample)
        {
            sb.AppendLine(string.Join(",", "", "예시_모터과부하", "Error", "M901", "Bit", "RisingEdge", ""));
            sb.AppendLine(string.Join(",", "", "예시_생산카운터", "Error", "D100", "Word", "Gte", "1000"));
        }
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>
    /// CSV 바이트 → 행 목록(검증 결과 포함). 인코딩 = BOM 있으면 UTF-8, 없으면 UTF-8 엄격 디코드 시도 후 실패 시 CP949.
    /// 헤더 행은 첫 셀이 'System'/'이름'/'Name' 이면 건너뛴다. System 컬럼은 첫 셀이 헤더상 System 일 때만 존재한다고 본다
    /// (Promaker 6컬럼 파일 = System 없음). 헤더가 없는 파일은 컬럼 수(7=System 포함, ≤6=미포함)로 추정.
    /// </summary>
    public static UtCsvParseResult ParseCsv(byte[] bytes)
    {
        var (text, encodingName) = Decode(bytes);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rows = new List<UtCsvRowDto>();
        var hasSystemCol = false;
        var headerDetected = false;
        var start = 0;
        if (lines.Length > 0)
        {
            var first = ParseLine(lines[0]);
            var c0 = first.Count > 0 ? first[0].Trim() : string.Empty;
            if (c0.Equals("System", StringComparison.OrdinalIgnoreCase)) { hasSystemCol = true; headerDetected = true; start = 1; }
            else if (c0.Contains("이름") || c0.StartsWith("Name", StringComparison.OrdinalIgnoreCase)) { headerDetected = true; start = 1; }
            else hasSystemCol = first.Count >= 7;
        }
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = ParseLine(line);
            var off = hasSystemCol ? 1 : 0;
            string Cell(int idx) => idx < p.Count ? p[idx].Trim() : string.Empty;
            var sys = hasSystemCol ? Cell(0) : string.Empty;
            var name = Cell(off + 0);
            var addr = Cell(off + 2);
            var vt = Cell(off + 3);
            var op = Cell(off + 4);
            var mv = Cell(off + 5);
            if (p.Count - off < 3)
            {
                rows.Add(new UtCsvRowDto(i + 1, sys, name, addr, vt, op, mv, "컬럼이 부족합니다(최소: 이름, 로그 레벨, 태그 주소)."));
                continue;
            }
            var (entry, err) = Normalize(name, addr, string.IsNullOrWhiteSpace(vt) ? "Bit" : vt, op, mv);
            rows.Add(entry is null
                ? new UtCsvRowDto(i + 1, sys, name, addr, vt, op, mv, err)
                : new UtCsvRowDto(i + 1, sys, entry.Name, entry.TagAddress, entry.ValueType, entry.MatchOp, entry.MatchValue, null));
        }
        return new UtCsvParseResult(rows, headerDetected, hasSystemCol, encodingName);
    }

    private static (string Text, string Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "utf-8(BOM)");
        try { return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "utf-8"); }
        catch (DecoderFallbackException)
        {
            try { return (Encoding.GetEncoding(949).GetString(bytes), "cp949"); }
            catch { return (Encoding.Latin1.GetString(bytes), "latin1"); }
        }
    }

    /// <summary>따옴표 필드/이스케이프("") 지원 단순 CSV 행 파서 — Promaker CsvParseLine 과 동일 규칙.</summary>
    private static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQ = false;
                }
                else cur.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }
}
