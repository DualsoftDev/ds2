using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// B안 마이그레이션 — 이종 Device Call 을 Device 별로 분할, 빈 Call 삭제.
/// arrows 는 재배선하지 않는다 (사용자 결정: ApiCalls 는 그룹화 컨테이너일 뿐).
/// 공개 Store API (AddCallWithLinkedApiDefs / RemoveEntities) 사용 — 트랜잭션/히스토리 자동 처리.
/// </summary>
public static class MultiDeviceCallSplitter
{
    public sealed class InvalidCallRow
    {
        public Guid   CallId        { get; init; }
        public string CallName      { get; init; } = "";
        public string WorkName      { get; init; } = "";
        public string FlowName      { get; init; } = "";
        public string Reason        { get; init; } = "";
        public int    DeviceCount   { get; init; }
        public int    ApiCallCount  { get; init; }
        public bool   IsEmptyCall   => ApiCallCount == 0;
        public bool   IsMultiDevice => DeviceCount > 1;
        /// <summary>체크박스 바인딩 — true 면 "모두 적용" 시 처리.</summary>
        public bool   Selected      { get; set; } = true;
    }

    public static List<InvalidCallRow> Scan(DsStore store)
    {
        var invalid = CallValidation.detectInvalidCalls(store).ToList();
        var result = new List<InvalidCallRow>();

        foreach (var tuple in invalid)
        {
            var callId = tuple.Item1;
            var reasons = tuple.Item2.ToList();
            var callOpt = Queries.getCall(callId, store);
            if (!FSharpOption<Call>.get_IsSome(callOpt)) continue;
            var call = callOpt.Value;

            var devices = CallValidation.devicesOf(call, store).ToList();
            var workOpt = Queries.getWork(call.ParentId, store);
            var workName = FSharpOption<Work>.get_IsSome(workOpt) ? workOpt.Value.Name : "";
            var flowName = "";
            if (FSharpOption<Work>.get_IsSome(workOpt))
            {
                var flowOpt = Queries.getFlow(workOpt.Value.ParentId, store);
                if (FSharpOption<Flow>.get_IsSome(flowOpt)) flowName = flowOpt.Value.Name;
            }

            result.Add(new InvalidCallRow
            {
                CallId       = call.Id,
                CallName     = call.Name,
                WorkName     = workName,
                FlowName     = flowName,
                Reason       = string.Join(" / ", reasons),
                DeviceCount  = devices.Count,
                ApiCallCount = call.ApiCalls.Count,
            });
        }
        return result;
    }

    /// <summary>
    /// 선택된 행들에 대해 분할/삭제 수행. 반환: (splitCount, deletedCount).
    /// - 이종 Device Call → Device 별 분할. 각 분할본은 AddCallWithLinkedApiDefs 로 생성 후 원 Call 삭제.
    /// - 빈 ApiCalls Call → RemoveEntities 로 삭제.
    /// arrows 는 그대로 둔다 (원 Call 참조 arrow 는 cascade 삭제됨; 분할본은 새 Call 이라 arrow 없음).
    /// </summary>
    public static (int SplitCount, int DeletedCount) Apply(DsStore store, IEnumerable<InvalidCallRow> rows)
    {
        int splits = 0;
        int deletes = 0;

        // Enumerate first (we'll mutate store during iteration via public API)
        var work = rows.Where(r => r.Selected).ToList();

        // 삭제 대상 (빈 Call 및 분할 후 원본 삭제용) 누적
        var toRemove = new List<(EntityKind Kind, Guid Id)>();

        foreach (var row in work)
        {
            var callOpt = Queries.getCall(row.CallId, store);
            if (!FSharpOption<Call>.get_IsSome(callOpt)) continue;
            var call = callOpt.Value;

            if (row.IsEmptyCall)
            {
                toRemove.Add((EntityKind.Call, call.Id));
                deletes++;
                continue;
            }

            if (!row.IsMultiDevice) continue;

            // Device(passiveSystem) 별 그룹화
            var groups = call.ApiCalls
                .Select(ac =>
                {
                    if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId))
                        return (ApiDefId: Guid.Empty, DeviceId: Guid.Empty, DeviceName: "");
                    var apiDefOpt = Queries.getApiDef(ac.ApiDefId.Value, store);
                    if (!FSharpOption<ApiDef>.get_IsSome(apiDefOpt))
                        return (ApiDefId: Guid.Empty, DeviceId: Guid.Empty, DeviceName: "");
                    var apiDef = apiDefOpt.Value;
                    var sysOpt = Queries.getSystem(apiDef.ParentId, store);
                    var sysName = FSharpOption<DsSystem>.get_IsSome(sysOpt) ? sysOpt.Value.Name : "";
                    return (ApiDefId: apiDef.Id, DeviceId: apiDef.ParentId, DeviceName: sysName);
                })
                .Where(t => t.DeviceId != Guid.Empty)
                .GroupBy(t => t.DeviceId)
                .ToList();

            foreach (var g in groups)
            {
                var deviceName = g.First().DeviceName;
                var apiDefIds = g.Select(t => t.ApiDefId).Distinct().ToList();
                // 원본 Call 의 ApiName 을 유지하며 DevicesAlias 만 Device 이름으로 교체.
                // Name = "{DevicesAlias}.{ApiName}" 규칙 — 새 이름은 "<Device>.<ApiName>".
                DsStoreNodesExtensions.AddCallWithLinkedApiDefs(
                    store, call.ParentId, deviceName, call.ApiName,
                    apiDefIds as IEnumerable<Guid>);
                splits++;
            }

            // 원본 Call 은 분할 완료 후 삭제
            toRemove.Add((EntityKind.Call, call.Id));
        }

        if (toRemove.Count > 0)
        {
            var selections = toRemove.Select(t => Tuple.Create(t.Kind, t.Id));
            DsStoreNodesExtensions.RemoveEntities(store, selections);
        }

        return (splits, deletes);
    }
}
