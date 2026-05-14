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
    IRI: string option                     // Phase 7 §4.2 C-6
    FlowNames: string Set
    /// flow 가 같은 이름으로 여러 개 생기는 회귀 detect 위해 count 별도 비교 (M1 fix).
    FlowCount: int
    ApiDefNames: string Set
    ApiDefCount: int
    /// Phase 7 §4.2 C-5: ApiDef 별 보강 property (`actionType` / `description`) 평탄화.
    /// key = ApiDef.Name, value = "<actionType>|<description>" — 직렬화.
    ApiDefDetails: Map<string, string>
}

/// Phase 7 §4.2 TC-1 — call 별 보강 property 평탄화. 본 type 이 false-positive 회귀의
/// 핵심 가드 (emit / apply 양쪽 동시 누락 시 shape diff 0 회피).
type CallDetail = {
    Ref: string                            // "{devicesAlias}.{apiName}"
    /// todo §10.2 #8 — `ApiCalls[0]` 1:1 매핑 PoC scope invariant guard.
    /// PoC scope = 0 또는 1. multi-ApiCall (≥ 2) 확장 시 round-trip diff 로 즉시 가시화 → silent regression 차단.
    ApiCallCount: int
    ContactKind: ContactKind
    SkipInputSensor: bool
    CallType: CallType option              // None = Properties 미설정
    InTag: (string * string) option        // (Name, Address) — None = IOTag 미설정 or empty
    OutTag: (string * string) option
    /// CallCondition recursive 평탄화 (sorted). 빈 콜렉션 → "".
    CallConditionSummary: string
}

type WorkShape = {
    LocalName: string
    FlowName: string
    SystemName: string
    Calls: CallDetail list                 // 순서 무관 비교를 위해 Ref 기준 sort (Phase 7 §4.2 TC-1)
    TokenRole: TokenRole                   // Phase 7 §4.2 C-6
}

type ArrowShape = {
    SourceLabel: string  // "{system}.{flow}.{work}" 형태로 안정 식별
    TargetLabel: string
    ArrowType: ArrowType
}

type StoreShape = {
    ProjectName: string option
    ProjectAuthor: string                  // Phase 7 §4.2 C-6
    ProjectVersion: string                 // Phase 7 §4.2 C-6
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

/// Phase 7 §4.2 TC-1 — ApiCall (CallCondition.Conditions leaf) 의 이름 기반 label.
/// GUID lossy (4-set) 회피 — system+api 이름 기반.
let private apiCallLabel (store: DsStore) (ac: ApiCall) : string =
    match ac.ApiDefId with
    | Some apiId ->
        match Queries.getApiDef apiId store with
        | Some apiDef ->
            match Queries.getSystem apiDef.ParentId store with
            | Some sys -> sprintf "%s.%s" sys.Name apiDef.Name
            | None -> sprintf "<orphan>.%s" apiDef.Name
        | None -> "<unknownApiDef>"
    | None -> "<noApiDef>"

/// Phase 7 §4.2 TC-1 — CallCondition recursive 평탄화. 비교용 직렬화 string.
let rec private summarizeCallCondition (store: DsStore) (cc: CallCondition) : string =
    let typ = cc.Type |> Option.map (sprintf "%A") |> Option.defaultValue "None"
    let conds =
        cc.Conditions
        |> Seq.map (fun ac -> sprintf "%s|%A" (apiCallLabel store ac) ac.ContactKind)
        |> Seq.toList
        |> List.sort
        |> String.concat ","
    let children =
        cc.Children
        |> Seq.map (summarizeCallCondition store)
        |> Seq.toList
        |> List.sort
        |> String.concat ";"
    sprintf "{%s,%b,%b,[%s],[%s]}" typ cc.IsOR cc.IsInverted conds children

/// Phase 7 §4.2 TC-1 — Call.CallConditions 전체 평탄화. 빈 콜렉션 → "" (default 케이스).
let private summarizeCallConditions (store: DsStore) (c: Call) : string =
    if c.CallConditions.Count = 0 then ""
    else
        c.CallConditions
        |> Seq.map (summarizeCallCondition store)
        |> Seq.toList
        |> List.sort
        |> String.concat "+"

/// Phase 7 §4.2 TC-1 — IOTag instance → (Name, Address) tuple. 빈 IOTag (Name+Address 둘 다 빈) 는 None.
let private ioTagTuple (tagOpt: IOTag option) : (string * string) option =
    tagOpt
    |> Option.bind (fun t ->
        if System.String.IsNullOrEmpty(t.Name) && System.String.IsNullOrEmpty(t.Address) then None
        else Some (t.Name, t.Address))

/// Phase 7 §4.2 TC-1 — Call → CallDetail 평탄화. ApiCalls 는 1:1 매핑 PoC scope (`ApiCalls.[0]` 만).
let private callDetailOf (store: DsStore) (c: Call) : CallDetail =
    let firstApiCall = if c.ApiCalls.Count > 0 then Some c.ApiCalls.[0] else None
    let contactKind = firstApiCall |> Option.map (fun ac -> ac.ContactKind) |> Option.defaultValue ContactKind.NoContact
    let skipInputSensor = firstApiCall |> Option.map (fun ac -> ac.SkipInputSensor) |> Option.defaultValue false
    let inTag = firstApiCall |> Option.bind (fun ac -> ioTagTuple ac.InTag)
    let outTag = firstApiCall |> Option.bind (fun ac -> ioTagTuple ac.OutTag)
    let callTypeOpt =
        c.Properties
        |> Seq.tryPick (function | SimulationCall props -> Some props.CallType | _ -> None)
    {
        Ref = sprintf "%s.%s" c.DevicesAlias c.ApiName
        ApiCallCount = c.ApiCalls.Count
        ContactKind = contactKind
        SkipInputSensor = skipInputSensor
        CallType = callTypeOpt
        InTag = inTag
        OutTag = outTag
        CallConditionSummary = summarizeCallConditions store c
    }

/// Phase 7 §4.2 TC-1 — ApiDef → ("<actionType>|<description>") 평탄화.
let private apiDefDetail (apiDef: ApiDef) : string =
    let actionType = sprintf "%A" apiDef.ApiDefActionType
    let description = apiDef.Description |> Option.defaultValue ""
    sprintf "%s|%s" actionType description

let captureShape (store: DsStore) : StoreShape =
    let projects = Queries.allProjects store
    match projects with
    | [] ->
        { ProjectName = None
          ProjectAuthor = ""
          ProjectVersion = ""
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
                let apiDefDetails =
                    apiDefs
                    |> List.map (fun d -> d.Name, apiDefDetail d)
                    |> Map.ofList
                {
                    Name = s.Name
                    IsActive = isActive
                    SystemType = s.SystemType
                    IRI = s.IRI
                    FlowNames = flows |> List.map (fun f -> f.Name) |> Set.ofList
                    FlowCount = flows.Length
                    ApiDefNames = apiDefs |> List.map (fun d -> d.Name) |> Set.ofList
                    ApiDefCount = apiDefs.Length
                    ApiDefDetails = apiDefDetails
                })
            |> Set.ofList

        let works =
            allSystems
            |> List.collect (fun (s, _) ->
                Queries.flowsOf s.Id store
                |> List.collect (fun f ->
                    Queries.worksOf f.Id store
                    |> List.map (fun w ->
                        let calls =
                            Queries.callsOf w.Id store
                            |> List.map (callDetailOf store)
                            |> List.sortBy (fun cd -> cd.Ref)
                        {
                            LocalName = w.LocalName
                            FlowName = f.Name
                            SystemName = s.Name
                            Calls = calls
                            TokenRole = w.TokenRole
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
            ProjectAuthor = p.Author
            ProjectVersion = p.Version
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
    if a.ProjectAuthor <> b.ProjectAuthor then
        diffs.Add(sprintf "ProjectAuthor: '%s' ≠ '%s'" a.ProjectAuthor b.ProjectAuthor)
    if a.ProjectVersion <> b.ProjectVersion then
        diffs.Add(sprintf "ProjectVersion: '%s' ≠ '%s'" a.ProjectVersion b.ProjectVersion)
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
