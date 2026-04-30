using System;
using System.Collections.Generic;
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
/// 이렇게 두면:
///   - Basic Wizard 매크로 적용 결과와 일치 (이전 _LS/_CMD 어긋남 해소)
///   - Advanced Wizard 의 Api_None 슬롯도 IO 조회에서 모두 보임
/// </summary>
public static class IoQueryService
{
    public sealed record QueryResult(
        IReadOnlyList<IoBatchRow> Rows,
        IReadOnlyList<UnmatchedSignalRow> Unmatched,
        IReadOnlyList<string> ErrorMessages)
    {
        public bool HasError => ErrorMessages.Count > 0;
    }

    public static QueryResult Generate(DsStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));

        var rows = new List<IoBatchRow>();
        var unmatched = new List<UnmatchedSignalRow>();
        var errors = new List<string>();

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
            errors.Add($"저장된 IO 태그 조회 실패: {ex.Message}");
        }

        // (2) 통합 파이프라인 — Api_None IW/QW + Dummy(MW) 모두 한 번에 emit.
        try
        {
            var bundle = IoSignalPipeline.GenerateAll(store);
            foreach (var msg in bundle.ErrorMessages)   errors.Add($"파이프라인: {msg}");
            foreach (var msg in bundle.WarningMessages) errors.Add($"파이프라인 경고: {msg}");

            // Api_None IW/QW (ApiCallId=Empty) 행 추가.
            foreach (var row in bundle.IoRows.Where(r => r.ApiCallId == Guid.Empty))
            {
                if (string.IsNullOrWhiteSpace(row.Device))
                    rows.Add(WithDevicePlaceholder(row));
                else
                    rows.Add(row);
            }

            // Dummy(MW) 신호 — Out* 컬럼에 매핑.
            foreach (var d in bundle.DummyRows)
            {
                rows.Add(new IoBatchRow(
                    callId:      Guid.Empty,
                    apiCallId:   Guid.Empty,
                    flow:        d.Flow,
                    work:        d.Work,
                    device:      string.IsNullOrWhiteSpace(d.Call) ? IoConstants.ApiNoneSentinel : d.Call,
                    api:         IoConstants.ApiNoneSentinel,
                    inAddress:   "",
                    inSymbol:    "",
                    outAddress:  d.Address ?? "",
                    outSymbol:   d.Symbol ?? "",
                    outDataType: string.IsNullOrEmpty(d.DataType) ? "BOOL" : d.DataType,
                    inDataType:  ""));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Api_None/Dummy 보충 실패: {ex.Message}");
        }

        return new QueryResult(rows, unmatched, errors);
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
