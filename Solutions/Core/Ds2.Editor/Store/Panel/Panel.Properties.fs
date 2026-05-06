namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


[<Extension>]
type DsStorePanelPropertiesExtensions =
    /// 프로젝트 속성 일괄 변경 (Undo 지원)
    [<Extension>]
    static member UpdateProjectProperties(store: DsStore, author: string, dateTime: DateTimeOffset,
                                          version: string) =
        StoreLog.debug($"UpdateProjectProperties")
        let project = Queries.allProjects store |> List.head
        store.WithTransaction("프로젝트 속성 변경", fun () ->
            store.TrackMutate(store.Projects, project.Id, fun p ->
                p.Author <- author
                p.DateTime <- dateTime
                p.Version <- version))

    /// 시스템 타입 변경 (Undo 지원)
    [<Extension>]
    static member UpdateSystemType(store: DsStore, systemId: Guid, systemType: string) =
        StoreLog.debug($"UpdateSystemType systemId={systemId}, systemType={systemType}")
        store.WithTransaction("시스템 타입 변경", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                sys.SystemType <- DirectPanelOps.toOpt systemType))
        store.EmitAndHistory(SystemPropsChanged systemId)

    /// ApiCall IO 태그 일괄 업데이트 — 단일 transaction + Call 별 1회 이벤트.
    /// Wizard Apply 의 N 행 적용 시 N transactions → 1 transaction 으로 축소.
    /// entries: (callId, apiCallId, outTag, inTag) 튜플 시퀀스. null IOTag 는 None 으로 저장.
    /// 반환값: 실제로 적용된 entry 개수.
    [<Extension>]
    static member UpdateApiCallIoTagsBatch(
            store: DsStore,
            entries: System.Collections.Generic.IReadOnlyList<struct (Guid * Guid * IOTag * IOTag)>) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            let mutable applied = 0
            let touchedCallIds = System.Collections.Generic.HashSet<Guid>()
            store.WithTransaction("IO 태그 일괄 업데이트", fun () ->
                for e in entries do
                    let struct (callId, apiCallId, outTag, inTag) = e
                    if callId <> Guid.Empty && apiCallId <> Guid.Empty then
                        try
                            Queries.requireNonReferenceCall callId store
                            store.TrackMutate(store.Calls, callId, fun call ->
                                call.ApiCalls
                                |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
                                |> Option.iter (fun targetApiCall ->
                                    targetApiCall.OutTag <- Option.ofObj outTag
                                    targetApiCall.InTag  <- Option.ofObj inTag))
                            touchedCallIds.Add callId |> ignore
                            applied <- applied + 1
                        with _ -> ()
            )
            // Call 별 1회만 이벤트 — 같은 Call 내 N ApiCall 변경은 1 refresh.
            for cid in touchedCallIds do
                store.EmitAndHistory(CallPropsChanged cid)
            applied
