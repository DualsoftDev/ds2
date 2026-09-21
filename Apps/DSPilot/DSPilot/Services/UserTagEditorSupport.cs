// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using System.Text;
using DSPilot.Models.UserTagAlerts;

namespace DSPilot.Services;

/// <summary>
/// 설정▸사용자 태그 편집기의 공통 규칙 — 허용 값 타입/매칭 조건 표, 항목 검증, CSV 양식(내보내기/가져오기).
///
/// 규칙은 Promaker UserTagEditDialog / UserTagPanel(CSV) 과 맞춘다:
///   · LogLevel 은 <b>종류 축</b>(2026-09-17): Error = 이상알람TAG, Info = 모니터링TAG.
///     AASX 는 둘을 한 리스트에 섞어 담고 화면만 탭으로 갈린다. CSV 의 '로그 레벨' 컬럼도 이제 읽고 쓴다
///     (종전엔 "읽되 무시"하고 내보낼 때 Error 로 박았다 — 모니터링TAG 를 왕복시키면 알람이 됐다).
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

    /// <summary>이상알람TAG — 매칭 조건에 걸리면 알람으로 발화하고 이상·알람 화면에 뜬다.</summary>
    public const string LevelAlarm = "Error";

    /// <summary>모니터링TAG — 발화하지 않는다. 값 변화만 기록해 태그 모니터링 화면이 추이로 보여 준다.</summary>
    public const string LevelMonitor = "Info";

    public static readonly string[] Levels = [LevelAlarm, LevelMonitor];

    /// <summary>
    /// 로그 레벨 = <b>종류 축</b>(2026-09-17). Info 만 모니터링TAG 이고 나머지는 전부 이상알람TAG 다.
    /// <para>
    /// Warning 은 두 앱 모두 쓴 적이 없고(Promaker·DSPilot 이 항상 Error 로 기록했다) 조회도 Error 만
    /// 표시하므로 Error 로 모은다. 빈 값·미일치도 Error 다 — F# <c>parseLogLevel</c> 은 미일치를 Info 로
    /// 떨어뜨리는데, 그 기본값을 그대로 쓰면 값이 깨졌을 때 알람이 조용히 사라진다.
    /// </para>
    /// </summary>
    public static string NormalizeLevel(string? s) =>
        string.Equals((s ?? string.Empty).Trim(), LevelMonitor, StringComparison.OrdinalIgnoreCase)
            ? LevelMonitor : LevelAlarm;

    /// <summary>모니터링TAG 인가(= 알람 발화 대상이 아닌가).</summary>
    public static bool IsMonitorLevel(string? s) => NormalizeLevel(s) == LevelMonitor;

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
        string? name, string? tagAddress, string? valueType, string? matchOp, string? matchValue,
        string? level = null)
    {
        var n = (name ?? string.Empty).Trim();
        var a = (tagAddress ?? string.Empty).Trim();
        if (n.Length == 0) return (null, "이름이 비어 있습니다.");
        if (a.Length == 0) return (null, "태그 주소가 비어 있습니다.");
        if (a.Any(char.IsWhiteSpace)) return (null, "태그 주소에 공백이 있습니다.");
        var vt = NormalizeValueType(string.IsNullOrWhiteSpace(valueType) ? "Bit" : valueType);
        if (vt is null) return (null, $"알 수 없는 값 타입 '{valueType}' (허용: {string.Join("/", ValueTypes)}).");
        var lv = NormalizeLevel(level);

        // 모니터링TAG 는 "언제 발화하나"를 묻지 않는다 — 값이 바뀌면 기록할 뿐이다. 그래서 매칭 조건·기준값을
        // 검증하지 않고 Changed(모든 값 타입이 허용)로 고정한다. 나중에 사용자가 레벨만 이상알람으로 되돌려도
        // 조건이 비어 있지 않도록 빈 칸 대신 Changed 를 박아 둔다.
        if (lv == LevelMonitor)
            return (new UserTagWriteEntry(n, a, vt, "Changed", string.Empty, lv), null);

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
        return (new UserTagWriteEntry(n, a, vt, op, mv, lv), null);
    }

    /// <summary>
    /// 모니터링 메타 검증 — 데드밴드는 음수 불가, 기록 간격은 하루를 넘지 않는다.
    /// 값이 없거나 0 이면 "제한 없음" 이라 정상이다. 문제가 없으면 null.
    /// </summary>
    public static string? ValidateMonitorMeta(double? deadband, int? minIntervalMs)
    {
        if (deadband is < 0) return "데드밴드는 0 이상이어야 합니다.";
        if (deadband is double.NaN or double.PositiveInfinity) return "데드밴드가 숫자가 아닙니다.";
        if (minIntervalMs is < 0) return "최소 기록 간격은 0 이상이어야 합니다.";
        if (minIntervalMs is > 86_400_000) return "최소 기록 간격이 하루를 넘습니다.";
        return null;
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

    /// <summary>
    /// 모니터링TAG 양식 — 위 7컬럼 뒤에 표시·수집 메타 3칸을 덧댄다. 매칭 조건·기준값 칸은 자리만 지키고 비운다
    /// (Promaker 6컬럼 파서와 열 위치를 어긋나게 하지 않으려고 빼지 않는다).
    /// </summary>
    public static readonly string[] CsvHeaderMonitor =
        [.. CsvHeader, "단위", "데드밴드", "최소 기록 간격(ms)"];

    /// <summary>
    /// 이상알람TAG 양식 — 위 7컬럼 뒤에 디바이스 1칸. 모니터링 양식과 같은 자리(8열)를 쓰지만 뜻은 종류마다 다르다
    /// (양식 자체가 탭별이라 섞이지 않는다). 앞 7칸 위치를 지켜 Promaker 6컬럼 파서와 어긋나지 않는다.
    /// <para>이 칸을 빼면 CSV 교체 가져오기가 사용자가 묶어 둔 귀속을 조용히 지운다 — 값은 DSPilot 설정에 있고
    /// CSV 는 그 System 의 최종 목록이기 때문이다.</para>
    /// </summary>
    public static readonly string[] CsvHeaderAlarm = [.. CsvHeader, "디바이스"];

    /// <summary>
    /// CSV 디바이스 칸의 '전역' 표기. 빈 칸(미지정)과 구분해야 왕복에서 두 상태가 뭉개지지 않는다.
    /// DevicesAlias 로 쓰일 수 없는 글자라 실제 디바이스 이름과 충돌하지 않는다(Call 이름 "{alias}.{api}" 의 앞부분).
    /// 편집기 폼의 센티넬(settings.html UT_DEVICE_GLOBAL)과 같은 글자를 쓴다.
    /// </summary>
    public const string CsvDeviceGlobal = "*";

    /// <summary>
    /// CSV 디바이스 칸 → 저장 표현. 빈 칸 = <c>null</c>(미지정), <see cref="CsvDeviceGlobal"/> = <c>""</c>(전역), 그 외 = 이름.
    /// </summary>
    public static string? ParseCsvDevice(string? cell)
    {
        var v = (cell ?? string.Empty).Trim();
        if (v.Length == 0) return null;
        return v == CsvDeviceGlobal ? string.Empty : v;
    }

    public const string CsvMimeType = "text/csv; charset=utf-8";

    /// <summary>
    /// UTF-8 BOM 포함 CSV 바이트. rows 가 비면 헤더만(양식).
    /// <para>레벨 칸은 이제 실제 종류를 적는다(종전엔 "Error" 고정). 양식 예시는 <paramref name="level"/> 에 맞춘다 —
    /// 모니터링TAG 탭에서 받은 양식으로 등록했더니 알람이 되더라는 사고를 막는다.</para>
    /// </summary>
    public static byte[] BuildCsv(IEnumerable<UtEditorTagDto> rows, bool includeExample, string? level = null)
    {
        var lv = NormalizeLevel(level);
        var monitor = lv == LevelMonitor;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", monitor ? CsvHeaderMonitor : CsvHeaderAlarm));
        var any = false;
        foreach (var r in rows)
        {
            any = true;
            var cells = new List<string>
            {
                Esc(r.SystemName), Esc(r.Name), NormalizeLevel(r.Level), Esc(r.TagAddress), Esc(r.ValueType),
                Esc(r.MatchOp), Esc(r.MatchValue ?? string.Empty),
            };
            if (monitor)
            {
                cells.Add(Esc(r.Unit ?? string.Empty));
                cells.Add(r.Deadband?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                cells.Add(r.MinIntervalMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            }
            else
            {
                // 세 상태를 한 칸에 담는다: 빈 칸 = 미지정, CsvDeviceGlobal = 전역, 그 외 = 디바이스 이름.
                // 전역을 빈 칸으로 내보내면 왕복에서 '아직 안 묶음' 과 뭉개진다.
                cells.Add(Esc(r.Device is null ? string.Empty : (r.Device.Length == 0 ? CsvDeviceGlobal : r.Device)));
            }
            sb.AppendLine(string.Join(",", cells));
        }
        if (!any && includeExample)
        {
            if (monitor)
            {
                // 모니터링TAG 는 매칭 조건·기준값을 쓰지 않는다(값이 바뀌면 기록). 칸은 양식 호환을 위해 남기되 비운다.
                sb.AppendLine(string.Join(",", "", "예시_펌프압력", LevelMonitor, "D200", "Real", "", "", "bar", "0.5", "1000"));
                sb.AppendLine(string.Join(",", "", "예시_생산카운터", LevelMonitor, "D100", "Word", "", "", "ea", "", ""));
            }
            else
            {
                // 디바이스 칸: 이름 = 그 디바이스 · "*" = 전역(라인 전체 신호) · 빈 칸 = 미지정(지표 제외).
                sb.AppendLine(string.Join(",", "", "예시_모터과부하", LevelAlarm, "M901", "Bit", "RisingEdge", "", "Conveyor1"));
                sb.AppendLine(string.Join(",", "", "예시_비상정지", LevelAlarm, "M950", "Bit", "RisingEdge", "", CsvDeviceGlobal));
                sb.AppendLine(string.Join(",", "", "예시_고온경보", LevelAlarm, "D100", "Word", "Gte", "1000", ""));
            }
        }
        // Encoding.GetBytes 는 BOM(preamble)을 붙이지 않는다 — UTF8Encoding(true) 를 넘겨도 마찬가지다.
        // 직접 붙이지 않으면 한글 Windows 의 Excel 이 CP949 로 열어 헤더부터 깨진다.
        var enc = new UTF8Encoding(true);
        return [.. enc.GetPreamble(), .. enc.GetBytes(sb.ToString())];
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
    public static UtCsvParseResult ParseCsv(byte[] bytes, string? targetLevel = null)
    {
        // 탭에서 가져오면 그 탭의 레벨로 귀속시킨다(targetLevel). 지정이 없으면 파일에 적힌 레벨을 따른다.
        // 거부하지 않고 맞추되 LevelAdjusted 로 알린다 — 다른 탭 파일을 잘못 넣었을 때 조용히 사라지는 것보다 낫다.
        var forced = string.IsNullOrWhiteSpace(targetLevel) ? null : NormalizeLevel(targetLevel);
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
            var csvLevel = NormalizeLevel(Cell(off + 1));
            var addr = Cell(off + 2);
            var vt = Cell(off + 3);
            var op = Cell(off + 4);
            var mv = Cell(off + 5);
            // 8열은 종류마다 뜻이 다르다 — 모니터링 양식은 단위, 이상알람 양식은 디바이스. 양식이 탭별이라 섞이지 않는다.
            // 없는 파일(Promaker 6컬럼)은 그냥 빈 값이 된다.
            var deviceCell = Cell(off + 6);
            var unit = Cell(off + 6);
            var deadbandCell = Cell(off + 7);
            var intervalCell = Cell(off + 8);
            var lv = forced ?? csvLevel;
            var adjusted = forced is not null && !string.Equals(csvLevel, forced, StringComparison.Ordinal);
            if (p.Count - off < 3)
            {
                rows.Add(new UtCsvRowDto(i + 1, sys, name, addr, vt, op, mv,
                    "컬럼이 부족합니다(최소: 이름, 로그 레벨, 태그 주소).", lv, adjusted));
                continue;
            }
            var (entry, err) = Normalize(name, addr, string.IsNullOrWhiteSpace(vt) ? "Bit" : vt, op, mv, lv);
            if (entry is null)
            {
                rows.Add(new UtCsvRowDto(i + 1, sys, name, addr, vt, op, mv, err, lv, adjusted));
                continue;
            }

            // 메타는 모니터링TAG 행에만 싣는다. 숫자가 아니면 그 칸만 버리고 행은 살린다 —
            // 표시·감량 설정 하나 때문에 태그 등록 전체를 막을 이유가 없다.
            double? deadband = null;
            int? interval = null;
            if (entry.Level == LevelMonitor)
            {
                if (double.TryParse(deadbandCell, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && db > 0)
                    deadband = db;
                if (int.TryParse(intervalCell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv) && iv > 0)
                    interval = iv;
            }
            var metaErr = entry.Level == LevelMonitor ? ValidateMonitorMeta(deadband, interval) : null;
            // 귀속은 이상알람TAG 행에만 싣는다 — 모니터링 행에서 8열은 단위다. 이름이 모델에 없는 디바이스라도
            // 여기서 막지 않는다(유령 귀속은 화면이 경고로 다루고, 가져오기가 태그 등록 자체를 막을 이유가 없다).
            var device = entry.Level == LevelMonitor ? null : ParseCsvDevice(deviceCell);
            rows.Add(new UtCsvRowDto(i + 1, sys, entry.Name, entry.TagAddress, entry.ValueType, entry.MatchOp,
                entry.MatchValue, metaErr, entry.Level, adjusted,
                entry.Level == LevelMonitor && unit.Length > 0 ? unit : null, deadband, interval, device));
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
