using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AAStoPLC.TagWizard;
using Ds2.Core.Store;
using Ds2.Editor;
using Plc.Xgi;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// I/O 조회용 행 생성 — 두 출처를 병합한다:
///   (1) store 의 진실: ApiCall.InTag/OutTag — Wizard Apply 결과 (Basic/Advanced 모두 일치)
///   (2) 파이프라인 보충: Api_None 신호 — 어떤 ApiCall 에도 바인딩되지 않아 store 에 저장되지 않으므로
///       IoSignalPipeline 를 한 번 돌려 Api_None 슬롯 + Dummy(MW) 결과만 별도 행으로 노출.
///
/// 결과에는 행 데이터 외에 사용자에게 보여줄 진단(<see cref="DiagnosticItem"/>) 리스트가 포함된다.
/// 원시 파이프라인/예외 메시지는 사용자 친화적인 한글 설명·조치 안내로 변환되어 들어간다.
/// </summary>
public static class IoQueryService
{
    public sealed record QueryResult(
        IReadOnlyList<IoBatchRow> Rows,
        IReadOnlyList<DummySignalRow> DummyRows,
        IReadOnlyList<UnmatchedSignalRow> Unmatched,
        IReadOnlyList<DiagnosticItem> Diagnostics)
    {
        public int ErrorCount   { get; } = CountBy(Diagnostics, DiagnosticSeverity.Error);
        public int WarningCount { get; } = CountBy(Diagnostics, DiagnosticSeverity.Warning);

        public bool HasError    => ErrorCount   > 0;
        public bool HasWarning  => WarningCount > 0;

        // 호환용 — 옛 ErrorMessages 호출부.
        public IReadOnlyList<string> ErrorMessages =>
            Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                       .Select(d => string.IsNullOrEmpty(d.RawMessage) ? d.Title : d.RawMessage!)
                       .ToList();

        private static int CountBy(IReadOnlyList<DiagnosticItem> items, DiagnosticSeverity sev)
        {
            int n = 0;
            foreach (var d in items) if (d.Severity == sev) n++;
            return n;
        }
    }

    public static QueryResult Generate(DsStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));

        var rows = new List<IoBatchRow>();
        var dummyRows = new List<DummySignalRow>();
        var unmatched = new List<UnmatchedSignalRow>();
        var diagnostics = new List<DiagnosticItem>();

        // (1) 저장된 ApiCall IO 태그 — store 의 진실.
        try
        {
            foreach (var b in store.GetAllApiCallIORows())
            {
                bool isUnknown = string.Equals(b.DeviceName, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(b.ApiName, "UNKNOWN", StringComparison.OrdinalIgnoreCase);

                var row = new IoBatchRow(
                    callId:      b.CallId,
                    apiCallId:   b.ApiCallId,
                    flow:        b.FlowName,
                    work:        b.WorkName,
                    device:      b.DeviceName,
                    api:         b.ApiName,
                    inAddress:   b.InAddress  ?? "",
                    inSymbol:    b.InSymbol   ?? "",
                    outAddress:  b.OutAddress ?? "",
                    outSymbol:   b.OutSymbol  ?? "",
                    outDataType: b.OutDataType ?? "BOOL",
                    inDataType:  b.InDataType  ?? "BOOL");
                row.IsUnmatched = isUnknown;
                rows.Add(row);

                if (isUnknown)
                {
                    unmatched.Add(new UnmatchedSignalRow(
                        Flow: b.FlowName, Device: b.DeviceName, Api: b.ApiName,
                        OutSymbol: b.OutSymbol ?? "", OutAddress: b.OutAddress ?? "",
                        InSymbol: b.InSymbol ?? "",   InAddress: b.InAddress ?? "",
                        FailureReason: "ApiDef 미해석 — Wizard 에서 Api/Device 매핑 확인"));
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticItem(
                Severity:   DiagnosticSeverity.Error,
                Code:       "STORE_READ_FAILED",
                Title:      "내부 데이터 읽기 실패",
                Detail:     "저장된 ApiCall IO 태그를 읽는 중 예외가 발생했습니다. 새로고침을 시도하거나, " +
                            "반복되면 store 손상 가능성이 있으니 로그와 함께 보고해 주세요.",
                AffectedRows: Array.Empty<IoRowKey>(),
                RawMessage: ex.Message));
        }

        // 매칭 실패 행을 진단으로 합치기 — 그리드 강조 + 패널 안내 한 번에.
        if (unmatched.Count > 0)
        {
            var keys = new List<IoRowKey>();
            foreach (var r in rows)
                if (r.IsUnmatched) keys.Add(new IoRowKey(r.CallId, r.ApiCallId));

            diagnostics.Add(new DiagnosticItem(
                Severity:   DiagnosticSeverity.Warning,
                Code:       "WIZARD_MAPPING_INCOMPLETE",
                Title:      $"Wizard 매핑 미완료 ({unmatched.Count}건)",
                Detail:     "Device 또는 Api 가 'UNKNOWN' 상태입니다. " +
                            "TAG Wizard → Step 3 → '패턴 적용' 으로 매크로를 적용하면 매핑이 채워집니다.\n" +
                            "이미 적용했다면 ApiDef 별칭이 변경되었거나 Device 별칭 매핑이 누락되지 않았는지 확인하세요.",
                AffectedRows: keys,
                RawMessage: null));
        }

        // (2) 파이프라인 진단만 수집 — preset 패턴에서 파생되는 Api_None / Dummy 행은
        //     I/O 조회에 표시하지 않는다 (실제 Apply 된 IO 만 노출).
        try
        {
            var bundle = IoSignalPipeline.GenerateAll(store);

            // 같은 SystemType 의 TEMPLATE_NOT_FOUND 는 한 카드로 합치고, 나머지는 그대로.
            var seenTemplateMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var msg in bundle.ErrorMessages)
            {
                var item = MapPipelineErrorMessage(msg, rows, DiagnosticSeverity.Error);
                if (item.Code == "TEMPLATE_NOT_FOUND" && !string.IsNullOrEmpty(item.SystemType))
                {
                    if (!seenTemplateMissing.Add(item.SystemType!))
                        continue;
                }
                diagnostics.Add(item);
            }
            foreach (var msg in bundle.WarningMessages)
                diagnostics.Add(MapPipelineErrorMessage(msg, rows, DiagnosticSeverity.Warning));

            // Dummy 행 — preset 패턴이 만드는 파생 신호. I/O 조회의 별도 탭으로 노출.
            foreach (var d in bundle.DummyRows)
                dummyRows.Add(d);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticItem(
                Severity:   DiagnosticSeverity.Error,
                Code:       "PIPELINE_CRASH",
                Title:      "Api_None 보충 실패",
                Detail:     "어느 ApiCall 에도 바인딩되지 않은 신호(Api_None) 를 계산하는 도중 예외가 발생했습니다. " +
                            "신호 자체는 표시되지 않을 수 있으니 새로고침으로 재시도하세요.",
                AffectedRows: Array.Empty<IoRowKey>(),
                RawMessage: ex.Message));
        }

        return new QueryResult(rows, dummyRows, unmatched, diagnostics);
    }

    /// <summary>파이프라인 원시 에러 메시지를 사용자 친화적 진단으로 변환.</summary>
    private static DiagnosticItem MapPipelineErrorMessage(string msg, List<IoBatchRow> rows, DiagnosticSeverity defaultSeverity)
    {
        msg ??= "";

        if (Regex.IsMatch(msg, @"template.*not.*found|템플릿.*없음|preset.*없음", RegexOptions.IgnoreCase))
        {
            var sysType = ExtractSystemType(msg);
            var titleSuffix = string.IsNullOrEmpty(sysType) ? "" : $" — SystemType '{sysType}'";
            var detail = string.IsNullOrEmpty(sysType)
                ? "사용 중인 SystemType 의 태그 패턴이 어디에도 등록되어 있지 않습니다. " +
                  "TAG Wizard → Step 2 (신호 템플릿) 에서 해당 SystemType 의 IW/QW/MW 패턴을 등록하세요."
                : $"SystemType '{sysType}' 의 태그 패턴이 어디에도 등록되어 있지 않습니다. " +
                  "TAG Wizard → Step 2 (신호 템플릿) 에서 IW/QW/MW 패턴을 등록하세요.";
            return new DiagnosticItem(
                Severity:   DiagnosticSeverity.Error,
                Code:       "TEMPLATE_NOT_FOUND",
                Title:      "신호 템플릿 누락" + titleSuffix,
                Detail:     detail,
                AffectedRows: Array.Empty<IoRowKey>(),
                RawMessage: msg,
                SystemType: sysType);
        }

        if (Regex.IsMatch(msg, "address.*pool|pool.*exhaust|주소.*풀", RegexOptions.IgnoreCase))
        {
            return new DiagnosticItem(
                Severity:   DiagnosticSeverity.Error,
                Code:       "ADDRESS_POOL_FULL",
                Title:      "IO 주소 풀이 부족합니다",
                Detail:     "사용 가능한 IO 주소(IW/QW)가 모자랍니다. " +
                            "Preset 의 시작 주소나 워드 개수를 늘려서 풀을 확장하세요.",
                AffectedRows: Array.Empty<IoRowKey>(),
                RawMessage: msg);
        }

        if (Regex.IsMatch(msg, "alias.*colli|중복.*별칭|별칭.*중복", RegexOptions.IgnoreCase))
        {
            return new DiagnosticItem(
                Severity:   DiagnosticSeverity.Warning,
                Code:       "DEVICE_ALIAS_COLLISION",
                Title:      "Device 별칭 충돌",
                Detail:     "같은 별칭을 사용하는 Device 가 둘 이상 있어 신호 매핑이 모호합니다. " +
                            "Preset 또는 모델에서 별칭이 유일하도록 조정하세요.",
                AffectedRows: TryMatchByName(msg, rows),
                RawMessage: msg);
        }

        return new DiagnosticItem(
            Severity:   defaultSeverity,
            Code:       "PIPELINE_GENERIC",
            Title:      defaultSeverity == DiagnosticSeverity.Warning ? "파이프라인 경고" : "파이프라인 오류",
            Detail:     "신호 생성 파이프라인이 메시지를 보고했습니다. 원본 메시지를 참고해 주세요.",
            AffectedRows: TryMatchByName(msg, rows),
            RawMessage: msg);
    }

    /// <summary>"Template not found for SystemType: Conveyor" 같은 메시지에서 SystemType 토큰 추출.</summary>
    private static string? ExtractSystemType(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = Regex.Match(message, @"SystemType\s*[:=]\s*([^\s,;)\]]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var token = m.Groups[1].Value.Trim().Trim('\'', '"');
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>메시지에 등장하는 Device/Api 이름과 일치하는 행을 best-effort 로 추출.</summary>
    private static IReadOnlyList<IoRowKey> TryMatchByName(string message, List<IoBatchRow> rows)
    {
        if (string.IsNullOrWhiteSpace(message) || rows.Count == 0)
            return Array.Empty<IoRowKey>();

        var keys = new List<IoRowKey>();
        var seen = new HashSet<(Guid, Guid)>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Device) && string.IsNullOrEmpty(r.Api))
                continue;

            bool hit =
                (!string.IsNullOrEmpty(r.Device) && message.IndexOf(r.Device, StringComparison.OrdinalIgnoreCase) >= 0)
             || (!string.IsNullOrEmpty(r.Api)    && message.IndexOf(r.Api,    StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hit) continue;

            if (seen.Add((r.CallId, r.ApiCallId)))
                keys.Add(new IoRowKey(r.CallId, r.ApiCallId));
        }
        return keys;
    }

}
