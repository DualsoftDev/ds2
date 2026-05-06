using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ds2.Core.Store;
using Ds2.Editor;
using Plc.Xgi;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// I/O 조회용 행 생성 — 두 출처를 병합한다:
///   (1) store 의 진실: ApiCall.InTag/OutTag — Wizard Apply 결과 (Basic/Advanced 모두 일치)
///   (2) 파이프라인 보충: Api_None 신호 — 어떤 ApiCall 에도 바인딩되지 않아 store 에 저장되지 않으므로
///       IoListPipeline 를 한 번 돌려 Api_None 슬롯 결과만 별도 행으로 노출.
///
/// 결과에는 행 데이터 외에 사용자에게 보여줄 진단(<see cref="DiagnosticItem"/>) 리스트가 포함된다.
/// 원시 파이프라인/예외 메시지는 사용자 친화적인 한글 설명·조치 안내로 변환되어 들어간다.
/// </summary>
public static class IoQueryService
{
    public sealed record QueryResult(
        IReadOnlyList<IoBatchRow> Rows,
        IReadOnlyList<UnmatchedSignalRow> Unmatched,
        IReadOnlyList<DiagnosticItem> Diagnostics)
    {
        public int ErrorCount   { get; } = CountBy(Diagnostics, DiagnosticSeverity.Error);
        public int WarningCount { get; } = CountBy(Diagnostics, DiagnosticSeverity.Warning);

        public bool HasError    => ErrorCount   > 0;
        public bool HasWarning  => WarningCount > 0;

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
            {
                if (r.IsUnmatched) keys.Add(new IoRowKey(r.CallId, r.ApiCallId));
            }

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

        // (2) Api_None 보충 — 파이프라인 1회 실행. 실패해도 (1) 결과는 그대로 반환.
        try
        {
            using var tempDir = PresetToTempTemplateDir.Materialize(store);
            var result = IoListPipeline.generate(store, tempDir.Path);

            foreach (var err in result.Errors)
                diagnostics.Add(MapPipelineError(err, rows));

            // Api_None 행 추가 — DeviceAlias 가 비어있는 경우 "Api_None" 으로 표기.
            foreach (var row in IoSignalPipeline.BuildApiNoneRows(result))
            {
                if (string.IsNullOrWhiteSpace(row.Device))
                    rows.Add(WithDevicePlaceholder(row));
                else
                    rows.Add(row);
            }
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

        return new QueryResult(rows, unmatched, diagnostics);
    }

    /// <summary>Plc.Xgi 의 원시 에러를 사용자 친화적 진단으로 변환.</summary>
    private static DiagnosticItem MapPipelineError(GenerationError err, List<IoBatchRow> rows)
    {
        var msg = err.Message ?? "";

        // ErrorType 별 우선 매핑 — 가장 흔한 케이스부터.
        switch (err.ErrorType)
        {
            case ErrorType.TemplateNotFound:
                return new DiagnosticItem(
                    Severity:   DiagnosticSeverity.Error,
                    Code:       "TEMPLATE_NOT_FOUND",
                    Title:      "XGI 템플릿 파일 없음",
                    Detail:     "Preset 에 등록된 XGI 템플릿 파일을 찾을 수 없습니다. " +
                                "Preset 편집에서 템플릿 경로를 확인하거나 템플릿을 다시 첨부하세요.",
                    AffectedRows: Array.Empty<IoRowKey>(),
                    RawMessage: msg);

            case ErrorType.ApiDefNotInTemplate:
                return new DiagnosticItem(
                    Severity:   DiagnosticSeverity.Error,
                    Code:       "API_DEF_NOT_IN_TEMPLATE",
                    Title:      "ApiDef 가 템플릿에 정의되어 있지 않음",
                    Detail:     "사용 중인 ApiDef 가 현재 XGI 템플릿에 없습니다. " +
                                "템플릿 파일을 갱신하거나 해당 ApiDef 의 SystemType / Api 이름이 템플릿과 일치하는지 확인하세요.",
                    AffectedRows: TryMatchByName(msg, rows),
                    RawMessage: msg);
        }

        // 미분류 — 메시지 패턴으로 보충 매핑 시도.
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

        // 그 외 — 분류 안 됨. 최소 일반 오류 카드로 표시.
        return new DiagnosticItem(
            Severity:   DiagnosticSeverity.Error,
            Code:       "PIPELINE_GENERIC",
            Title:      "파이프라인 오류",
            Detail:     "신호 생성 파이프라인이 오류를 보고했습니다. 원본 메시지를 참고해 주세요.",
            AffectedRows: TryMatchByName(msg, rows),
            RawMessage: msg);
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

    /// <summary>Device 컬럼이 비어있을 때 "Api_None" 으로 표기 — 그리드에서 식별 가능하게.</summary>
    private static IoBatchRow WithDevicePlaceholder(IoBatchRow src) =>
        new IoBatchRow(
            callId: src.CallId, apiCallId: src.ApiCallId,
            flow: src.Flow, work: src.Work,
            device: IoConstants.ApiNoneSentinel,
            api: src.Api,
            inAddress: src.InAddress, inSymbol: src.InSymbol,
            outAddress: src.OutAddress, outSymbol: src.OutSymbol,
            outDataType: src.OutDataType, inDataType: src.InDataType);
}

/// <summary>진단 항목의 심각도.</summary>
public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>I/O 그리드 행 식별 키 — CallId/ApiCallId 페어.</summary>
public readonly record struct IoRowKey(Guid CallId, Guid ApiCallId);

/// <summary>
/// 사용자에게 보여줄 진단 항목.
/// 원시 메시지는 <see cref="RawMessage"/> 로 따로 보존하고,
/// <see cref="Title"/>/<see cref="Detail"/> 은 한글 설명·조치 안내로 정제된 형태.
/// <see cref="AffectedRows"/> 가 채워져 있으면 그리드에서 해당 행을 강조/점프할 수 있다.
/// </summary>
public sealed record DiagnosticItem(
    DiagnosticSeverity Severity,
    string Code,
    string Title,
    string Detail,
    IReadOnlyList<IoRowKey> AffectedRows,
    string? RawMessage);
