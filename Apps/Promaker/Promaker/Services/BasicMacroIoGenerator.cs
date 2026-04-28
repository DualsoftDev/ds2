using System;
using System.Collections.Generic;
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

        foreach (var apiCall in store.ApiCalls.Values)
        {
            var ctx = ApiCallContextResolver.Resolve(store, apiCall);
            if (string.IsNullOrWhiteSpace(ctx.FlowName)) continue;

            var inSym  = string.IsNullOrEmpty(input.IwMacro) ? "" : Expand(input.IwMacro,  ctx.FlowName, ctx.DeviceAlias, ctx.ApiName);
            var outSym = string.IsNullOrEmpty(input.QwMacro) ? "" : Expand(input.QwMacro,  ctx.FlowName, ctx.DeviceAlias, ctx.ApiName);

            string inAddr = "", outAddr = "";
            if (!counter.TryGetValue(ctx.FlowName, out var cnt)) cnt = (0, 0, 0);
            if (flowMap.TryGetValue(ctx.FlowName, out var fb))
            {
                if (!string.IsNullOrEmpty(inSym))
                {
                    inAddr = $"%IW{fb.IwBase}.{cnt.iw / 16}.{cnt.iw % 16}";
                    cnt.iw++;
                }
                if (!string.IsNullOrEmpty(outSym))
                {
                    outAddr = $"%QW{fb.QwBase}.{cnt.qw / 16}.{cnt.qw % 16}";
                    cnt.qw++;
                }
            }
            counter[ctx.FlowName] = cnt;

            rows.Add(new IoBatchRow(
                callId:     ctx.ParentCallId,
                apiCallId:  apiCall.Id,
                flow:       ctx.FlowName,
                work:       "",
                device:     ctx.DeviceAlias,
                api:        ctx.ApiName,
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
}
