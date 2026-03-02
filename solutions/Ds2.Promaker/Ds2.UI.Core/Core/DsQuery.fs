namespace Ds2.UI.Core

open System
open Ds2.Core

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
/// let project = DsQuery.getProject projectId store
/// let systems = DsQuery.allSystems store
/// let flows = DsQuery.flowsOf systemId store
/// </code>
/// </example>
/// </summary>
module DsQuery =

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

    /// <summary>모든 DsSystem 조회</summary>
    let allSystems (store: DsStore) : DsSystem list = allOf store.SystemsReadOnly

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

    /// <summary>모든 Work 조회</summary>
    let allWorks (store: DsStore) : Work list = allOf store.WorksReadOnly

    /// <summary>특정 Flow에 속한 Work들 조회</summary>
    let worksOf (flowId: Guid) (store: DsStore) : Work list =
        childrenOf store.WorksReadOnly.Values flowId (fun w -> w.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // Call 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Call ID로 Call 조회</summary>
    let getCall (id: Guid) (store: DsStore) = byId store.Calls id

    /// <summary>모든 Call 조회</summary>
    let allCalls (store: DsStore) : Call list = allOf store.CallsReadOnly

    /// <summary>특정 Work에 속한 Call들 조회</summary>
    let callsOf (workId: Guid) (store: DsStore) : Call list =
        childrenOf store.CallsReadOnly.Values workId (fun c -> c.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // ApiDef 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiDef ID로 ApiDef 조회</summary>
    let getApiDef (id: Guid) (store: DsStore) = byId store.ApiDefs id

    /// <summary>모든 ApiDef 조회</summary>
    let allApiDefs (store: DsStore) : ApiDef list = allOf store.ApiDefsReadOnly

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

    /// <summary>특정 Flow에 속한 ArrowBetweenWorks 조회</summary>
    let arrowWorksOf (flowId: Guid) (store: DsStore) : ArrowBetweenWorks list =
        childrenOf store.ArrowWorksReadOnly.Values flowId (fun a -> a.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenCalls 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenCalls ID로 Arrow 조회</summary>
    let getArrowCall (id: Guid) (store: DsStore) = byId store.ArrowCalls id

    /// <summary>모든 ArrowBetweenCalls 조회</summary>
    let allArrowCalls (store: DsStore) : ArrowBetweenCalls list = allOf store.ArrowCallsReadOnly

    /// <summary>특정 Flow에 속한 ArrowBetweenCalls 조회</summary>
    let arrowCallsOf (flowId: Guid) (store: DsStore) : ArrowBetweenCalls list =
        childrenOf store.ArrowCallsReadOnly.Values flowId (fun a -> a.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // HwButton 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwButton ID로 Button 조회</summary>
    let getButton (id: Guid) (store: DsStore) = byId store.HwButtons id

    /// <summary>모든 HwButton 조회</summary>
    let allButtons (store: DsStore) : HwButton list = allOf store.HwButtonsReadOnly

    /// <summary>특정 DsSystem에 속한 HwButton들 조회</summary>
    let buttonsOf (systemId: Guid) (store: DsStore) : HwButton list =
        childrenOf store.HwButtonsReadOnly.Values systemId (fun b -> b.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // HwLamp 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwLamp ID로 Lamp 조회</summary>
    let getLamp (id: Guid) (store: DsStore) = byId store.HwLamps id

    /// <summary>모든 HwLamp 조회</summary>
    let allLamps (store: DsStore) : HwLamp list = allOf store.HwLampsReadOnly

    /// <summary>특정 DsSystem에 속한 HwLamp들 조회</summary>
    let lampsOf (systemId: Guid) (store: DsStore) : HwLamp list =
        childrenOf store.HwLampsReadOnly.Values systemId (fun l -> l.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // HwCondition 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwCondition ID로 Condition 조회</summary>
    let getCondition (id: Guid) (store: DsStore) = byId store.HwConditions id

    /// <summary>모든 HwCondition 조회</summary>
    let allConditions (store: DsStore) : HwCondition list = allOf store.HwConditionsReadOnly

    /// <summary>특정 DsSystem에 속한 HwCondition들 조회</summary>
    let conditionsOf (systemId: Guid) (store: DsStore) : HwCondition list =
        childrenOf store.HwConditionsReadOnly.Values systemId (fun c -> c.ParentId)

    // ─────────────────────────────────────────────────────────────────────────
    // HwAction 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwAction ID로 Action 조회</summary>
    let getAction (id: Guid) (store: DsStore) = byId store.HwActions id

    /// <summary>모든 HwAction 조회</summary>
    let allActions (store: DsStore) : HwAction list = allOf store.HwActionsReadOnly

    /// <summary>특정 DsSystem에 속한 HwAction들 조회</summary>
    let actionsOf (systemId: Guid) (store: DsStore) : HwAction list =
        childrenOf store.HwActionsReadOnly.Values systemId (fun a -> a.ParentId)
