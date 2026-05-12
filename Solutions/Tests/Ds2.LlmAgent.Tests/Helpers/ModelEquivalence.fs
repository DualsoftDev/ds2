module ModelEquivalence

open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// Phase 1 YAML protocol round-trip 의미-동등 비교 (todo §3.1 Step 9, M13).
///
/// **비교 범위**: entity 개수 + 이름 + 부모-자식 관계 + arrow source/target/type.
/// **무시**: GUID 표면 값, anchor / comment / key 순서 / whitespace / duration 형식 표기 차이.
///
/// 두 store 가 *의미적으로 동일한 모델* 인지 — apply(export(model)) ≡ model 의 검증.

type SystemShape = {
    Name: string
    IsActive: bool
    SystemType: string option
    FlowNames: string Set
    /// flow 가 같은 이름으로 여러 개 생기는 회귀 detect 위해 count 별도 비교 (M1 fix).
    FlowCount: int
    ApiDefNames: string Set
    ApiDefCount: int
}

type WorkShape = {
    LocalName: string
    FlowName: string
    SystemName: string
    CallNames: string list  // 순서 무관 비교를 위해 sort
}

type ArrowShape = {
    SourceLabel: string  // "{system}.{flow}.{work}" 형태로 안정 식별
    TargetLabel: string
    ArrowType: ArrowType
}

type StoreShape = {
    ProjectName: string option
    Systems: SystemShape Set
    Works: WorkShape Set
    WorkArrows: ArrowShape Set
    CallArrows: ArrowShape Set
}

let private systemNameOf (store: DsStore) (sysId: System.Guid) : string =
    match Queries.getSystem sysId store with
    | Some s -> s.Name
    | None -> sprintf "<unknown:%O>" sysId

let private workLabel (store: DsStore) (workId: System.Guid) : string =
    match Queries.getWork workId store with
    | Some w ->
        match Queries.getFlow w.ParentId store with
        | Some f -> sprintf "%s.%s.%s" (systemNameOf store f.ParentId) f.Name w.LocalName
        | None -> sprintf "<orphanFlow>.%s" w.LocalName
    | None -> sprintf "<unknown:%O>" workId

let private callLabel (store: DsStore) (callId: System.Guid) : string =
    match Queries.getCall callId store with
    | Some c ->
        match Queries.getWork c.ParentId store with
        | Some w -> sprintf "%s|%s.%s" (workLabel store w.Id) c.DevicesAlias c.ApiName
        | None -> sprintf "<orphanWork>|%s.%s" c.DevicesAlias c.ApiName
    | None -> sprintf "<unknown:%O>" callId

let captureShape (store: DsStore) : StoreShape =
    let projects = Queries.allProjects store
    match projects with
    | [] ->
        { ProjectName = None
          Systems = Set.empty
          Works = Set.empty
          WorkArrows = Set.empty
          CallArrows = Set.empty }
    | p :: _ ->
        let actives = Queries.activeSystemsOf p.Id store
        let passives = Queries.passiveSystemsOf p.Id store
        let allSystems =
            (actives |> List.map (fun s -> s, true))
            @ (passives |> List.map (fun s -> s, false))

        let systems =
            allSystems
            |> List.map (fun (s, isActive) ->
                let flows = Queries.flowsOf s.Id store
                let apiDefs = Queries.apiDefsOf s.Id store
                {
                    Name = s.Name
                    IsActive = isActive
                    SystemType = s.SystemType
                    FlowNames = flows |> List.map (fun f -> f.Name) |> Set.ofList
                    FlowCount = flows.Length
                    ApiDefNames = apiDefs |> List.map (fun d -> d.Name) |> Set.ofList
                    ApiDefCount = apiDefs.Length
                })
            |> Set.ofList

        let works =
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.flowsOf s.Id store
                |> List.collect (fun f ->
                    Queries.worksOf f.Id store
                    |> List.map (fun w ->
                        let callNames =
                            Queries.callsOf w.Id store
                            |> List.map (fun c -> sprintf "%s.%s" c.DevicesAlias c.ApiName)
                            |> List.sort
                        {
                            LocalName = w.LocalName
                            FlowName = f.Name
                            SystemName = s.Name
                            CallNames = callNames
                        })))
            |> Set.ofList

        let workArrows =
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.arrowWorksOf s.Id store
                |> List.map (fun a ->
                    {
                        SourceLabel = workLabel store a.SourceId
                        TargetLabel = workLabel store a.TargetId
                        ArrowType = a.ArrowType
                    }))
            |> Set.ofList

        // Phase 2.5 m6: allArrowCalls 1회 호출 후 ParentId 별 그룹 캐싱. work 마다 전체 enumerate (O(N×M)) 회피.
        let callArrowsByParent =
            Queries.allArrowCalls store
            |> List.groupBy (fun a -> a.ParentId)
            |> Map.ofList

        let callArrows =
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.flowsOf s.Id store
                |> List.collect (fun f ->
                    Queries.worksOf f.Id store
                    |> List.collect (fun w ->
                        callArrowsByParent
                        |> Map.tryFind w.Id
                        |> Option.defaultValue []
                        |> List.map (fun a ->
                            {
                                SourceLabel = callLabel store a.SourceId
                                TargetLabel = callLabel store a.TargetId
                                ArrowType = a.ArrowType
                            }))))
            |> Set.ofList

        {
            ProjectName = Some p.Name
            Systems = systems
            Works = works
            WorkArrows = workArrows
            CallArrows = callArrows
        }

/// 두 store shape 가 의미-동등인지. mismatch 발견 시 차이 메시지 list 반환 (빈 list = 동등).
let diff (a: StoreShape) (b: StoreShape) : string list =
    let diffs = ResizeArray()
    if a.ProjectName <> b.ProjectName then
        diffs.Add(sprintf "ProjectName: %A ≠ %A" a.ProjectName b.ProjectName)
    if a.Systems <> b.Systems then
        let onlyA = Set.difference a.Systems b.Systems
        let onlyB = Set.difference b.Systems a.Systems
        if not (Set.isEmpty onlyA) then diffs.Add(sprintf "Systems only in A: %A" (Set.toList onlyA))
        if not (Set.isEmpty onlyB) then diffs.Add(sprintf "Systems only in B: %A" (Set.toList onlyB))
    if a.Works <> b.Works then
        let onlyA = Set.difference a.Works b.Works
        let onlyB = Set.difference b.Works a.Works
        if not (Set.isEmpty onlyA) then diffs.Add(sprintf "Works only in A: %A" (Set.toList onlyA))
        if not (Set.isEmpty onlyB) then diffs.Add(sprintf "Works only in B: %A" (Set.toList onlyB))
    if a.WorkArrows <> b.WorkArrows then
        let onlyA = Set.difference a.WorkArrows b.WorkArrows
        let onlyB = Set.difference b.WorkArrows a.WorkArrows
        if not (Set.isEmpty onlyA) then diffs.Add(sprintf "WorkArrows only in A: %A" (Set.toList onlyA))
        if not (Set.isEmpty onlyB) then diffs.Add(sprintf "WorkArrows only in B: %A" (Set.toList onlyB))
    if a.CallArrows <> b.CallArrows then
        let onlyA = Set.difference a.CallArrows b.CallArrows
        let onlyB = Set.difference b.CallArrows a.CallArrows
        if not (Set.isEmpty onlyA) then diffs.Add(sprintf "CallArrows only in A: %A" (Set.toList onlyA))
        if not (Set.isEmpty onlyB) then diffs.Add(sprintf "CallArrows only in B: %A" (Set.toList onlyB))
    diffs |> List.ofSeq

// ─── Phase 2.5 m1 — RelaxedShape (StoreShape 의 부분 projection) ─────────────
//
// 카운트 + 관계 위주 비교용. cylinder sugar canonical 의 internal Flow 이름은 동등 비교에서 제외.
// Passive flowNames 만 빼고 비교하므로 GUI fixture vs export round-trip 의 *완화* 의미-동등 검증에 적합.
// 이전 `ModelProtocolTests.fs` 의 private RelaxedShape 정의를 흡수 (정보 중복 제거).

type RelaxedShape = {
    ProjectName: string option
    SystemNames: string Set
    ActiveSystemFlowNames: Map<string, string Set>
    PassiveSystemApiDefNames: Map<string, string Set>
    WorkLocalNames: Map<string, string Set>
    WorkArrowsByType: Map<string, int>
}

let captureRelaxed (store: DsStore) : RelaxedShape =
    match Queries.allProjects store with
    | [] ->
        { ProjectName = None
          SystemNames = Set.empty
          ActiveSystemFlowNames = Map.empty
          PassiveSystemApiDefNames = Map.empty
          WorkLocalNames = Map.empty
          WorkArrowsByType = Map.empty }
    | p :: _ ->
        let actives = Queries.activeSystemsOf p.Id store
        let passives = Queries.passiveSystemsOf p.Id store
        let allSystems =
            (actives |> List.map (fun s -> s, true))
            @ (passives |> List.map (fun s -> s, false))

        let sysNames = allSystems |> List.map (fun (s, _) -> s.Name) |> Set.ofList

        let activeFlowNames =
            actives
            |> List.map (fun s ->
                s.Name, Queries.flowsOf s.Id store |> List.map (fun f -> f.Name) |> Set.ofList)
            |> Map.ofList

        let passiveApiNames =
            passives
            |> List.map (fun s ->
                s.Name, Queries.apiDefsOf s.Id store |> List.map (fun d -> d.Name) |> Set.ofList)
            |> Map.ofList

        let workLocalsBySystem =
            allSystems
            |> List.map (fun (s, _) ->
                let locals =
                    Queries.flowsOf s.Id store
                    |> List.collect (fun f -> Queries.worksOf f.Id store)
                    |> List.map (fun w -> w.LocalName)
                    |> Set.ofList
                s.Name, locals)
            |> Map.ofList

        // Phase 2.5 m7: ArrowType key 직렬화는 `ModelProtocol.formatArrowType` (SSOT §2.4) 재활용.
        // 이전의 `%A` 표현은 컴파일러 fallback 표기와 silent 일치 — 회귀 fence 부재.
        let arrowsByType =
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.arrowWorksOf s.Id store
                |> List.map (fun a -> sprintf "%s|%s" s.Name (ModelProtocol.formatArrowType a.ArrowType)))
            |> List.countBy id
            |> Map.ofList

        { ProjectName = Some p.Name
          SystemNames = sysNames
          ActiveSystemFlowNames = activeFlowNames
          PassiveSystemApiDefNames = passiveApiNames
          WorkLocalNames = workLocalsBySystem
          WorkArrowsByType = arrowsByType }

// ─── Phase 2.5 m3 + cycle2 M3 — round-trip helper ────────────────────────────
//
// `exportToJson → apply → project × 2 → 동등 검증` pattern 흡수.
// projection 함수 (captureShape / captureRelaxed) 를 generic 인자로 받아 두 비교 방식 모두 적용 가능.

/// 임의 projection 으로 round-trip 의미-동등 검증. before/after 한 쌍 반환.
let roundTripWith (project: DsStore -> 'T) (store: DsStore) : 'T * 'T =
    let before = project store
    use exported = ModelProtocol.exportToJson store
    let store2 = DsStore()
    let plan = ImportPlanBuilder()
    let diag, _ = ModelProtocol.apply plan store2 exported.RootElement
    if diag.HasErrors then
        failwithf "round-trip apply diagnostics: %s" (diag.Format())
    store2.ApplyImportPlan("round-trip helper", plan.Build())
    let after = project store2
    before, after

/// StoreShape 기반 round-trip — 정확 의미-동등 비교 (cascade 자식 이름 포함).
let roundTripShape (store: DsStore) : StoreShape * StoreShape =
    roundTripWith captureShape store

/// RelaxedShape 기반 round-trip — Passive cascade internal Flow 이름 무시 (GUI fixture 호환).
let roundTripRelaxed (store: DsStore) : RelaxedShape * RelaxedShape =
    roundTripWith captureRelaxed store
