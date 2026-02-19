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
    // Project 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Project ID로 Project 조회</summary>
    let getProject (id: Guid) (store: DsStore) : Project option =
        match store.Projects.TryGetValue(id) with
        | true, p -> Some p
        | false, _ -> None

    /// <summary>모든 Project 조회</summary>
    let allProjects (store: DsStore) : Project list =
        store.ProjectsReadOnly.Values |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // DsSystem 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>DsSystem ID로 DsSystem 조회</summary>
    let getSystem (id: Guid) (store: DsStore) : DsSystem option =
        match store.Systems.TryGetValue(id) with
        | true, s -> Some s
        | false, _ -> None

    /// <summary>모든 DsSystem 조회</summary>
    let allSystems (store: DsStore) : DsSystem list =
        store.SystemsReadOnly.Values |> Seq.toList

    /// <summary>특정 Project의 ActiveSystem 목록 조회 (순서 유지)</summary>
    let activeSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        match store.Projects.TryGetValue(projectId) with
        | true, project ->
            project.ActiveSystemIds
            |> Seq.choose (fun id -> getSystem id store)
            |> Seq.toList
        | false, _ -> []

    /// <summary>특정 Project의 PassiveSystem 목록 조회 (순서 유지)</summary>
    let passiveSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        match store.Projects.TryGetValue(projectId) with
        | true, project ->
            project.PassiveSystemIds
            |> Seq.choose (fun id -> getSystem id store)
            |> Seq.toList
        | false, _ -> []

    /// <summary>특정 Project의 모든 System 조회 (Active + Passive, 순서 유지)</summary>
    let projectSystemsOf (projectId: Guid) (store: DsStore) : DsSystem list =
        activeSystemsOf projectId store @ passiveSystemsOf projectId store

    // ─────────────────────────────────────────────────────────────────────────
    // Flow 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Flow ID로 Flow 조회</summary>
    let getFlow (id: Guid) (store: DsStore) : Flow option =
        match store.Flows.TryGetValue(id) with
        | true, f -> Some f
        | false, _ -> None

    /// <summary>모든 Flow 조회</summary>
    let allFlows (store: DsStore) : Flow list =
        store.FlowsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 Flow들 조회</summary>
    let flowsOf (systemId: Guid) (store: DsStore) : Flow list =
        store.FlowsReadOnly.Values
        |> Seq.filter (fun f -> f.ParentId = systemId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // Work 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Work ID로 Work 조회</summary>
    let getWork (id: Guid) (store: DsStore) : Work option =
        match store.Works.TryGetValue(id) with
        | true, w -> Some w
        | false, _ -> None

    /// <summary>모든 Work 조회</summary>
    let allWorks (store: DsStore) : Work list =
        store.WorksReadOnly.Values |> Seq.toList

    /// <summary>특정 Flow에 속한 Work들 조회</summary>
    let worksOf (flowId: Guid) (store: DsStore) : Work list =
        store.WorksReadOnly.Values
        |> Seq.filter (fun w -> w.ParentId = flowId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // Call 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Call ID로 Call 조회</summary>
    let getCall (id: Guid) (store: DsStore) : Call option =
        match store.Calls.TryGetValue(id) with
        | true, c -> Some c
        | false, _ -> None

    /// <summary>모든 Call 조회</summary>
    let allCalls (store: DsStore) : Call list =
        store.CallsReadOnly.Values |> Seq.toList

    /// <summary>특정 Work에 속한 Call들 조회</summary>
    let callsOf (workId: Guid) (store: DsStore) : Call list =
        store.CallsReadOnly.Values
        |> Seq.filter (fun c -> c.ParentId = workId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // ApiDef 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiDef ID로 ApiDef 조회</summary>
    let getApiDef (id: Guid) (store: DsStore) : ApiDef option =
        match store.ApiDefs.TryGetValue(id) with
        | true, a -> Some a
        | false, _ -> None

    /// <summary>모든 ApiDef 조회</summary>
    let allApiDefs (store: DsStore) : ApiDef list =
        store.ApiDefsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 ApiDef들 조회</summary>
    let apiDefsOf (systemId: Guid) (store: DsStore) : ApiDef list =
        store.ApiDefsReadOnly.Values
        |> Seq.filter (fun a -> a.ParentId = systemId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // ApiCall 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiCall ID로 ApiCall 조회</summary>
    let getApiCall (id: Guid) (store: DsStore) : ApiCall option =
        match store.ApiCalls.TryGetValue(id) with
        | true, ac -> Some ac
        | false, _ -> None

    /// <summary>모든 ApiCall 조회</summary>
    let allApiCalls (store: DsStore) : ApiCall list =
        store.ApiCallsReadOnly.Values |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenWorks 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenWorks ID로 Arrow 조회</summary>
    let getArrowWork (id: Guid) (store: DsStore) : ArrowBetweenWorks option =
        match store.ArrowWorks.TryGetValue(id) with
        | true, a -> Some a
        | false, _ -> None

    /// <summary>모든 ArrowBetweenWorks 조회</summary>
    let allArrowWorks (store: DsStore) : ArrowBetweenWorks list =
        store.ArrowWorksReadOnly.Values |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenCalls 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenCalls ID로 Arrow 조회</summary>
    let getArrowCall (id: Guid) (store: DsStore) : ArrowBetweenCalls option =
        match store.ArrowCalls.TryGetValue(id) with
        | true, a -> Some a
        | false, _ -> None

    /// <summary>모든 ArrowBetweenCalls 조회</summary>
    let allArrowCalls (store: DsStore) : ArrowBetweenCalls list =
        store.ArrowCallsReadOnly.Values |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // HwButton 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwButton ID로 Button 조회</summary>
    let getButton (id: Guid) (store: DsStore) : HwButton option =
        match store.HwButtons.TryGetValue(id) with
        | true, b -> Some b
        | false, _ -> None

    /// <summary>모든 HwButton 조회</summary>
    let allButtons (store: DsStore) : HwButton list =
        store.HwButtonsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 HwButton들 조회</summary>
    let buttonsOf (systemId: Guid) (store: DsStore) : HwButton list =
        store.HwButtonsReadOnly.Values
        |> Seq.filter (fun b -> b.ParentId = systemId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // HwLamp 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwLamp ID로 Lamp 조회</summary>
    let getLamp (id: Guid) (store: DsStore) : HwLamp option =
        match store.HwLamps.TryGetValue(id) with
        | true, l -> Some l
        | false, _ -> None

    /// <summary>모든 HwLamp 조회</summary>
    let allLamps (store: DsStore) : HwLamp list =
        store.HwLampsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 HwLamp들 조회</summary>
    let lampsOf (systemId: Guid) (store: DsStore) : HwLamp list =
        store.HwLampsReadOnly.Values
        |> Seq.filter (fun l -> l.ParentId = systemId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // HwCondition 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwCondition ID로 Condition 조회</summary>
    let getCondition (id: Guid) (store: DsStore) : HwCondition option =
        match store.HwConditions.TryGetValue(id) with
        | true, c -> Some c
        | false, _ -> None

    /// <summary>모든 HwCondition 조회</summary>
    let allConditions (store: DsStore) : HwCondition list =
        store.HwConditionsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 HwCondition들 조회</summary>
    let conditionsOf (systemId: Guid) (store: DsStore) : HwCondition list =
        store.HwConditionsReadOnly.Values
        |> Seq.filter (fun c -> c.ParentId = systemId)
        |> Seq.toList

    // ─────────────────────────────────────────────────────────────────────────
    // HwAction 쿼리
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwAction ID로 Action 조회</summary>
    let getAction (id: Guid) (store: DsStore) : HwAction option =
        match store.HwActions.TryGetValue(id) with
        | true, a -> Some a
        | false, _ -> None

    /// <summary>모든 HwAction 조회</summary>
    let allActions (store: DsStore) : HwAction list =
        store.HwActionsReadOnly.Values |> Seq.toList

    /// <summary>특정 DsSystem에 속한 HwAction들 조회</summary>
    let actionsOf (systemId: Guid) (store: DsStore) : HwAction list =
        store.HwActionsReadOnly.Values
        |> Seq.filter (fun a -> a.ParentId = systemId)
        |> Seq.toList