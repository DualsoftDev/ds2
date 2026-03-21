namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store

/// DsStore 확장 메서드 공유 로깅 + 엔티티 검증 헬퍼.
/// 쓰기 연산에서 엔티티가 없으면 Warn 로깅 + InvalidOperationException.
/// [<CallerMemberName>] 으로 호출자 메서드명을 자동 캡처 — 하드코딩 문자열 제거.
/// F# curried optional 파라미터는 CallerMemberName 미지원 → static member (tupled) 사용.
[<AbstractClass; Sealed>]
type internal StoreLog private () =
    static let log = log4net.LogManager.GetLogger("Ds2.Editor.Extensions")

    // ─── 공통 헬퍼 ───────────────────────────────────────────────
    static member private resolve(op: string option) = defaultArg op "Unknown"
    static member private fmt(detail: string, op: string option) = $"{StoreLog.resolve op}: {detail}"

    static member private fail(op: string, msg: string) =
        StoreLog.warn(msg, op)
        invalidOp $"{op}: {msg}"

    // ─── 로깅 ──────────────────────────────────────────────────────
    static member debug(detail: string, [<CallerMemberName>] ?op: string) = log.Debug(StoreLog.fmt(detail, op))
    static member warn(detail: string, [<CallerMemberName>] ?op: string) = log.Warn(StoreLog.fmt(detail, op))

    // ─── 엔티티 require (not found → Warn + throw) ──────────────
    static member private requireEntity<'T>(query: Guid -> DsStore -> 'T option, entityType: string, store: DsStore, id: Guid, op: string option) : 'T =
        match query id store with
        | Some x -> x
        | None -> StoreLog.fail(StoreLog.resolve op, $"{entityType} not found. id={id}")

    static member requireProject(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : Project =
        StoreLog.requireEntity(DsQuery.getProject, "Project", store, id, op)

    static member requireSystem(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : DsSystem =
        StoreLog.requireEntity(DsQuery.getSystem, "System", store, id, op)

    static member requireFlow(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : Flow =
        StoreLog.requireEntity(DsQuery.getFlow, "Flow", store, id, op)

    static member requireWork(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : Work =
        StoreLog.requireEntity(DsQuery.getWork, "Work", store, id, op)

    static member requireCall(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : Call =
        StoreLog.requireEntity(DsQuery.getCall, "Call", store, id, op)

    static member requireApiDef(store: DsStore, id: Guid, [<CallerMemberName>] ?op: string) : ApiDef =
        StoreLog.requireEntity(DsQuery.getApiDef, "ApiDef", store, id, op)

    // ─── Seq 내 검색 (not found → Warn + throw) ──────────────────
    static member private requireInSeq<'T>(items: 'T seq, predicate: 'T -> bool, errMsg: string, op: string) : 'T =
        match items |> Seq.tryFind predicate with
        | Some x -> x
        | None -> StoreLog.fail(op, errMsg)

    static member private tryFindConditionRec (conditions: CallCondition seq) (condId: Guid) : CallCondition option =
        DsQuery.tryFindConditionRec conditions condId

    static member requireCallCondition(store: DsStore, callId: Guid, condId: Guid, [<CallerMemberName>] ?op: string) : CallCondition =
        let op = StoreLog.resolve op
        let call = StoreLog.requireCall(store, callId, op)
        match StoreLog.tryFindConditionRec call.CallConditions condId with
        | Some cc -> cc
        | None -> StoreLog.fail(op, $"CallCondition not found. callId={callId}, condId={condId}")

    static member requireApiCallInCall(call: Call, apiCallId: Guid, [<CallerMemberName>] ?op: string) =
        StoreLog.requireInSeq(
            call.ApiCalls, (fun ac -> ac.Id = apiCallId), 
            $"ApiCall {apiCallId} not found in Call {call.Id}", StoreLog.resolve op) |> ignore

    static member requireApiCallInCondition(cond: CallCondition, apiCallId: Guid, [<CallerMemberName>] ?op: string) : ApiCall =
        StoreLog.requireInSeq(
            cond.Conditions, (fun ac -> ac.Id = apiCallId), 
            $"ApiCall not found in condition. condId={cond.Id}, apiCallId={apiCallId}", StoreLog.resolve op)
