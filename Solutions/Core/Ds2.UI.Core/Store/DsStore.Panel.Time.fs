namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

module internal PanelTimeOps =
    let getMs (tsOpt: TimeSpan option) : int option =
        tsOpt |> Option.map (fun t -> int t.TotalMilliseconds)

    let fromMs (ms: int option) : TimeSpan option =
        ms |> Option.map (fun m -> TimeSpan.FromMilliseconds(float m))

    let readMs (query: Guid -> DsStore -> 'T option) (getProp: 'T -> TimeSpan option) (entityKind: EntityKind) (store: DsStore) (id: Guid) : int option =
        query id store
        |> Option.bind (fun entity -> getProp entity |> getMs)
        |> Option.orElseWith (fun () -> StoreLog.warn($"{entityKind} not found. id={id}"); None)

[<Extension>]
type DsStorePanelTimeExtensions =
    [<Extension>]
    static member GetWorkPeriodMs(store: DsStore, workId: Guid) : int option =
        PanelTimeOps.readMs DsQuery.getWork (fun w -> w.Properties.Period) EntityKind.Work store workId

    [<Extension>]
    static member GetWorkPeriodMsOrNull(store: DsStore, workId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetWorkPeriodMs(store, workId)
        |> Option.toNullable

    [<Extension>]
    static member GetCallTimeoutMs(store: DsStore, callId: Guid) : int option =
        PanelTimeOps.readMs DsQuery.getCall (fun c -> c.Properties.Timeout) EntityKind.Call store callId

    [<Extension>]
    static member GetCallTimeoutMsOrNull(store: DsStore, callId: Guid) : Nullable<int> =
        DsStorePanelTimeExtensions.GetCallTimeoutMs(store, callId)
        |> Option.toNullable

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: int option) =
        StoreLog.debug($"workId={workId}, periodMs={periodMs}")
        let work = StoreLog.requireWork(store, workId)
        let period = PanelTimeOps.fromMs periodMs
        if work.Properties.Period <> period then
            store.WithTransaction("Work 속성 변경", fun () ->
                store.TrackMutate(store.Works, workId, fun w -> w.Properties.Period <- period))
            store.EmitAndHistory(WorkPropsChanged workId)

    [<Extension>]
    static member UpdateWorkPeriodMs(store: DsStore, workId: Guid, periodMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateWorkPeriodMs(store, workId, Option.ofNullable periodMs)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: int option) =
        StoreLog.debug($"callId={callId}, timeoutMs={timeoutMs}")
        let call = StoreLog.requireCall(store, callId)
        let timeout = PanelTimeOps.fromMs timeoutMs
        if call.Properties.Timeout <> timeout then
            store.WithTransaction("Call 속성 변경", fun () ->
                store.TrackMutate(store.Calls, callId, fun c -> c.Properties.Timeout <- timeout))
            store.EmitAndHistory(CallPropsChanged callId)

    [<Extension>]
    static member UpdateCallTimeoutMs(store: DsStore, callId: Guid, timeoutMs: Nullable<int>) =
        DsStorePanelTimeExtensions.UpdateCallTimeoutMs(store, callId, Option.ofNullable timeoutMs)

    // ─── ApiDef / System Query ─────────────────────────────────────
