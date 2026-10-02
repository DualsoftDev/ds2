// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using ClosedXML.Excel;
using DSPilot.Models.UserTagAlerts;
using static DSPilot.Kpi.ErrorTagReliability;

namespace DSPilot.Services;

/// <summary>
/// 이상·알람(UserTag/Abnormal) Excel(.xlsx) 내보내기 — /api/user-tags/excel.
/// 시트 1 '알람' 은 현재 필터(기간·검색·System·구분·설비)로 조회한 알림 행, 시트 2 '디바이스별' 과 시트 3
/// '에러코드 매핑' 은 등록 에러 태그 축의 고장 지표(doc/31)로 기간·System 만 따른다.
/// </summary>
public static class UserTagAlertExcelExporter
{
    public const string XlsxMimeType = ExcelExporterBase.XlsxMimeType;

    /// <summary>
    /// 시트 3장 — 알람(원본 + 판정) · 디바이스별(eMTBF·eMTTR) · 에러코드 매핑. 지표는 2026-09-29 부터 화면에서
    /// 빼고 파일에서만 낸다(doc/31 §8) — 고객 요구가 "우리 에러코드 기준의 값을 파일로" 였고, 화면에서는 OEE 의
    /// MTBF(비가동 기준)와 혼동을 남겼다.
    /// </summary>
    /// <param name="reliability">디바이스별 고장 지표. null 이면 알람 시트만 낸다(계산 실패·프로젝트 미로드).</param>
    /// <param name="system">System 스코프 — 기간 외에 지표 시트가 따르는 유일한 필터.</param>
    /// <param name="listFiltered">알람 시트에 검색·구분·설비 필터가 걸렸는지. 지표 시트는 그 필터를 받지 않으므로 부제가 밝힌다.</param>
    public static byte[] Build(
        IReadOnlyList<UserTagAlertRecord> rows, DateTime periodStartLocal, DateTime periodEndLocal, string? flow,
        ErrorTagReliabilityService.Result? reliability = null, string? system = null, bool listFiltered = false)
    {
        using var workbook = new XLWorkbook();
        var period = $"기간 {periodStartLocal:yyyy-MM-dd HH:mm} ~ {periodEndLocal:yyyy-MM-dd HH:mm}";
        var verdicts = IndexVerdicts(reliability);

        BuildAlarmSheet(workbook, rows, period, flow, reliability is not null, verdicts);
        if (reliability is not null)
        {
            BuildDeviceSheet(workbook, reliability, period, system, listFiltered);
            BuildMappingSheet(workbook, reliability, period, system);
        }
        return ExcelExporterBase.SaveToBytes(workbook);
    }

    // ── 시트 1: 알람 ─────────────────────────────────────────────────────────

    private static void BuildAlarmSheet(
        XLWorkbook workbook, IReadOnlyList<UserTagAlertRecord> rows, string period, string? flow,
        bool hasReliability, IReadOnlyDictionary<string, ErrorTagReliabilityService.AlertVerdict> verdicts)
    {
        var ws = workbook.Worksheets.Add("알람");
        const int lastCol = 17;

        var titleText = "이상·알람 조회" + (string.IsNullOrWhiteSpace(flow) ? "" : $" · 설비 {flow} (자동감지만)");
        ExcelExporterBase.ApplyTitleRow(ws, 1, titleText, lastCol, 22);
        // 정지(초)의 기준을 파일 안에 적는다 — 템플릿(Equipment Availability Sheet)의 MTTR 은 수리시간이고 이 값은
        // 발생→재가동 달력시간이라, 기준이 안 보이면 밤새 래치된 알람의 14시간을 수리 14시간으로 읽는다.
        ExcelExporterBase.ApplySubtitleRow(ws, 2,
            $"{period}  ·  {rows.Count:N0} 건"
            + (hasReliability ? "  ·  정지(초) = 사건 발생 → 재가동, 달력시간 그대로(비생산 차감 없음) · 사건 번호가 같으면 같은 정지에서 울린 알람" : ""),
            lastCol);

        const int headerRow = 4;
        // 상태·해소 시각·지속(초) = 알람이 풀린 기록(Bit 1→0 등). 화면 목록의 "해소" 칸과 같은 원본(clearedAt).
        // 디바이스·재가동·정지·판정 = 이상알람TAG 축의 회복 판정(doc/31) — 디바이스가 묶인 행에만 값이 있다.
        // 자동감지(Abnormal)는 점 이벤트라 해소 개념이 없어 '—' 로 비운다.
        ExcelExporterBase.ApplyHeaderRow(ws, headerRow,
            ["시각", "레벨", "구분", "System", "이름", "경로(주소)", "조건", "매칭값", "실제값",
             "상태", "해소 시각", "지속(초)",
             "디바이스", "재가동 시각", "정지(초)", "판정", "사건 번호"]);

        int row = headerRow + 1;
        foreach (var a in rows)
        {
            var isAbn = string.Equals(a.ValueType, "Abnormal", StringComparison.Ordinal);
            ws.Cell(row, 1).Value = a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(row, 2).Value = a.LogLevel;
            ws.Cell(row, 3).Value = isAbn ? "자동감지" : "이상알람TAG";
            ws.Cell(row, 4).Value = a.SystemName;
            ws.Cell(row, 5).Value = a.Name;
            ws.Cell(row, 6).Value = a.TagAddress;
            ws.Cell(row, 7).Value = a.MatchOp;
            ws.Cell(row, 8).Value = a.MatchValue ?? "";
            ws.Cell(row, 9).Value = a.ActualValue;

            if (isAbn)
            {
                ws.Cell(row, 10).Value = "—";
                ws.Cell(row, 11).Value = "—";
            }
            else if (a.ClearedAt is { } clr)
            {
                ws.Cell(row, 10).Value = "해소";
                ws.Cell(row, 11).Value = clr.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                if (clr > a.OccurredAt)
                {
                    ws.Cell(row, 12).Value = Math.Round((clr - a.OccurredAt).TotalSeconds, 1);
                    ws.Cell(row, 12).Style.NumberFormat.Format = "0.0";
                }
            }
            else
            {
                ws.Cell(row, 10).Value = "진행 중";
                ws.Cell(row, 11).Value = "";
            }

            // 회복 판정 — 집계에서 빠진 건은 빠진 이유가 먼저다. '복구 완료' 로 보이면 설비가 선 적 없는
            // 경고를 고장으로 읽게 된다(doc/31 §8).
            if (isAbn)
            {
                ws.Cell(row, 13).Value = "—";
                ws.Cell(row, 14).Value = "—";
                ws.Cell(row, 16).Value = "—";
                ws.Cell(row, 17).Value = "—";
            }
            else if (verdicts.TryGetValue(VerdictKey(a.OccurredAt, a.Endpoint, a.TagAddress), out var v))
            {
                ws.Cell(row, 13).Value = v.Device;
                ws.Cell(row, 14).Value = v.RestartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                if (v.Skip == SkipCause.None && v.RepairMs is { } rep)
                {
                    ws.Cell(row, 15).Value = Math.Round(rep / 1000.0, 1);
                    ws.Cell(row, 15).Style.NumberFormat.Format = "0.0";
                }
                ws.Cell(row, 16).Value = v.Skip == SkipCause.None ? StateLabel(v.State) : SkipLabel(v.Skip);
                // 사건 번호 — 같은 정지에 묶인 알람들이 같은 번호를 받는다(doc/31 §2.1). "알람 73줄인데 고장 N건" 을 줄 단위로 추적하는 열.
                if (v.EventNo > 0) ws.Cell(row, 17).Value = v.EventNo;
            }
            else if (hasReliability)
            {
                // 판정이 없는 이상알람TAG 행 — 접속 정보가 없는 옛 행(2026-09-22 이전)이거나 디바이스가 안 묶인 태그.
                ws.Cell(row, 13).Value = "";
                ws.Cell(row, 16).Value = string.IsNullOrWhiteSpace(a.Endpoint) ? "구 데이터(접속 정보 없음)" : "디바이스 미지정";
            }
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        double[] widths = [20, 8, 10, 16, 24, 28, 16, 14, 16, 10, 20, 10, 18, 20, 10, 16, 10];
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ExcelExporterBase.FreezeAndFooter(ws, headerRow);
    }

    // ── 시트 2: 디바이스별 ────────────────────────────────────────────────────

    private static void BuildDeviceSheet(
        XLWorkbook workbook, ErrorTagReliabilityService.Result rel, string period, string? system, bool listFiltered)
    {
        var ws = workbook.Worksheets.Add("디바이스별");
        const int lastCol = 15;
        var s = rel.Summary;

        // 열 이름에 근거를 붙인다(doc/30 §7.1) — OEE 의 MTBF/MTTR(비가동 기준)과 한 문서에 들어갈 수 있는 자리다.
        ExcelExporterBase.ApplyTitleRow(ws, 1, "디바이스별 고장 지표 — 등록 에러태그 기준 (eMTBF · eMTTR)", lastCol, 22);
        ExcelExporterBase.ApplySubtitleRow(ws, 2,
            $"{period}  ·  System {(string.IsNullOrWhiteSpace(system) ? "전체" : system)}"
            + $"  ·  집계 대상 고장 {s.FaultCount:N0}건 · 복구 완료 {s.RecoveredCount:N0}건"
            + (rel.ProjectLoaded ? "" : "  ·  프로젝트 미로드(지표 없음)"),
            lastCol);
        // 알람 시트와 모집단이 다르다 — 빠진 것을 세어 보여야 "알람 500건인데 고장 30건" 이 설명된다.
        ExcelExporterBase.ApplySubtitleRow(ws, 3,
            $"제외 — 무정지 경고 {s.NonStopWarningCount:N0} · 재접속 스냅샷 {s.LinkSnapshotCount:N0} · 판정 불가 {s.UnknownStopCount:N0}"
            + $" · 값 변경 조건 {rel.SkippedChangedCount:N0} · 구 데이터(접속 정보 없음) {rel.LegacyAlertCount:N0}"
            + $" · 디바이스 미지정 태그 {rel.UnboundTagCount:N0}개"
            + (listFiltered ? "  ·  알람 시트의 검색·구분·설비 필터는 이 시트에 적용되지 않습니다(기간·System 만)" : ""),
            lastCol);

        // 식과 기준을 파일 안에 적는다 — 고객 템플릿(Equipment Availability Sheet)이 같은 식(Availability = MTTF/(MTTF+MTTR),
        // Failure Rate = 1/MTTF)을 쓰므로 열을 그대로 옮겨 붙일 수 있게 하되, eMTTR 이 수리시간이 아니라 발생→재가동
        // 달력시간이라는 사실은 숨기지 않는다. 연산은 그대로고 파생 표기만 더한 것이다(2026-10-02).
        ExcelExporterBase.ApplySubtitleRow(ws, 4,
            "eMTBF = 가동시간 ÷ 고장 · eMTTR = Σ(발생→재가동) ÷ 복구 완료, 달력시간 그대로(비생산 차감 없음)"
            + " · Availability = eMTBF ÷ (eMTBF + eMTTR) · Failure Rate = 1 ÷ eMTBF(분)",
            lastCol);

        const int headerRow = 5;
        ExcelExporterBase.ApplyHeaderRow(ws, headerRow,
            ["구분", "이름", "System", "묶인 에러코드", "고장", "복구 완료", "무정지 경고", "진행 중", "재가동 미확인",
             "가동시간(분)", "eMTBF(분)", "eMTTR(분)", "총 정지(분)", "Availability(%)", "Failure Rate(1/분)"]);

        var tagsByDevice = BoundTagsOf(rel, system)
            .GroupBy(t => (t.System, t.Device))
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(TagLabel)));

        int row = headerRow + 1;
        var seen = new HashSet<(string, string)>();
        foreach (var d in rel.Devices)
        {
            seen.Add((d.System, d.Device));
            ws.Cell(row, 1).Value = "디바이스";
            ws.Cell(row, 2).Value = d.Device;
            ws.Cell(row, 3).Value = d.System;
            ws.Cell(row, 4).Value = tagsByDevice.TryGetValue((d.System, d.Device), out var tags) ? tags : "";
            ws.Cell(row, 5).Value = d.FaultCount;
            ws.Cell(row, 6).Value = d.RecoveredCount;
            ws.Cell(row, 7).Value = d.NonStopWarningCount;
            ws.Cell(row, 8).Value = d.InProgressCount;
            ws.Cell(row, 9).Value = d.RestartUnconfirmedCount;
            SetMinutes(ws.Cell(row, 10), d.OperatingMs);
            SetMetric(ws.Cell(row, 11), d.EMtbfMs, d.FaultCount);
            SetMetric(ws.Cell(row, 12), d.EMttrMs, d.RecoveredCount);
            SetMinutes(ws.Cell(row, 13), d.TotalDownMs);
            SetAvailability(ws.Cell(row, 14), d.EMtbfMs, d.EMttrMs);
            SetFailureRate(ws.Cell(row, 15), d.EMtbfMs);
            row++;
        }
        // 묶였지만 기간 안에 알람이 없던 디바이스 — "이 설비는 이 기간 고장이 없었다" 도 정보다.
        foreach (var (key, tags) in tagsByDevice.OrderBy(kv => kv.Key.System, StringComparer.OrdinalIgnoreCase)
                                                .ThenBy(kv => kv.Key.Device, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(key)) continue;
            ws.Cell(row, 1).Value = "디바이스";
            ws.Cell(row, 2).Value = key.Device;
            ws.Cell(row, 3).Value = key.System;
            ws.Cell(row, 4).Value = tags;
            for (var c = 5; c <= 9; c++) ws.Cell(row, c).Value = 0;
            for (var c = 10; c <= 15; c++) ws.Cell(row, c).Value = "—";
            row++;
        }

        // 합산 행 — 설비(flow) · PLC(System) · 전체. 설비 행의 합은 전체보다 클 수 있다(공유 디바이스, doc/31 §4.1).
        row++;
        foreach (var f in rel.Flows) row = ScopeRow(ws, row, "설비", f.Name, "", f.Totals);
        foreach (var p in rel.Systems) row = ScopeRow(ws, row, "PLC", p.Name, p.Name, p.Totals);
        ScopeRow(ws, row, "전체", "전체", "", s);

        double[] widths = [10, 24, 16, 48, 8, 10, 11, 9, 13, 13, 13, 13, 12, 15, 17];
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ExcelExporterBase.FreezeAndFooter(ws, headerRow);
    }

    private static int ScopeRow(IXLWorksheet ws, int row, string kind, string name, string system, Summary t)
    {
        ws.Cell(row, 1).Value = kind;
        ws.Cell(row, 2).Value = name;
        ws.Cell(row, 3).Value = system;
        ws.Cell(row, 5).Value = t.FaultCount;
        ws.Cell(row, 6).Value = t.RecoveredCount;
        ws.Cell(row, 7).Value = t.NonStopWarningCount;
        ws.Cell(row, 8).Value = t.InProgressCount;
        ws.Cell(row, 9).Value = t.RestartUnconfirmedCount;
        SetMinutes(ws.Cell(row, 10), t.OperatingMs);
        SetMetric(ws.Cell(row, 11), t.EMtbfMs, t.FaultCount);
        SetMetric(ws.Cell(row, 12), t.EMttrMs, t.RecoveredCount);
        SetMinutes(ws.Cell(row, 13), t.TotalDownMs);
        SetAvailability(ws.Cell(row, 14), t.EMtbfMs, t.EMttrMs);
        SetFailureRate(ws.Cell(row, 15), t.EMtbfMs);
        ws.Row(row).Style.Font.Bold = true;
        return row + 1;
    }

    // ── 시트 3: 에러코드 매핑 ─────────────────────────────────────────────────

    private static void BuildMappingSheet(
        XLWorkbook workbook, ErrorTagReliabilityService.Result rel, string period, string? system)
    {
        var ws = workbook.Worksheets.Add("에러코드 매핑");
        const int lastCol = 10;
        var tags = BoundTagsOf(rel, system);

        ExcelExporterBase.ApplyTitleRow(ws, 1, "에러코드 → 디바이스 매핑 — 설정 ▸ 사용자 태그에서 묶은 것", lastCol, 22);
        ExcelExporterBase.ApplySubtitleRow(ws, 2,
            $"{period}  ·  System {(string.IsNullOrWhiteSpace(system) ? "전체" : system)}  ·  매핑 {tags.Count:N0}건  ·  건수는 조회 기간 안의 이 에러코드 알람",
            lastCol);

        const int headerRow = 4;
        ExcelExporterBase.ApplyHeaderRow(ws, headerRow,
            ["System", "디바이스", "에러코드", "주소", "PLC 접속", "발생", "해소", "진행 중", "복구 완료", "무정지 경고"]);

        // 태그별 건수 — 키는 (엔드포인트, 주소)(doc/31 §6). 여기서만 태그 단위로 센다: 건수는 태그의 것이고
        // 지표(eMTBF·eMTTR)는 디바이스의 것이다(§2.1) — 태그별 eMTBF 는 만들지 않는다.
        var byTag = rel.Alerts
            .GroupBy(a => (a.Endpoint, a.TagAddress), TagKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => (
                Occurred: g.Count(),
                Cleared: g.Count(a => a.ClearedAtUtc is not null),
                Open: g.Count(a => a.ClearedAtUtc is null),
                Recovered: g.Count(a => a.Skip == SkipCause.None && a.State == RecoveryState.Recovered),
                Warned: g.Count(a => a.Skip == SkipCause.NonStopWarning)),
                TagKeyComparer.Instance);

        int row = headerRow + 1;
        foreach (var t in tags)
        {
            byTag.TryGetValue((t.Endpoint, t.TagAddress), out var n);
            ws.Cell(row, 1).Value = t.System;
            ws.Cell(row, 2).Value = t.Device;
            ws.Cell(row, 3).Value = t.Name.Length > 0 ? t.Name : "(정의 없음)";
            ws.Cell(row, 4).Value = t.TagAddress;
            ws.Cell(row, 5).Value = t.Endpoint;
            ws.Cell(row, 6).Value = n.Occurred;
            ws.Cell(row, 7).Value = n.Cleared;
            ws.Cell(row, 8).Value = n.Open;
            ws.Cell(row, 9).Value = n.Recovered;
            ws.Cell(row, 10).Value = n.Warned;
            row++;
        }

        double[] widths = [16, 24, 28, 22, 18, 8, 8, 9, 10, 11];
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ExcelExporterBase.FreezeAndFooter(ws, headerRow);
    }

    // ── 공통 ─────────────────────────────────────────────────────────────────

    private static List<ErrorTagReliabilityService.BoundTag> BoundTagsOf(ErrorTagReliabilityService.Result rel, string? system) =>
        string.IsNullOrWhiteSpace(system)
            ? rel.BoundTags
            : rel.BoundTags.Where(t => string.Equals(t.System, system.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    private static string TagLabel(ErrorTagReliabilityService.BoundTag t) =>
        t.Name.Length > 0 ? $"{t.Name} ({t.TagAddress})" : t.TagAddress;

    /// <summary>
    /// 알람 원본 행 ↔ 판정 행의 조인 키 = (시각, 엔드포인트, 주소). 같은 조회 경로에서 읽은 같은 시각이라 Ticks 가
    /// 일치한다. System 이름이 아니라 엔드포인트인 이유 — 접속 정보가 없는 옛 행은 판정이 없는데, 이름으로 이으면
    /// 같은 시각·주소의 새 행 판정이 옛 행에 잘못 붙는다(doc/31 §6: 신호의 정체는 (엔드포인트, 주소)).
    /// </summary>
    private static string VerdictKey(DateTime occurredAt, string? endpoint, string? address) =>
        $"{occurredAt.Ticks}|{(endpoint ?? "").Trim()}|{address ?? ""}";

    private static Dictionary<string, ErrorTagReliabilityService.AlertVerdict> IndexVerdicts(ErrorTagReliabilityService.Result? rel)
    {
        var map = new Dictionary<string, ErrorTagReliabilityService.AlertVerdict>(StringComparer.Ordinal);
        if (rel is null) return map;
        foreach (var v in rel.Alerts)
            map.TryAdd(VerdictKey(v.OccurredAtUtc, v.Endpoint, v.TagAddress), v);
        return map;
    }

    private static void SetMinutes(IXLCell cell, long ms)
    {
        if (ms <= 0) { cell.Value = "—"; return; }
        cell.Value = Math.Round(ms / 60000.0, 1);
        cell.Style.NumberFormat.Format = "0.0";
    }

    /// <summary>표본 미달이면 숫자 대신 근거 — 0 이나 빈칸으로 두면 "고장이 없다" 로 읽힌다.</summary>
    private static void SetMetric(IXLCell cell, double? ms, int n)
    {
        if (ms is null) { cell.Value = $"표본 부족 (n={n})"; return; }
        cell.Value = Math.Round(ms.Value / 60000.0, 1);
        cell.Style.NumberFormat.Format = "0.0";
    }

    /// <summary>
    /// Availability(%) = eMTBF ÷ (eMTBF + eMTTR) — 고객 템플릿의 식 그대로. 둘 중 하나라도 표본 미달이면 비운다
    /// (한쪽만 있는 값으로 가용성을 만들면 "고장은 있는데 100%" 같은 가짜가 나온다).
    /// </summary>
    private static void SetAvailability(IXLCell cell, double? mtbfMs, double? mttrMs)
    {
        if (mtbfMs is not double b || mttrMs is not double r || b + r <= 0) { cell.Value = "—"; return; }
        cell.Value = Math.Round(b / (b + r) * 100.0, 2);
        cell.Style.NumberFormat.Format = "0.00";
    }

    /// <summary>Failure Rate(1/분) = 1 ÷ eMTBF(분) — 템플릿의 λ. 표본 미달이면 비운다.</summary>
    private static void SetFailureRate(IXLCell cell, double? mtbfMs)
    {
        if (mtbfMs is not double b || b <= 0) { cell.Value = "—"; return; }
        cell.Value = 60000.0 / b;
        cell.Style.NumberFormat.Format = "0.000000";
    }

    private static string StateLabel(RecoveryState s) => s switch
    {
        RecoveryState.Recovered => "복구 완료",
        RecoveryState.RestartUnconfirmed => "재가동 미확인",
        RecoveryState.AwaitingRestart => "복구 확인 중",
        RecoveryState.InProgress => "진행 중",
        _ => s.ToString(),
    };

    private static string SkipLabel(SkipCause c) => c switch
    {
        SkipCause.NonStopWarning => "무정지 경고",
        SkipCause.LinkSnapshot => "재접속 스냅샷",
        SkipCause.ChangedOp => "값 변경 조건",
        SkipCause.UnknownStop => "판정 불가",
        _ => "",
    };

    private sealed class TagKeyComparer : IEqualityComparer<(string Endpoint, string TagAddress)>
    {
        public static readonly TagKeyComparer Instance = new();
        public bool Equals((string Endpoint, string TagAddress) a, (string Endpoint, string TagAddress) b) =>
            string.Equals(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.TagAddress, b.TagAddress, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Endpoint, string TagAddress) k) =>
            HashCode.Combine(k.Endpoint.ToUpperInvariant(), k.TagAddress.ToUpperInvariant());
    }
}
