namespace Ds2.LlmAgent

open System
open Ds2.Core
open Ds2.Core.Store

/// LLM mutation/read tool handler 가 호출하는 F# 측 helper.
///
/// 결정 7 (d): mutation = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적. turn end `ApplyImportPlan` 1회.
/// `Ds2.Core` 의 entity ctor (e.g. `DsSystem`) 가 internal 이라 C# tool method 가 직접 호출 불가 →
/// 본 module 이 F# 측 wrapper.
///
/// **Plan + Store 합산 검색**: 같은 turn 안에서 add_flow → add_work 처럼 의존하는 호출이 연속될 때
/// 새 Flow 는 store 에 아직 없고 plan 에만 있다. 각 queue 함수는 parent existence / 이름 중복을
/// store 와 plan 의 누적 operation 양쪽에서 동시에 검사한다.
[<RequireQualifiedAccess>]
module ToolOperations =

    // ─── Plan + Store 합산 lookup 헬퍼 ───────────────────────────────────────

    let private tryFindSystemInPlan (plan: ImportPlanBuilder) (id: Guid) : DsSystem option =
        plan.Operations
        |> Seq.tryPick (function AddSystem s when s.Id = id -> Some s | _ -> None)

    let private tryFindFlowInPlan (plan: ImportPlanBuilder) (id: Guid) : Flow option =
        plan.Operations
        |> Seq.tryPick (function AddFlow f when f.Id = id -> Some f | _ -> None)

    let private tryFindWorkInPlan (plan: ImportPlanBuilder) (id: Guid) : Work option =
        plan.Operations
        |> Seq.tryPick (function AddWork w when w.Id = id -> Some w | _ -> None)

    let private tryFindCallInPlan (plan: ImportPlanBuilder) (id: Guid) : Call option =
        plan.Operations
        |> Seq.tryPick (function AddCall c when c.Id = id -> Some c | _ -> None)

    let private requireSystem (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : DsSystem =
        match Queries.getSystem id store with
        | Some s -> s
        | None ->
            match tryFindSystemInPlan plan id with
            | Some s -> s
            | None -> invalidOp $"System(id={id}) 가 존재하지 않습니다."

    let private requireFlow (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : Flow =
        match Queries.getFlow id store with
        | Some f -> f
        | None ->
            match tryFindFlowInPlan plan id with
            | Some f -> f
            | None -> invalidOp $"Flow(id={id}) 가 존재하지 않습니다."

    let private requireWork (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : Work =
        match Queries.getWork id store with
        | Some w -> w
        | None ->
            match tryFindWorkInPlan plan id with
            | Some w -> w
            | None -> invalidOp $"Work(id={id}) 가 존재하지 않습니다."

    /// Plan + store 합산: 특정 system 내 Flow 이름 중복?
    let private hasFlowNameClash (plan: ImportPlanBuilder) (store: DsStore) (systemId: Guid) (name: string) : bool =
        let inStore = not (Queries.isFlowNameUniqueInSystem systemId name None store)
        let inPlan =
            plan.Operations
            |> Seq.exists (function
                | AddFlow f -> f.ParentId = systemId && f.Name = name
                | _ -> false)
        inStore || inPlan

    /// Plan + store 합산: 특정 flow 내 Work LocalName 중복?
    let private hasWorkLocalNameClash (plan: ImportPlanBuilder) (store: DsStore) (flowId: Guid) (localName: string) : bool =
        let inStore = not (Queries.isLocalNameUniqueInFlow flowId localName None store)
        let inPlan =
            plan.Operations
            |> Seq.exists (function
                | AddWork w -> w.ParentId = flowId && w.LocalName = localName
                | _ -> false)
        inStore || inPlan

    /// Plan + store 합산: 특정 work 내 Call 이름(DevicesAlias.ApiName) 중복?
    let private hasCallNameClash (plan: ImportPlanBuilder) (store: DsStore) (workId: Guid) (fullName: string) : bool =
        let inStore = not (Queries.isCallNameUniqueInWork workId fullName None store)
        let inPlan =
            plan.Operations
            |> Seq.exists (function
                | AddCall c -> c.ParentId = workId && c.Name = fullName
                | _ -> false)
        inStore || inPlan

    /// Plan + store 합산: 특정 system 내 ApiDef 이름 중복?
    let private hasApiDefNameClash (plan: ImportPlanBuilder) (store: DsStore) (systemId: Guid) (name: string) : bool =
        let inStore =
            Queries.apiDefsOf systemId store
            |> List.exists (fun d -> d.Name = name)
        let inPlan =
            plan.Operations
            |> Seq.exists (function
                | AddApiDef d -> d.ParentId = systemId && d.Name = name
                | _ -> false)
        inStore || inPlan

    // ─── Mutation queue (Phase 1c~1d) ────────────────────────────────────────

    /// add_system mutation tool 의 plan 누적 wrapper.
    ///
    /// **현재 phase 1c 단순화**: 첫 번째 project 에 자동 부착. project 가 0개면 invalidOp.
    /// (phase 1d 에서 projectId 인자 또는 selection 기반 resolve)
    /// 반환: 새로 생성된 system Id (LLM 응답에 포함).
    let queueAddSystem (plan: ImportPlanBuilder) (store: DsStore) (name: string) (isActive: bool) : Guid =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "System name 이 비어있습니다."
        let project =
            match Queries.allProjects store with
            | [] -> invalidOp "프로젝트가 없습니다. 먼저 프로젝트를 생성하세요."
            | p :: _ -> p
        let sys = DsSystem(name)
        plan.Add(AddSystem sys)
        plan.Add(LinkSystemToProject(project.Id, sys.Id, isActive))
        sys.Id

    /// add_flow mutation tool. parent System 은 store 또는 plan 에 존재해야 함.
    let queueAddFlow (plan: ImportPlanBuilder) (store: DsStore) (name: string) (systemId: Guid) : Guid =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Flow name 이 비어있습니다."
        requireSystem plan store systemId |> ignore
        if hasFlowNameClash plan store systemId name then
            invalidOp $"같은 System 내에 이미 '{name}' Flow 가 존재합니다."
        let flow = Flow(name, systemId)
        plan.Add(AddFlow flow)
        flow.Id

    /// add_work mutation tool. parent Flow 는 store 또는 plan 에 존재해야 함.
    /// Work.Name = "{flow.Name}.{localName}" 으로 자동 조립 (Work 의 FlowPrefix = flow.Name).
    let queueAddWork (plan: ImportPlanBuilder) (store: DsStore) (localName: string) (flowId: Guid) : Guid =
        if String.IsNullOrWhiteSpace(localName) then
            invalidArg (nameof localName) "Work localName 이 비어있습니다."
        let flow = requireFlow plan store flowId
        if hasWorkLocalNameClash plan store flowId localName then
            invalidOp $"같은 Flow 내에 이미 '{localName}' Work 가 존재합니다."
        let work = Work(flow.Name, localName, flowId)
        plan.Add(AddWork work)
        work.Id

    /// add_call mutation tool. parent Work 는 store 또는 plan 에 존재해야 함.
    /// Call.Name = "{devicesAlias}.{apiName}" 자동 조립.
    let queueAddCall (plan: ImportPlanBuilder) (store: DsStore) (devicesAlias: string) (apiName: string) (workId: Guid) : Guid =
        if String.IsNullOrWhiteSpace(devicesAlias) then
            invalidArg (nameof devicesAlias) "Call devicesAlias 가 비어있습니다."
        if String.IsNullOrWhiteSpace(apiName) then
            invalidArg (nameof apiName) "Call apiName 이 비어있습니다."
        requireWork plan store workId |> ignore
        let fullName = $"{devicesAlias}.{apiName}"
        if hasCallNameClash plan store workId fullName then
            invalidOp $"같은 Work 내에 이미 '{fullName}' Call 이 존재합니다."
        let call = Call(devicesAlias, apiName, workId)
        plan.Add(AddCall call)
        call.Id

    /// add_api_def mutation tool. parent System 존재 + 이름 중복 검사.
    let queueAddApiDef (plan: ImportPlanBuilder) (store: DsStore) (name: string) (systemId: Guid) : Guid =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "ApiDef name 이 비어있습니다."
        requireSystem plan store systemId |> ignore
        if hasApiDefNameClash plan store systemId name then
            invalidOp $"같은 System 내에 이미 '{name}' ApiDef 가 존재합니다."
        let apiDef = ApiDef(name, systemId)
        plan.Add(AddApiDef apiDef)
        apiDef.Id

    /// add_arrow mutation tool.
    /// source/target 이 모두 Work → ArrowBetweenWorks(parentId=systemId), 같은 system 에 속해야 함.
    /// source/target 이 모두 Call → ArrowBetweenCalls(parentId=workId), 같은 work 에 속해야 함.
    /// 종류가 섞이면 invalidOp.
    /// 반환: (newArrowId, kind) — kind = "work" 또는 "call".
    let queueAddArrow
        (plan: ImportPlanBuilder) (store: DsStore)
        (sourceId: Guid) (targetId: Guid) (arrowType: ArrowType) : Guid * string =
        if sourceId = targetId then
            invalidOp "Arrow 의 source 와 target 이 같습니다."

        let srcWorkOpt = Queries.getWork sourceId store |> Option.orElseWith (fun () -> tryFindWorkInPlan plan sourceId)
        let tgtWorkOpt = Queries.getWork targetId store |> Option.orElseWith (fun () -> tryFindWorkInPlan plan targetId)
        let srcCallOpt = Queries.getCall sourceId store |> Option.orElseWith (fun () -> tryFindCallInPlan plan sourceId)
        let tgtCallOpt = Queries.getCall targetId store |> Option.orElseWith (fun () -> tryFindCallInPlan plan targetId)

        match srcWorkOpt, tgtWorkOpt, srcCallOpt, tgtCallOpt with
        | Some srcW, Some tgtW, _, _ ->
            // ArrowBetweenWorks: parent = systemId (work → flow → system)
            let srcFlow = requireFlow plan store srcW.ParentId
            let tgtFlow = requireFlow plan store tgtW.ParentId
            if srcFlow.ParentId <> tgtFlow.ParentId then
                invalidOp "ArrowBetweenWorks: source 와 target 이 같은 System 에 속해야 합니다."
            let systemId = srcFlow.ParentId
            let arrow = ArrowBetweenWorks(systemId, sourceId, targetId, arrowType)
            plan.Add(AddArrowWork arrow)
            arrow.Id, "work"
        | _, _, Some srcC, Some tgtC ->
            if srcC.ParentId <> tgtC.ParentId then
                invalidOp "ArrowBetweenCalls: source 와 target 이 같은 Work 에 속해야 합니다."
            let arrow = ArrowBetweenCalls(srcC.ParentId, sourceId, targetId, arrowType)
            plan.Add(AddArrowCall arrow)
            arrow.Id, "call"
        | _ ->
            // 혼용 (한쪽은 Work, 다른쪽은 Call) 과 "어디에도 없음" 을 분리해서 LLM 에게 회복 단서 제공.
            let isMissing id =
                srcWorkOpt.IsNone && srcCallOpt.IsNone && id = sourceId
                || tgtWorkOpt.IsNone && tgtCallOpt.IsNone && id = targetId
            if isMissing sourceId then
                invalidOp $"Arrow source(id={sourceId}) 가 Work/Call 어디에도 존재하지 않습니다."
            elif isMissing targetId then
                invalidOp $"Arrow target(id={targetId}) 가 Work/Call 어디에도 존재하지 않습니다."
            else
                invalidOp "Arrow 의 source/target 은 모두 Work 이거나 모두 Call 이어야 합니다 (혼용 불가)."

    // ─── Read tool (Phase 1c~) ───────────────────────────────────────────────

    /// list_systems read tool. 모든 project 의 active + passive 시스템.
    /// 반환: (Id, Name, IsActive) tuple 의 목록.
    let listSystems (store: DsStore) : (Guid * string * bool) list =
        Queries.allProjects store
        |> List.collect (fun p ->
            let active  = Queries.activeSystemsOf  p.Id store |> List.map (fun s -> s.Id, s.Name, true)
            let passive = Queries.passiveSystemsOf p.Id store |> List.map (fun s -> s.Id, s.Name, false)
            active @ passive)
