namespace Ds2.Core.Store

open System
open Ds2.Core
open Ds2.Core.Store

// =============================================================================
// Query 모듈 - 읽기 전용 쿼리 API
// =============================================================================

/// <summary>
/// 도메인 스토어 쿼리 모듈
///
/// <para>모든 엔티티 조회 및 관계 탐색 기능을 제공합니다.</para>
///
/// <example>
/// <code>
/// let store = DsStore.empty()
/// let project = Queries.getProject projectId store
/// let flows = Queries.flowsOf systemId store
/// </code>
/// </example>
/// </summary>
[<AutoOpen>]
module Queries =

    // ─────────────────────────────────────────────────────────────────────────
    // 내부 헬퍼 — Dictionary 보일러플레이트 제거
    // ─────────────────────────────────────────────────────────────────────────

    let private byId (dict: System.Collections.Generic.Dictionary<Guid, 'T>) (id: Guid) : 'T option =
        match dict.TryGetValue(id) with true, v -> Some v | _ -> None

    let private allOf (dict: System.Collections.Generic.IReadOnlyDictionary<Guid, 'T>) : 'T list =
        dict.Values |> Seq.toList

    let private childrenOf (values: seq<'T>) (parentId: Guid) (getParent: 'T -> Guid) : 'T list =
        values |> Seq.filter (fun x -> getParent x = parentId) |> Seq.toList

    let private orderedSystemsOf (getIds: Project -> seq<Guid>) (projectId: Guid) (store: DsStore) : DsSystem list =
        match store.Projects.TryGetValue(projectId) with
        | true, project -> getIds project |> Seq.choose (fun id -> byId store.Systems id) |> Seq.toList
        | false, _      -> []

    // ─────────────────────────────────────────────────────────────────────────
    // Project 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Project ID로 Project 조회</summary>
    let getProject (id: Guid) (store: DsStore) = byId store.Projects id

    /// <summary>모든 Project 조회</summary>
    let allProjects (store: DsStore) : Project list = allOf store.ProjectsReadOnly

    // ─────────────────────────────────────────────────────────────────────────
    // DsSystem 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>DsSystem ID로 DsSystem 조회</summary>
    let getSystem (id: Guid) (store: DsStore) = byId store.Systems id

    /// <summary>특정 Project의 ActiveSystem 목록 조회 (순서 유지)</summary>
    let activeSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        orderedSystemsOf (fun p -> p.ActiveSystemIds) projectId store

    /// <summary>특정 Project의 PassiveSystem 목록 조회 (순서 유지)</summary>
    let passiveSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        orderedSystemsOf (fun p -> p.PassiveSystemIds) projectId store

    /// <summary>특정 Project의 모든 System 조회 (Active + Passive, 순서 유지)</summary>
    let projectSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        activeSystemsOf projectId store @ passiveSystemsOf projectId store

    // ─────────────────────────────────────────────────────────────────────────
    // Flow 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Flow ID로 Flow 조회</summary>
    let getFlow (id: Guid) (store: DsStore) = byId store.Flows id

    /// <summary>모든 Flow 조회</summary>
    let allFlows (store: DsStore) : Flow list = allOf store.FlowsReadOnly

    /// <summary>특정 DsSystem에 속한 Flow들 조회</summary>
    let flowsOf (systemId: Guid) (store: DsStore) : Flow list =
        childrenOf store.FlowsReadOnly.Values systemId (fun f -> f.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // Work 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Work ID로 Work 조회</summary>
    let getWork (id: Guid) (store: DsStore) = byId store.Works id

    /// <summary>특정 Flow에 속한 Work들 조회</summary>
    let worksOf (flowId: Guid) (store: DsStore) : Work list =
        childrenOf store.WorksReadOnly.Values flowId (fun w -> w.ParentId)

    /// <summary>특정 Project의 Active System에 속한 모든 Work 조회</summary>
    let activeWorksOf (projectId: Guid) (store: DsStore) : Work list =
        activeSystemsOf projectId store
        |> List.collect (fun sys -> flowsOf sys.Id store)
        |> List.collect (fun flow -> worksOf flow.Id store)

    /// <summary>Work가 속한 System의 ID를 반환</summary>
    let trySystemIdOfWork (workId: Guid) (store: DsStore) : Guid option =
        getWork workId store
        |> Option.bind (fun work -> getFlow work.ParentId store)
        |> Option.map (fun flow -> flow.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // Work 이름/참조 헬퍼
    // ─────────────────────────────────────────────────────────────────────────

    /// Work ID로 전체 표시명("Flow.Work" 형식) 조회
    let tryGetWorkFullName (workId: Guid) (store: DsStore) : string option =
        getWork workId store |> Option.map (fun w -> w.Name)

    /// Flow 내 원본 Work만 (ReferenceOf = None)
    let originalWorksOf (flowId: Guid) (store: DsStore) : Work list =
        worksOf flowId store |> List.filter (fun w -> w.ReferenceOf.IsNone)

    /// Flow 내 LocalName 중복 검사 (excludeId: 자기 자신 제외)
    let isLocalNameUniqueInFlow (flowId: Guid) (localName: string) (excludeId: Guid option) (store: DsStore) : bool =
        worksOf flowId store
        |> List.exists (fun w ->
            w.LocalName = localName
            && (match excludeId with Some id -> w.Id <> id | None -> true))
        |> not

    /// System 내 Flow 이름 중복 검사 (excludeId: 자기 자신 제외)
    let isFlowNameUniqueInSystem (systemId: Guid) (name: string) (excludeId: Guid option) (store: DsStore) : bool =
        flowsOf systemId store
        |> List.exists (fun f ->
            f.Name = name
            && (match excludeId with Some id -> f.Id <> id | None -> true))
        |> not

    /// 자동 증가 이름: "Name" → "Name_1" → "Name_2"
    let nextUniqueName (baseName: string) (existingNames: string list) : string =
        if not (List.contains baseName existingNames) then baseName
        else
            let mutable i = 1
            while List.contains $"{baseName}_{i}" existingNames do
                i <- i + 1
            $"{baseName}_{i}"

    /// Reference Work이면 원본 ID, 아니면 자기 자신 ID 반환
    let resolveOriginalWorkId (workId: Guid) (store: DsStore) : Guid =
        getWork workId store
        |> Option.bind (fun w -> w.ReferenceOf)
        |> Option.defaultValue workId

    /// Reference OR 그룹: 원본 Work + 해당 원본을 참조하는 모든 reference Work의 ID
    let referenceGroupOf (workId: Guid) (store: DsStore) : Guid list =
        let origId =
            getWork workId store
            |> Option.bind (fun w -> w.ReferenceOf)
            |> Option.defaultValue workId
        store.WorksReadOnly.Values
        |> Seq.filter (fun w -> w.Id = origId || w.ReferenceOf = Some origId)
        |> Seq.map (fun w -> w.Id)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // Call 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Call ID로 Call 조회</summary>
    let getCall (id: Guid) (store: DsStore) = byId store.Calls id

    /// <summary>특정 Work에 속한 Call들 조회</summary>
    let callsOf (workId: Guid) (store: DsStore) : Call list =
        childrenOf store.CallsReadOnly.Values workId (fun c -> c.ParentId)

    /// Work 내 원본 Call만 (ReferenceOf = None)
    let originalCallsOf (workId: Guid) (store: DsStore) : Call list =
        callsOf workId store |> List.filter (fun c -> c.ReferenceOf.IsNone)

    /// Reference Call이면 invalidOp — 원본에서 수정하도록 유도
    let requireNonReferenceCall (callId: Guid) (store: DsStore) =
        match getCall callId store with
        | Some c when c.ReferenceOf.IsSome ->
            invalidOp "레퍼런스 Call은 수정할 수 없습니다. 원본 Call에서 수정하세요."
        | _ -> ()

    /// Reference Call이면 원본 ID, 아니면 자기 자신 ID 반환
    let resolveOriginalCallId (callId: Guid) (store: DsStore) : Guid =
        getCall callId store
        |> Option.bind (fun c -> c.ReferenceOf)
        |> Option.defaultValue callId

    /// Reference OR 그룹: 원본 Call + 해당 원본을 참조하는 모든 reference Call의 ID
    let callReferenceGroupOf (callId: Guid) (store: DsStore) : Guid list =
        let origId =
            getCall callId store
            |> Option.bind (fun c -> c.ReferenceOf)
            |> Option.defaultValue callId
        store.CallsReadOnly.Values
        |> Seq.filter (fun c -> c.Id = origId || c.ReferenceOf = Some origId)
        |> Seq.map (fun c -> c.Id)
        |> Seq.toList

    /// Work 내 Call 이름(DevicesAlias.ApiName) 중복 검사 — 원본 Call만 대상
    let isCallNameUniqueInWork (workId: Guid) (callName: string) (excludeId: Guid option) (store: DsStore) : bool =
        originalCallsOf workId store
        |> List.exists (fun c -> c.Name = callName && excludeId <> Some c.Id)
        |> not

    // ─────────────────────────────────────────────────────────────────────────
    // ApiDef 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiDef ID로 ApiDef 조회</summary>
    let getApiDef (id: Guid) (store: DsStore) = byId store.ApiDefs id

    /// <summary>특정 DsSystem에 속한 ApiDef들 조회</summary>
    let apiDefsOf (systemId: Guid) (store: DsStore) : ApiDef list =
        childrenOf store.ApiDefsReadOnly.Values systemId (fun a -> a.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // ApiCall 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiCall ID로 ApiCall 조회</summary>
    let getApiCall (id: Guid) (store: DsStore) = byId store.ApiCalls id

    /// <summary>모든 ApiCall 조회</summary>
    let allApiCalls (store: DsStore) : ApiCall list = allOf store.ApiCallsReadOnly

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenWorks 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenWorks ID로 Arrow 조회</summary>
    let getArrowWork (id: Guid) (store: DsStore) = byId store.ArrowWorks id

    /// <summary>모든 ArrowBetweenWorks 조회</summary>
    let allArrowWorks (store: DsStore) : ArrowBetweenWorks list = allOf store.ArrowWorksReadOnly

    /// <summary>특정 System에 속한 ArrowBetweenWorks 조회</summary>
    let arrowWorksOf (systemId: Guid) (store: DsStore) : ArrowBetweenWorks list =
        childrenOf store.ArrowWorksReadOnly.Values systemId (fun a -> a.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenCalls 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenCalls ID로 Arrow 조회</summary>
    let getArrowCall (id: Guid) (store: DsStore) = byId store.ArrowCalls id

    /// <summary>모든 ArrowBetweenCalls 조회</summary>
    let allArrowCalls (store: DsStore) : ArrowBetweenCalls list = allOf store.ArrowCallsReadOnly

    /// <summary>특정 Work에 속한 ArrowBetweenCalls 조회</summary>
    let arrowCallsOf (workId: Guid) (store: DsStore) : ArrowBetweenCalls list =
        childrenOf store.ArrowCallsReadOnly.Values workId (fun a -> a.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // 엔티티 이름 접근 (EntityKind 기반)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>EntityKind + ID로 엔티티 이름 조회</summary>
    let tryGetName (store: DsStore) (entityKind: EntityKind) (id: Guid) : string option =
        let entity =
            match entityKind with
            | EntityKind.Project   -> getProject   id store |> Option.map (fun e -> e :> DsEntity)
            | EntityKind.System    -> getSystem    id store |> Option.map (fun e -> e :> DsEntity)
            | EntityKind.Flow      -> getFlow      id store |> Option.map (fun e -> e :> DsEntity)
            | EntityKind.Work      -> getWork      id store |> Option.map (fun e -> e :> DsEntity)
            | EntityKind.Call      -> getCall      id store |> Option.map (fun e -> e :> DsEntity)
            | EntityKind.ApiDef    -> getApiDef    id store |> Option.map (fun e -> e :> DsEntity)
            | _                    -> None
        entity |> Option.map (fun e -> e.Name)

    /// CallCondition 트리에서 ID로 재귀 검색
    let rec tryFindConditionRec (conditions: CallCondition seq) (condId: Guid) : CallCondition option =
        conditions |> Seq.tryPick (fun cc ->
            if cc.Id = condId then Some cc
            else tryFindConditionRec cc.Children condId)

    // ─────────────────────────────────────────────────────────────────────────
    // Work ↔ Device Duration 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// Call 하나의 Device duration(ms): Call → ApiCall → ApiDef → RxGuid → Device Work → Duration
    let private callDeviceDurationMs (call: Call) (store: DsStore) : int =
        call.ApiCalls
        |> Seq.choose (fun apiCall ->
            apiCall.ApiDefId
            |> Option.bind (fun defId -> getApiDef defId store)
            |> Option.bind (fun def -> def.RxGuid)
            |> Option.bind (fun rxWorkId -> getWork rxWorkId store)
            |> Option.bind (fun rxWork -> rxWork.Duration)
            |> Option.map (fun ts -> int ts.TotalMilliseconds))
        |> Seq.tryHead
        |> Option.defaultValue 0

    /// <summary>Work 내 Call들의 Critical Path Duration(ms)을 반환합니다.
    /// Call arrow topology(ArrowBetweenCalls Start)를 분석하여 병렬/직렬 실행을 고려한
    /// 최장 경로(critical path)를 계산합니다. Device duration이 없으면 None.</summary>
    let tryGetDeviceDurationMs (workId: Guid) (store: DsStore) : int option =
        let calls = callsOf workId store
        if calls.IsEmpty then None
        else
            let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList
            let durationMap =
                calls |> List.map (fun c -> c.Id, callDeviceDurationMs c store) |> Map.ofList

            // Call arrow topology: Start/StartReset 화살표만 사용
            let arrows = arrowCallsOf workId store
            let predsMap =
                arrows
                |> List.filter (fun a ->
                    a.ArrowType = ArrowType.Start || a.ArrowType = ArrowType.StartReset)
                |> List.filter (fun a -> Set.contains a.SourceId callIds && Set.contains a.TargetId callIds)
                |> List.groupBy (fun a -> a.TargetId)
                |> List.map (fun (tgt, arr) -> tgt, arr |> List.map (fun a -> a.SourceId))
                |> Map.ofList

            // Critical path: longestPath(c) = duration(c) + max(longestPath(pred))
            let mutable memo = Map.empty<Guid, int>
            let rec longestPathTo (callId: Guid) =
                match Map.tryFind callId memo with
                | Some v -> v
                | None ->
                    let myDuration = Map.tryFind callId durationMap |> Option.defaultValue 0
                    let preds = Map.tryFind callId predsMap |> Option.defaultValue []
                    let maxPredPath =
                        match preds |> List.map longestPathTo with
                        | [] -> 0
                        | xs -> List.max xs
                    let result = myDuration + maxPredPath
                    memo <- memo.Add(callId, result)
                    result

            let criticalPath =
                match calls |> List.map (fun c -> longestPathTo c.Id) with
                | [] -> 0
                | xs -> List.max xs

            if criticalPath > 0 then Some criticalPath else None

    // ─────────────────────────────────────────────────────────────────────────
    // TokenSpec 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>첫 번째 Project의 TokenSpec 목록 조회</summary>
    let getTokenSpecs (store: DsStore) : TokenSpec list =
        match allProjects store |> List.tryHead with
        | Some project -> project.TokenSpecs |> Seq.toList
        | None -> []
