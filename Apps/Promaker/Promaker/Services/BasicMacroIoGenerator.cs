using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// Basic Wizard 모드 — 매크로 + Flow 선두주소만으로 IoBatchRow 를 생성.
/// 이전엔 TagWizardBasicDialog.RefreshPreview 안에서 직접 수행했던 로직 추출.
/// 출력은 Advanced 와 동일한 IoBatchRow 로 통일해 적용 단계는 IoTagApplier 하나로 수렴.
/// </summary>
public static class BasicMacroIoGenerator
{
    /// <summary>Flow 단위 선두주소(워드 단위 정수).</summary>
    public sealed record FlowBase(string FlowName, int IwBase, int QwBase, int MwBase);

    public sealed record Input(string IwMacro, string QwMacro, string MwMacro, IReadOnlyList<FlowBase> FlowBases);

    public static List<IoBatchRow> Generate(DsStore store, Input input)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (input == null) throw new ArgumentNullException(nameof(input));

        var rows = new List<IoBatchRow>();
        var flowMap = new Dictionary<string, FlowBase>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in input.FlowBases) flowMap[b.FlowName] = b;

        var counter = new Dictionary<string, (int iw, int qw, int mw)>(StringComparer.OrdinalIgnoreCase);

        // Call 별 ApiName 등장 순서(nth) 추적 — SignalCounts 필터에 사용.
        var nthByCallApi = new Dictionary<(Guid callId, string api), int>();

        // ApiCall 을 Call 단위로 순회 — Call.ApiCalls 순서가 nth 의 단일 진실원.
        var orderedApiCalls = store.Calls.Values
            .SelectMany(c => c.ApiCalls.Select(ac => (call: c, apiCall: ac)));

        // ApiCall 복제 모드: 출력(QW) 은 Call+ApiName 단위로 단일 주소/심볼 공유 (캐시).
        var qwCacheByCallApi = new Dictionary<(Guid callId, string api), (string Addr, string Sym)>();

        foreach (var (parentCall, apiCall) in orderedApiCalls)
        {
            var ctx = ApiCallContextResolver.Resolve(store, apiCall);
            if (string.IsNullOrWhiteSpace(ctx.FlowName)) continue;

            // SignalCounts[ApiName] 적용 — Call-level 카운트 초과 ApiCall 은 skip.
            var key = (ctx.ParentCallId, ctx.ApiName ?? "");
            var nth = (nthByCallApi.TryGetValue(key, out var prev) ? prev : 0) + 1;
            nthByCallApi[key] = nth;
            if (TryGetSignalCount(store, ctx.ParentCallId, ctx.ApiName) is int max && nth > max)
                continue;

            var apiName = ctx.ApiName ?? "";

            // ApiCall 별 실제 device alias — ApiCall.Name = "{devAlias}.{apiName}" 에서 추출.
            // 추출 실패 시 parent Call 의 DevicesAlias 사용.
            var perApiDevice = ExtractDeviceFromApiCallName(apiCall.Name) ?? ctx.DeviceAlias ?? "";

            // IW 심볼: 매크로 + per-ApiCall device → 자연스럽게 nth 별로 다른 이름.
            var inSym = string.IsNullOrEmpty(input.IwMacro)
                ? "" : Expand(input.IwMacro, ctx.FlowName, perApiDevice, apiName);
            // QW 심볼: ApiCall 복제 모드는 같은 솔레노이드 → parent Call 의 DevicesAlias 사용 + 캐시.
            var qwSymBase = string.IsNullOrEmpty(input.QwMacro)
                ? "" : Expand(input.QwMacro, ctx.FlowName, ctx.DeviceAlias ?? "", apiName);

            string inAddr = "", outAddr = "", outSym = "";
            if (!counter.TryGetValue(ctx.FlowName, out var cnt)) cnt = (0, 0, 0);
            if (flowMap.TryGetValue(ctx.FlowName, out var fb))
            {
                if (!string.IsNullOrEmpty(inSym))
                {
                    inAddr = $"%IW{fb.IwBase}.{cnt.iw / 16}.{cnt.iw % 16}";
                    cnt.iw++;
                }
                if (!string.IsNullOrEmpty(qwSymBase))
                {
                    if (qwCacheByCallApi.TryGetValue(key, out var cached))
                    {
                        // 같은 (Call, ApiName) 의 후속 ApiCall — QW 주소/심볼 재사용, 카운터 증가 안 함.
                        outAddr = cached.Addr;
                        outSym  = cached.Sym;
                    }
                    else
                    {
                        outAddr = $"%QW{fb.QwBase}.{cnt.qw / 16}.{cnt.qw % 16}";
                        outSym  = qwSymBase;
                        cnt.qw++;
                        qwCacheByCallApi[key] = (outAddr, outSym);
                    }
                }
            }
            counter[ctx.FlowName] = cnt;

            rows.Add(new IoBatchRow(
                callId:     ctx.ParentCallId,
                apiCallId:  apiCall.Id,
                flow:       ctx.FlowName,
                work:       "",
                device:     perApiDevice,
                api:        apiName,
                inAddress:  inAddr,
                inSymbol:   inSym,
                outAddress: outAddr,
                outSymbol:  outSym));
        }

        return rows;
    }

    public static string Expand(string macro, string flow, string device, string api) =>
        (macro ?? "")
            .Replace("$(F)", flow   ?? "")
            .Replace("$(D)", device ?? "")
            .Replace("$(A)", api    ?? "");

    /// <summary>ApiCall.Name = "{devAlias}.{apiName}" 형식에서 devAlias 추출. '.' 없으면 null.</summary>
    private static string? ExtractDeviceFromApiCallName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var idx = name.IndexOf('.');
        return idx > 0 ? name.Substring(0, idx) : null;
    }

    /// <summary>Call.Properties → ControlCallProperties.SignalCounts[ApiName] 조회.</summary>
    private static int? TryGetSignalCount(DsStore store, Guid callId, string? apiName)
    {
        if (string.IsNullOrEmpty(apiName)) return null;
        if (!store.Calls.TryGetValue(callId, out var call)) return null;
        foreach (var p in call.Properties)
            if (p is CallSubmodelProperty.ControlCall cc
                && cc.Item.SignalCounts.TryGetValue(apiName, out var n))
                return n;
        return null;
    }
}
