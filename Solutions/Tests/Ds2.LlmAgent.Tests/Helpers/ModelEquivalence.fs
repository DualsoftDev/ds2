module ModelEquivalence

open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

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

        let callArrows =
            // ArrowBetweenCalls 은 work 단위 — 모든 work 순회.
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.flowsOf s.Id store
                |> List.collect (fun f ->
                    Queries.worksOf f.Id store
                    |> List.collect (fun w ->
                        // arrowCallsOf 가 있는지 확인 — 없으면 빈 list.
                        Queries.allArrowCalls store
                        |> List.filter (fun a -> a.ParentId = w.Id)
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
