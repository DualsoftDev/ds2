namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

// =============================================================================
// 디바이스 / Action(API) 이름 일괄 변경 (cascade rename)
//
// 단일 트랜잭션(=Undo 1단계)으로 다음을 cascade 변경:
//  - 디바이스명 변경: DsSystem.Name + 그 디바이스 ApiDef 를 참조하는 ApiCall.Name 앞부분
//    + Call.DevicesAlias(옛 이름 일치 시) — 본체 + Condition 2경로.
//  - Action명 변경: ApiDef.Name + 내부 Work.LocalName(Tx/Rx) + ApiCall.Name 뒷부분
//    + 소유 Call.ApiName — 본체 + Condition 2경로.
//
// ★중대 제약(DsStore.fs RebuildApiCallsDictionary): store.ApiCalls(=allApiCalls)에는
//   Call 본체 ApiCall 만 등록되고, Call.Conditions / Work.Conditions 트리 내 ApiCall 은
//   같은 Id 의 *독립 인스턴스*로 dict 에 없다. 따라서 Condition 내 ApiCall.Name 갱신은
//   소유 Call/Work 를 TrackMutate 하며 Conditions 트리를 직접 재귀 순회해야 한다.
//   (TrackMutate(store.ApiCalls,..) 로는 Condition 인스턴스를 못 바꾼다.)
// =============================================================================

/// 디바이스/Action 이름 변경의 영향 미리보기.
/// preview(미리보기)와 apply(실제 적용)가 collectRenameImpact 단일 SSOT 를 공유하므로
/// 여기 담긴 건수/항목 = 실제 변경 건수와 일치한다.
type RenameDevicePreview = {
    /// 디바이스명 변경 여부 (Some(systemId, oldName, newName)). None 이면 디바이스명 미변경.
    DeviceRename     : (Guid * string * string) option
    /// Action(ApiDef)명 변경 목록 (apiDefId, oldName, newName) — 실제로 이름이 달라지는 것만.
    ApiRenames       : (Guid * string * string) list
    /// 이름이 바뀌는 본체 ApiCall 목록 (apiCallId, oldName, newName).
    BodyApiCalls     : (Guid * string * string) list
    /// 이름이 바뀌는 Condition 내 ApiCall 목록 (소유 ownerKind/ownerId + 옛/새 ApiCall 이름).
    /// 같은 owner 안 여러 ApiCall 이 바뀌면 항목 다건. apply 는 여기 등장한 owner 만 TrackMutate 한다.
    ConditionApiCalls: (EntityKind * Guid * string * string) list
    /// ApiName/DevicesAlias 가 바뀌는 Call 목록 (callId, oldCallName, newCallName).
    Calls            : (Guid * string * string) list
    /// LocalName 이 바뀌는 내부 Work 목록 (workId, oldLocalName, newLocalName).
    Works            : (Guid * string * string) list
    /// 대소문자 표류 등으로 자동 정리 불가한 항목 (위치 설명).
    /// 디바이스명 변경 시 ApiCall.Name 앞부분이 옛 디바이스명과 대소문자까지 일치하지 않으면 여기 모인다.
    DriftItems       : string list
}
with
    /// Call/ApiCall 외 영향 총 건수 합 (System + ApiDef + 본체 ApiCall + Condition ApiCall + Call + Work).
    member this.TotalCount =
        (if this.DeviceRename.IsSome then 1 else 0)
        + this.ApiRenames.Length
        + this.BodyApiCalls.Length
        + this.ConditionApiCalls.Length
        + this.Calls.Length
        + this.Works.Length


module internal RenameDeviceOps =

    /// 한 rename 작업(디바이스 또는 Action) 단위 — predicate(ApiDefId 기준 Guid) + 이름 변환.
    type RenameUnit = {
        Predicate : ApiCall -> bool
        Rename    : string -> string option
    }

    /// ApiCall.Name 의 앞부분(devAlias)을 newDevice 로 교체. '.' 없으면(dummy) None.
    let tryReplaceDevicePart (newDevice: string) (name: string) : string option =
        Queries.splitApiCallName name
        |> Option.map (fun (_, apiPart) -> $"{newDevice}.{apiPart}")

    /// ApiCall.Name 의 뒷부분(apiName)을 newApi 로 교체. '.' 없으면 None.
    let tryReplaceApiPart (newApi: string) (name: string) : string option =
        Queries.splitApiCallName name
        |> Option.map (fun (devPart, _) -> $"{devPart}.{newApi}")

    /// 디바이스 unit(있으면 맨 앞) + Action unit 들. predicate 는 전부 ApiDefId(Guid) 기준 — 이름 파싱 아님.
    /// dummy(ApiDefId=None) 는 어느 predicate 에도 안 걸려 자동 제외.
    let buildRenameUnits
        (store: DsStore) (systemId: Guid)
        (newDeviceName: string option) (apiRenames: (Guid * string) list) : RenameUnit list =
        let deviceUnit =
            match newDeviceName with
            | None -> []
            | Some newDev ->
                let deviceApiDefIds =
                    Queries.apiDefsOf systemId store |> List.map (fun d -> d.Id) |> Set.ofList
                if deviceApiDefIds.IsEmpty then []
                else
                    [ { Predicate = (fun ac -> ac.ApiDefId |> Option.exists deviceApiDefIds.Contains)
                        Rename    = tryReplaceDevicePart newDev } ]
        let apiUnits =
            apiRenames
            |> List.map (fun (apiDefId, newApi) ->
                { Predicate = (fun ac -> ac.ApiDefId = Some apiDefId)
                  Rename    = tryReplaceApiPart newApi })
        deviceUnit @ apiUnits

    /// 단일 ApiCall 에 적용 가능한 모든 unit 을 순차 적용한 최종 이름. 바뀐 게 없으면 None.
    /// (디바이스 + Action 동시 변경 시 앞부분/뒷부분이 각각 갱신될 수 있음.)
    let applyUnitsToName (units: RenameUnit list) (ac: ApiCall) : string option =
        let mutable current = ac.Name
        let mutable touched = false
        for u in units do
            if u.Predicate ac then
                match u.Rename current with
                | Some n when n <> current -> current <- n; touched <- true
                | _ -> ()
        if touched then Some current else None

    /// Conditions 트리를 재귀 순회하며 unit 적용 결과 (oldName, newName) 매핑 수집 (preview 용).
    let collectConditionRenames (units: RenameUnit list) (conditions: ResizeArray<Condition>)
        : (string * string) list =
        let rec walk (conds: ResizeArray<Condition>) acc =
            let mutable result = acc
            for cond in conds do
                for ac in cond.ApiCalls do
                    match applyUnitsToName units ac with
                    | Some newName -> result <- (ac.Name, newName) :: result
                    | None -> ()
                result <- walk cond.Children result
            result
        walk conditions [] |> List.rev

    /// Conditions 트리를 재귀 순회하며 unit 적용 결과로 ApiCall.Name 을 *직접 set* (apply 용).
    /// 실제로 하나라도 바뀌면 true.
    let applyConditionRenames (units: RenameUnit list) (conditions: ResizeArray<Condition>) : bool =
        let rec walk (conds: ResizeArray<Condition>) =
            let mutable changed = false
            for cond in conds do
                for ac in cond.ApiCalls do
                    match applyUnitsToName units ac with
                    | Some newName -> ac.Name <- newName; changed <- true
                    | None -> ()
                if walk cond.Children then changed <- true
            changed
        walk conditions


[<Extension>]
type DsStoreRenameDeviceExtensions =

    /// preview / apply 공유 SSOT. §4.3 대상 수집 (본체 + Condition 2경로, dummy 제외, Tx/Rx distinct).
    /// - systemId: 디바이스(Passive System) Guid.
    /// - newDeviceName: Some 이면 디바이스명 변경, None 이면 미변경.
    /// - apiRenames: (apiDefId, newName) 목록. oldName==newName 인 항목은 결과에서 제외한다.
    [<Extension>]
    static member CollectRenameImpact
        (store: DsStore, systemId: Guid, newDeviceName: string option, apiRenames: (Guid * string) list)
        : RenameDevicePreview =

        let oldDeviceName = Queries.getSystem systemId store |> Option.map (fun s -> s.Name)

        // 실제로 이름이 달라지는 디바이스명 변경만.
        let deviceRename =
            match newDeviceName, oldDeviceName with
            | Some newName, Some oldName when oldName <> newName -> Some(systemId, oldName, newName)
            | _ -> None

        // 실제로 이름이 달라지는 Action 변경만.
        let effectiveApiRenames =
            apiRenames
            |> List.choose (fun (apiDefId, newName) ->
                match Queries.getApiDef apiDefId store with
                | Some d when d.Name <> newName -> Some(apiDefId, d.Name, newName)
                | _ -> None)

        let units =
            RenameDeviceOps.buildRenameUnits store systemId
                (deviceRename |> Option.map (fun (_, _, n) -> n))
                (effectiveApiRenames |> List.map (fun (id, _, n) -> id, n))

        // ── (a) 본체 ApiCall ──────────────────────────────────────────
        let bodyApiCalls =
            Queries.allApiCalls store
            |> List.choose (fun ac ->
                RenameDeviceOps.applyUnitsToName units ac
                |> Option.map (fun newName -> ac.Id, ac.Name, newName))

        // ── 소유 Call 의 ApiName / DevicesAlias 변경 ──────────────────
        let callChanges =
            store.CallsReadOnly.Values
            |> Seq.choose (fun call ->
                let touchesBody =
                    call.ApiCalls |> Seq.exists (fun ac -> units |> List.exists (fun u -> u.Predicate ac))
                // ApiName 변경: 본체 ApiCall 중 effectiveApiRenames 대상이 있으면 새 ApiName.
                let newApiName =
                    call.ApiCalls
                    |> Seq.tryPick (fun ac ->
                        effectiveApiRenames
                        |> List.tryPick (fun (apiDefId, _, newName) ->
                            if ac.ApiDefId = Some apiDefId then Some newName else None))
                    |> Option.defaultValue call.ApiName
                // DevicesAlias 변경: 디바이스명 변경 + 현재 DevicesAlias == 옛 디바이스명 + 이 Call 이 그 디바이스 참조.
                let newAlias =
                    match deviceRename, oldDeviceName with
                    | Some(_, _, newDev), Some oldDev when call.DevicesAlias = oldDev && touchesBody -> newDev
                    | _ -> call.DevicesAlias
                if newApiName <> call.ApiName || newAlias <> call.DevicesAlias then
                    Some(call.Id, call.Name, $"{newAlias}.{newApiName}")
                else None)
            |> Seq.toList

        // ── (b) Condition 내 ApiCall (소유 Call/Work 별, ApiCall 인스턴스 단위 정확 계산) ──
        let conditionApiCalls =
            [ for call in store.CallsReadOnly.Values do
                for (oldN, newN) in RenameDeviceOps.collectConditionRenames units call.Conditions do
                    yield EntityKind.Call, call.Id, oldN, newN
              for work in store.WorksReadOnly.Values do
                for (oldN, newN) in RenameDeviceOps.collectConditionRenames units work.Conditions do
                    yield EntityKind.Work, work.Id, oldN, newN ]

        // ── 내부 Work.LocalName (Action 변경 시 Tx/Rx distinct) ───────
        let works =
            effectiveApiRenames
            |> List.collect (fun (apiDefId, _, newName) ->
                match Queries.getApiDef apiDefId store with
                | Some apiDef ->
                    [ apiDef.TxGuid; apiDef.RxGuid ]
                    |> List.choose id
                    |> List.distinct
                    |> List.choose (fun wid ->
                        Queries.getWork wid store
                        |> Option.bind (fun w ->
                            if w.LocalName <> newName then Some(w.Id, w.LocalName, newName) else None))
                | None -> [])
            |> List.distinctBy (fun (id, _, _) -> id)

        // ── 대소문자 표류(자동 정리 불가 — 미리보기 경고만) ───────────
        let driftItems =
            match deviceRename, oldDeviceName with
            | Some _, Some oldDev ->
                let deviceApiDefIds =
                    Queries.apiDefsOf systemId store |> List.map (fun d -> d.Id) |> Set.ofList
                Queries.allApiCalls store
                |> List.filter (fun ac -> ac.ApiDefId |> Option.exists deviceApiDefIds.Contains)
                |> List.choose (fun ac ->
                    Queries.splitApiCallName ac.Name
                    |> Option.bind (fun (devPart, _) ->
                        if devPart <> oldDev && String.Equals(devPart, oldDev, StringComparison.OrdinalIgnoreCase)
                        then Some $"ApiCall '{ac.Name}' 의 디바이스 표기 '{devPart}' 가 '{oldDev}' 와 대소문자 불일치"
                        else None))
            | _ -> []

        { DeviceRename      = deviceRename
          ApiRenames        = effectiveApiRenames
          BodyApiCalls      = bodyApiCalls
          ConditionApiCalls = conditionApiCalls
          Calls             = callChanges
          Works             = works
          DriftItems        = driftItems }

    /// collectRenameImpact 결과를 *단일* WithTransaction(=Undo 1단계) 으로 적용한다.
    /// 종료 emit = EmitRefreshAndHistory() 1회. 중복검사 포함 — 위반 시 invalidOp(트랜잭션 진입 전 차단).
    /// 반환 = 실제 변경된 엔티티 총 건수(preview.TotalCount 와 동일).
    [<Extension>]
    static member RenameDeviceBatch
        (store: DsStore, systemId: Guid, newDeviceName: string option, apiRenames: (Guid * string) list) : int =

        let preview = store.CollectRenameImpact(systemId, newDeviceName, apiRenames)

        if preview.TotalCount = 0 then 0
        else

        // ── 중복검사 (트랜잭션 진입 전: 위반 시 빈 트랜잭션조차 만들지 않음) ──
        match preview.DeviceRename with
        | Some(sysId, _, newName) ->
            match StoreHierarchyQueries.findProjectOfSystem store sysId with
            | Some projectId ->
                if not (Queries.isSystemNameUniqueInProject projectId newName (Some sysId) store) then
                    invalidOp $"프로젝트 내에 이미 '{newName}' System(디바이스)이 존재합니다."
            | None -> ()
        | None -> ()

        for (apiDefId, _, newName) in preview.ApiRenames do
            match Queries.getApiDef apiDefId store with
            | Some apiDef ->
                if not (Queries.isApiDefNameUniqueInSystem apiDef.ParentId newName (Some apiDefId) store) then
                    invalidOp $"디바이스 내에 이미 '{newName}' Action 이 존재합니다."
            | None -> ()

        for (workId, _, newName) in preview.Works do
            match Queries.getWork workId store with
            | Some work ->
                if not (Queries.isLocalNameUniqueInFlow work.ParentId newName (Some workId) store) then
                    invalidOp $"Flow 내에 이미 '{newName}' Work 가 존재합니다."
            | None -> ()

        for (callId, _, newFull) in preview.Calls do
            match Queries.getCall callId store with
            | Some call ->
                if not (Queries.isCallNameUniqueInWork call.ParentId newFull (Some callId) store) then
                    invalidOp $"Work 내에 이미 '{newFull}' Call 이 존재합니다."
            | None -> ()

        // ── rename units 재구성 (apply 경로에서 ApiCall 인스턴스에 직접 set) ──
        let units =
            RenameDeviceOps.buildRenameUnits store systemId
                (preview.DeviceRename |> Option.map (fun (_, _, n) -> n))
                (preview.ApiRenames |> List.map (fun (id, _, n) -> id, n))

        // ApiName/DevicesAlias 가 바뀌는 Call 집합 (preview.Calls = callChanges SSOT — 본체 ApiCall 이
        // renamed ApiDef 를 참조하면 callChanges 가 이미 그 Call 을 newApiName 으로 포함한다).
        let aliasOrApiCallIds = preview.Calls |> List.map (fun (cid, _, _) -> cid) |> Set.ofList
        // Condition 내 ApiCall 이 바뀌는 소유 Call/Work 집합 (preview 가 특정 — 무관 owner mutate 회피).
        let condCallIds =
            preview.ConditionApiCalls
            |> List.choose (fun (k, id, _, _) -> if k = EntityKind.Call then Some id else None)
            |> Set.ofList
        let condWorkIds =
            preview.ConditionApiCalls
            |> List.choose (fun (k, id, _, _) -> if k = EntityKind.Work then Some id else None)
            |> Set.ofList

        let label =
            match preview.DeviceRename with
            | Some(_, _, newName) -> $"디바이스 일괄 이름 변경 → \"{newName}\""
            | None -> "Action 일괄 이름 변경"

        store.WithTransaction(label, fun () ->
            // 1) System.Name
            match preview.DeviceRename with
            | Some(sysId, _, newName) ->
                store.TrackMutate(store.Systems, sysId, fun s -> s.Name <- newName)
            | None -> ()

            // 2) ApiDef.Name
            for (apiDefId, _, newName) in preview.ApiRenames do
                store.TrackMutate(store.ApiDefs, apiDefId, fun d -> d.Name <- newName)

            // 3) 내부 Work.LocalName (Tx/Rx distinct — preview.Works 가 이미 distinctBy id)
            for (workId, _, newName) in preview.Works do
                store.TrackMutate(store.Works, workId, fun w -> w.LocalName <- newName)

            // 4a) 본체 ApiCall.Name — *반드시* store.ApiCalls dict 경로로 TrackMutate.
            //     본체 ApiCall 은 store.ApiCalls 와 Call.ApiCalls 가 동일 인스턴스를 공유하고,
            //     undo/redo 직후 RewireApiCallReferences() 가 Call.ApiCalls 를 store.ApiCalls 로 재동기화한다
            //     (Authoring.fs applyTransaction). 따라서 Call snapshot 안에서만 ac.Name 을 바꾸면 그 변경은
            //     undo 시 store.ApiCalls(미추적) 의 옛 값으로 덮여 되돌려지지 않는다(=Undo 누락 회귀).
            for (acId, _, newName) in preview.BodyApiCalls do
                store.TrackMutate(store.ApiCalls, acId, fun ac -> ac.Name <- newName)

            // 4b) 소유 Call.ApiName / DevicesAlias.
            //     Call.Name setter 가 ApiName 변경을 차단 → 필드(c.ApiName / c.DevicesAlias) 직접 set.
            for callId in aliasOrApiCallIds do
                store.TrackMutate(store.Calls, callId, fun c ->
                    c.ApiCalls
                    |> Seq.tryPick (fun ac ->
                        preview.ApiRenames
                        |> List.tryPick (fun (apiDefId, _, newName) ->
                            if ac.ApiDefId = Some apiDefId then Some newName else None))
                    |> Option.iter (fun newApiName -> c.ApiName <- newApiName)
                    match preview.DeviceRename with
                    | Some(_, oldDev, newDev) when c.DevicesAlias = oldDev -> c.DevicesAlias <- newDev
                    | _ -> ())

            // 5) Condition 내 ApiCall.Name — preview 가 특정한 소유 Call/Work 만 TrackMutate 하며
            //    트리 직접 재귀 순회 (§3.1: store.ApiCalls dict 경로로는 못 바꾼다).
            for callId in condCallIds do
                store.TrackMutate(store.Calls, callId, fun c ->
                    RenameDeviceOps.applyConditionRenames units c.Conditions |> ignore)
            for workId in condWorkIds do
                store.TrackMutate(store.Works, workId, fun w ->
                    RenameDeviceOps.applyConditionRenames units w.Conditions |> ignore))

        store.EmitRefreshAndHistory()
        preview.TotalCount
