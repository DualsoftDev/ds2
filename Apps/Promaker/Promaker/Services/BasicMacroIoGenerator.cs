using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// Basic Wizard 모드 — 매크로 + Flow 선두주소만으로 IoBatchRow 를 생성.
/// 결정 로직(nth 추적, SignalCounts 필터, 매크로 expansion, 주소/심볼 계산, QW 캐시)은
/// F# <see cref="BasicMacroIo"/> 에 위임. C# 은 ApiCall 순회 + context 해석 + 결과 변환만 담당.
/// </summary>
public static class BasicMacroIoGenerator
{
    /// <summary>Flow 단위 선두주소(워드 단위 정수).</summary>
    public sealed record FlowBase(string FlowName, int IwBase, int QwBase, int MwBase);

    public sealed record Input(string IwMacro, string QwMacro, string MwMacro, IReadOnlyList<FlowBase> FlowBases);

    public static List<IoBatchRow> Generate(DsStore store, Input input, Guid? systemId = null)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (input == null) throw new ArgumentNullException(nameof(input));

        // 멀티 PLC — systemId 가 주어지면 그 System 소속 Call 만 대상.
        // 주소 순번(nth 추적)이 System 스코프로 매겨져야 다른 PLC 의 같은 Flow 명과 섞이지 않는다.
        HashSet<Guid>? callIdsInSystem = null;
        if (systemId is { } sid)
        {
            callIdsInSystem = new HashSet<Guid>();
            foreach (var flow in Queries.flowsOf(sid, store))
                foreach (var work in Queries.worksOf(flow.Id, store))
                    foreach (var call in Queries.callsOf(work.Id, store))
                        callIdsInSystem.Add(call.Id);
        }

        // ApiCall 순회 + context 해석(외부 AAStoPLC F# 모듈) → F# 입력 record 시퀀스로 변환.
        var contexts = store.Calls.Values
            .Where(c => callIdsInSystem == null || callIdsInSystem.Contains(c.Id))
            .SelectMany(c => c.ApiCalls.Select(ac => (call: c, apiCall: ac)))
            .Select(t =>
            {
                var c = ApiCallContextResolver.Resolve(store, t.apiCall);
                return new BasicMacroIo.ApiCallContext(
                    parentCallId: c.ParentCallId,
                    apiCallId:    t.apiCall.Id,
                    apiCallName:  t.apiCall.Name ?? "",
                    flowName:     c.FlowName ?? "",
                    deviceAlias:  c.DeviceAlias ?? "",
                    apiName:      c.ApiName ?? "");
            });

        var fsInput = new BasicMacroIo.Input(
            iwMacro:    input.IwMacro ?? "",
            qwMacro:    input.QwMacro ?? "",
            mwMacro:    input.MwMacro ?? "",
            flowBases:  Microsoft.FSharp.Collections.ListModule.OfSeq(
                            input.FlowBases.Select(b => new BasicMacroIo.FlowBase(
                                flowName: b.FlowName,
                                iwBase:   b.IwBase,
                                qwBase:   b.QwBase,
                                mwBase:   b.MwBase))));

        // SignalCounts lookup 은 F# 기본 헬퍼 사용 (store 의 ControlCallProperties 조회).
        FSharpFunc<Guid, FSharpFunc<string, FSharpOption<int>>> lookup =
            FuncConvert.FromFunc<Guid, string, FSharpOption<int>>(
                (cid, api) => BasicMacroIo.signalCountFromStore(store, cid, api));

        var rows = BasicMacroIo.generate(lookup, fsInput, contexts);

        return rows.Select(r => new IoBatchRow(
            callId:     r.CallId,
            apiCallId:  r.ApiCallId,
            flow:       r.Flow,
            work:       r.Work,
            device:     r.Device,
            api:        r.Api,
            inAddress:  r.InAddress,
            inSymbol:   r.InSymbol,
            outAddress: r.OutAddress,
            outSymbol:  r.OutSymbol)).ToList();
    }

    /// <summary>매크로 토큰($(F)/$(D)/$(A)) 치환 — TagWizardBasicDialog 등에서 재사용.</summary>
    public static string Expand(string macro, string flow, string device, string api) =>
        BasicMacroIo.expand(macro, flow, device, api);
}
