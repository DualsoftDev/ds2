using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Plc.Xgi;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// IoListPipeline 의 GenerationResult 를 UI 표시용 IoBatchRow / DummySignalRow / UnmatchedSignalRow 로 변환.
/// 이전엔 TagWizardDialog.ConvertSignalsToRows / IoBatchCommands.BuildIoBatchRows / MakeSingleSignalRow
/// 세 곳에서 동일 로직이 따로 구현돼 있었다 — 단일 진입점으로 통합.
/// </summary>
public static class IoSignalPipeline
{
    /// <summary>
    /// SignalRecord → IoBatchRow 변환.
    /// store 가 주어지면 ApiCall 매칭 시도 (CallId/ApiDef 해석) → 없으면 이름만 채운 행.
    /// </summary>
    public static List<IoBatchRow> BuildIoBatchRows(GenerationResult result, DsStore? store = null)
    {
        var rows = new List<IoBatchRow>();

        // Api_None 신호: 신호 1개 = 행 1개 (API 컬럼 빈칸).
        foreach (var s in result.IoSignals.Where(IsApiNone))
            rows.Add(MakeApiNoneRow(s));

        // 그 외: ApiCallId+Flow+Work+Call+Device 그룹 안에서 IW + QW 1쌍.
        var grouped = result.IoSignals
            .Where(s => !IsApiNone(s))
            .GroupBy(s => new { s.ApiCallId, s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;
            var input  = group.FirstOrDefault(s => s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase));
            var output = group.FirstOrDefault(s => s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase));

            // store 가 있을 때만 ApiCall 해석 — 없으면 이름만 채운 단순 행.
            Guid callId = Guid.Empty;
            Guid apiCallId = key.ApiCallId;
            string device = (input ?? output)?.DeviceAlias ?? "";
            string api = key.DeviceName;

            if (store != null)
            {
                var matchedCall = Device.findCallByName(key.FlowName, key.WorkName, key.CallName, store)?.Value;
                callId = matchedCall?.Id ?? Guid.Empty;
                if (matchedCall != null)
                {
                    var matchedApiCall = apiCallId != Guid.Empty
                        ? matchedCall.ApiCalls.FirstOrDefault(ac => ac.Id == apiCallId)
                        : null;
                    if (matchedApiCall == null)
                    {
                        matchedApiCall = Device.findApiCallByDeviceName(matchedCall, key.DeviceName, store)?.Value;
                        apiCallId = matchedApiCall?.Id ?? apiCallId;
                    }
                    if (matchedApiCall?.ApiDefId?.Value is { } apiDefId)
                    {
                        var apiDef = Queries.getApiDef(apiDefId, store)?.Value;
                        if (apiDef != null)
                        {
                            api = apiDef.Name;
                            if (store.Systems.TryGetValue(apiDef.ParentId, out var system))
                                device = system.Name;
                        }
                    }
                }
            }

            rows.Add(new IoBatchRow(
                callId:     callId,
                apiCallId:  apiCallId,
                flow:       key.FlowName,
                work:       key.WorkName,
                device:     device,
                api:        api,
                inAddress:  input?.Address ?? "",
                inSymbol:   input?.VarName ?? "",
                outAddress: output?.Address ?? "",
                outSymbol:  output?.VarName ?? ""));
        }

        return rows;
    }

    /// <summary>
    /// Api_None 신호만 추출 → IoBatchRow. (ApiCall 미바인딩 슬롯 → store 에 저장되지 않음.
    /// IO 조회에서 store 데이터에 더해 표시하기 위함.)
    /// </summary>
    public static List<IoBatchRow> BuildApiNoneRows(GenerationResult result)
    {
        var rows = new List<IoBatchRow>();
        foreach (var s in result.IoSignals.Where(IsApiNone))
            rows.Add(MakeApiNoneRow(s));
        return rows;
    }

    /// <summary>DummySignal → DummySignalRow 변환.</summary>
    public static List<DummySignalRow> BuildDummyRows(GenerationResult result) =>
        result.DummySignals
            .Select(s => new DummySignalRow(
                Flow: s.FlowName, Work: s.WorkName, Call: s.CallName,
                Symbol: s.VarName, Address: s.Address, Type: s.IoType))
            .ToList();

    /// <summary>
    /// 매칭 검증 — Call/ApiCall ID 가 비어 있는 행을 UnmatchedSignalRow 로 분류.
    /// Api_None 행(api 빈 문자열)은 의도적 미바인딩 → 제외.
    /// </summary>
    public static List<UnmatchedSignalRow> ClassifyUnmatched(IEnumerable<IoBatchRow> rows)
    {
        var list = new List<UnmatchedSignalRow>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Api)) continue;
            if (row.CallId != Guid.Empty && row.ApiCallId != Guid.Empty) continue;

            string reason = (row.CallId == Guid.Empty, row.ApiCallId == Guid.Empty) switch
            {
                (true, true)  => "Call 및 ApiCall을 찾을 수 없음",
                (true, false) => "Call을 찾을 수 없음",
                _             => "ApiCall(Device)을 찾을 수 없음",
            };
            list.Add(new UnmatchedSignalRow(
                Flow: row.Flow, Device: row.Device, Api: row.Api,
                OutSymbol: row.OutSymbol, OutAddress: row.OutAddress,
                InSymbol: row.InSymbol, InAddress: row.InAddress,
                FailureReason: reason));
        }
        return list;
    }

    private static bool IsApiNone(SignalRecord s) =>
        string.Equals(s.DeviceName, IoConstants.ApiNoneSentinel, StringComparison.OrdinalIgnoreCase);

    private static IoBatchRow MakeApiNoneRow(SignalRecord s)
    {
        bool isInput  = s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase);
        bool isOutput = s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase);
        return new IoBatchRow(
            callId:     Guid.Empty,
            apiCallId:  s.ApiCallId,
            flow:       s.FlowName,
            work:       s.WorkName,
            device:     s.DeviceAlias,
            api:        "",
            inAddress:  isInput  ? s.Address : "",
            inSymbol:   isInput  ? s.VarName : "",
            outAddress: isOutput ? s.Address : "",
            outSymbol:  isOutput ? s.VarName : "");
    }
}
