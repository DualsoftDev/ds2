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

    // ─── Tool 인자 sanitize (1d-4 C / Pass E) ────────────────────────────────
    //
    // RLO override (U+202E) / null byte / ZWJ (U+200D) / 제어문자 등 prompt injection 1차 방어.
    // 정상적인 entity 이름은 영문/한글/숫자/공백/일부 기호로 충분하며 Cc(Control)/Cf(Format) 가
    // 들어올 일은 spoofing / unicode bomb 시도뿐. 발견 시 codepoint 를 메시지에 포함해 LLM 회복 단서 제공.
    //
    // **C# interop 시그니처**: 빈 string "" = valid, 비어있지 않은 메시지 = error.
    // (Option<string> 반환 시 C# 측이 FSharpOption.get_IsSome 핸들 — sentinel 패턴이 호출 코드 단순화)

    /// 1d-4 C 의 default 길이 cap (System/Flow/Work/Call/ApiDef 이름 공통).
    [<Literal>]
    let NameMaxLength = 128

    /// Tool 인자 sanitize. 빈 string "" 반환 = valid, 메시지 반환 = invalid.
    let sanitizeName (value: string) (field: string) (maxLength: int) : string =
        if String.IsNullOrWhiteSpace(value) then
            $"VALIDATION_ERROR: {field} 이(가) 비어있습니다."
        else
            let trimmed = value.Trim()
            if trimmed.Length > maxLength then
                $"VALIDATION_ERROR: {field} 길이 {trimmed.Length} > {maxLength}."
            else
                let bad =
                    trimmed
                    |> Seq.tryFind (fun c ->
                        let cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                        cat = System.Globalization.UnicodeCategory.Control
                        || cat = System.Globalization.UnicodeCategory.Format)
                match bad with
                | Some c -> $"VALIDATION_ERROR: {field} 에 허용되지 않은 제어/format 문자 (U+{int c:X4}) 가 포함되어 있습니다."
                | None -> ""

    // ─── Plan + Store 합산 lookup 헬퍼 ───────────────────────────────────────

    /// 필수 string 인자 whitespace 검사 — invalidArg 메시지 형식 통일.
    /// 기존 5/6 케이스가 "{label} 이 비어있습니다." 형식이라 통일.
    let private requireNonEmpty (paramName: string) (value: string) (label: string) : unit =
        if String.IsNullOrWhiteSpace(value) then
            invalidArg paramName $"{label} 이 비어있습니다."

    /// plan.Operations 안에서 특정 ImportPlanOperation 패턴을 찾는 generic helper.
    /// `tryFindXxxInPlan` 4종의 본문이 picker 만 다르고 동일한 패턴이라 단일 진입점으로 통합.
    let private tryFindInPlan (plan: ImportPlanBuilder) (picker: ImportPlanOperation -> 'T option) : 'T option =
        plan.Operations |> Seq.tryPick picker

    /// store 우선 조회 → 없으면 plan 의 누적 operation 에서 fallback. None 이면 invalidOp.
    /// `requireSystem` / `requireFlow` / `requireWork` 의 공통 골격.
    let private requireFromStoreOrPlan
        (storeLookup: unit -> 'T option)
        (planLookup: unit -> 'T option)
        (notFoundMsg: string) : 'T =
        match storeLookup() with
        | Some x -> x
        | None ->
            match planLookup() with
            | Some x -> x
            | None -> invalidOp notFoundMsg

    let private tryFindSystemInPlan (plan: ImportPlanBuilder) (id: Guid) : DsSystem option =
        tryFindInPlan plan (function AddSystem s when s.Id = id -> Some s | _ -> None)

    let private tryFindFlowInPlan (plan: ImportPlanBuilder) (id: Guid) : Flow option =
        tryFindInPlan plan (function AddFlow f when f.Id = id -> Some f | _ -> None)

    let private tryFindWorkInPlan (plan: ImportPlanBuilder) (id: Guid) : Work option =
        tryFindInPlan plan (function AddWork w when w.Id = id -> Some w | _ -> None)

    let private tryFindCallInPlan (plan: ImportPlanBuilder) (id: Guid) : Call option =
        tryFindInPlan plan (function AddCall c when c.Id = id -> Some c | _ -> None)

    let private requireSystem (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : DsSystem =
        requireFromStoreOrPlan
            (fun () -> Queries.getSystem id store)
            (fun () -> tryFindSystemInPlan plan id)
            $"System(id={id}) 가 존재하지 않습니다."

    let private requireFlow (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : Flow =
        requireFromStoreOrPlan
            (fun () -> Queries.getFlow id store)
            (fun () -> tryFindFlowInPlan plan id)
            $"Flow(id={id}) 가 존재하지 않습니다."

    let private requireWork (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : Work =
        requireFromStoreOrPlan
            (fun () -> Queries.getWork id store)
            (fun () -> tryFindWorkInPlan plan id)
            $"Work(id={id}) 가 존재하지 않습니다."

    /// 같은 parent 내 이름 중복 검사 generic helper.
    /// store 측 / plan 측 두 path 의 `inStore || inPlan` 패턴을 단일화.
    let private hasNameClash
        (plan: ImportPlanBuilder)
        (storeHasClash: unit -> bool)
        (planMatcher: ImportPlanOperation -> bool) : bool =
        storeHasClash() || (plan.Operations |> Seq.exists planMatcher)

    /// Plan + store 합산: 특정 system 내 Flow 이름 중복?
    let private hasFlowNameClash (plan: ImportPlanBuilder) (store: DsStore) (systemId: Guid) (name: string) : bool =
        hasNameClash plan
            (fun () -> not (Queries.isFlowNameUniqueInSystem systemId name None store))
            (function AddFlow f -> f.ParentId = systemId && f.Name = name | _ -> false)

    /// Plan + store 합산: 특정 flow 내 Work LocalName 중복?
    let private hasWorkLocalNameClash (plan: ImportPlanBuilder) (store: DsStore) (flowId: Guid) (localName: string) : bool =
        hasNameClash plan
            (fun () -> not (Queries.isLocalNameUniqueInFlow flowId localName None store))
            (function AddWork w -> w.ParentId = flowId && w.LocalName = localName | _ -> false)

    /// Plan + store 합산: 특정 work 내 Call 이름(DevicesAlias.ApiName) 중복?
    let private hasCallNameClash (plan: ImportPlanBuilder) (store: DsStore) (workId: Guid) (fullName: string) : bool =
        hasNameClash plan
            (fun () -> not (Queries.isCallNameUniqueInWork workId fullName None store))
            (function AddCall c -> c.ParentId = workId && c.Name = fullName | _ -> false)

    /// Plan + store 합산: 특정 system 내 ApiDef 이름 중복?
    let private hasApiDefNameClash (plan: ImportPlanBuilder) (store: DsStore) (systemId: Guid) (name: string) : bool =
        hasNameClash plan
            (fun () -> Queries.apiDefsOf systemId store |> List.exists (fun d -> d.Name = name))
            (function AddApiDef d -> d.ParentId = systemId && d.Name = name | _ -> false)

    // ─── Mutation queue (Phase 1c~1d) ────────────────────────────────────────

    /// add_system mutation tool 의 plan 누적 wrapper.
    ///
    /// **현재 phase 1c 단순화**: 첫 번째 project 에 자동 부착. project 가 0개면 invalidOp.
    /// (phase 1d 에서 projectId 인자 또는 selection 기반 resolve)
    /// 반환: 새로 생성된 system Id (LLM 응답에 포함).
    let queueAddSystem (plan: ImportPlanBuilder) (store: DsStore) (name: string) (isActive: bool) : Guid =
        requireNonEmpty (nameof name) name "System name"
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
        requireNonEmpty (nameof name) name "Flow name"
        requireSystem plan store systemId |> ignore
        if hasFlowNameClash plan store systemId name then
            invalidOp $"같은 System 내에 이미 '{name}' Flow 가 존재합니다."
        let flow = Flow(name, systemId)
        plan.Add(AddFlow flow)
        flow.Id

    /// add_work mutation tool. parent Flow 는 store 또는 plan 에 존재해야 함.
    /// Work.Name = "{flow.Name}.{localName}" 으로 자동 조립 (Work 의 FlowPrefix = flow.Name).
    let queueAddWork (plan: ImportPlanBuilder) (store: DsStore) (localName: string) (flowId: Guid) : Guid =
        requireNonEmpty (nameof localName) localName "Work localName"
        let flow = requireFlow plan store flowId
        if hasWorkLocalNameClash plan store flowId localName then
            invalidOp $"같은 Flow 내에 이미 '{localName}' Work 가 존재합니다."
        let work = Work(flow.Name, localName, flowId)
        plan.Add(AddWork work)
        work.Id

    /// add_call mutation tool. parent Work 는 store 또는 plan 에 존재해야 함.
    /// Call.Name = "{devicesAlias}.{apiName}" 자동 조립.
    let queueAddCall (plan: ImportPlanBuilder) (store: DsStore) (devicesAlias: string) (apiName: string) (workId: Guid) : Guid =
        requireNonEmpty (nameof devicesAlias) devicesAlias "Call devicesAlias"
        requireNonEmpty (nameof apiName) apiName "Call apiName"
        requireWork plan store workId |> ignore
        let fullName = $"{devicesAlias}.{apiName}"
        if hasCallNameClash plan store workId fullName then
            invalidOp $"같은 Work 내에 이미 '{fullName}' Call 이 존재합니다."
        let call = Call(devicesAlias, apiName, workId)
        plan.Add(AddCall call)
        call.Id

    /// add_api_def mutation tool. parent System 존재 + 이름 중복 검사.
    let queueAddApiDef (plan: ImportPlanBuilder) (store: DsStore) (name: string) (systemId: Guid) : Guid =
        requireNonEmpty (nameof name) name "ApiDef name"
        requireSystem plan store systemId |> ignore
        if hasApiDefNameClash plan store systemId name then
            invalidOp $"같은 System 내에 이미 '{name}' ApiDef 가 존재합니다."
        let apiDef = ApiDef(name, systemId)
        plan.Add(AddApiDef apiDef)
        apiDef.Id

    // ─── Remove / Rename (Phase 2) ───────────────────────────────────────────
    //
    // Remove: cascade 는 Ds2.Editor 의 CascadeRemove.batchRemoveEntities 가 책임.
    //         LLM tool 측은 entity kind 자동 판별 + plan 누적만.
    // Rename: phase 2 첫 사이클은 System / ApiDef 만 지원. Flow/Work/Call 은 자식 (Work.FlowPrefix /
    //         Call.Name 합성) cascade 복잡도로 phase 후속.

    /// 같은 turn 안 plan 에 누적된 add_* operation 의 id 인지 검사 (회복 단서용).
    /// 본 함수는 진단 메시지 생성에만 사용 — remove 는 turn end 후 store 를 봐야 의미가 있다.
    let private addedInPlanKind (plan: ImportPlanBuilder) (id: Guid) : EntityKind option =
        plan.Operations
        |> Seq.tryPick (function
            | AddSystem s    when s.Id = id -> Some EntityKind.System
            | AddFlow f      when f.Id = id -> Some EntityKind.Flow
            | AddWork w      when w.Id = id -> Some EntityKind.Work
            | AddCall c      when c.Id = id -> Some EntityKind.Call
            | AddApiDef d    when d.Id = id -> Some EntityKind.ApiDef
            | _ -> None)

    /// remove_entity mutation tool. id 의 EntityKind 를 store dict 검색으로 자동 판별.
    /// **같은 turn 안 add 직후 remove 는 미지원** — turn end 까지 store 미반영이라 plan 안 add 와 remove 가
    /// 같은 id 로 누적되면 ImportPlanApply 가 add → remove 순으로 적용해 redundant op 가 됨. LLM 의 흔한
    /// 회복 패턴 ("방금 만든 것 지워") 을 명확히 차단하고 회복 단서 (turn 종료 후 retry) 제공.
    /// 반환: 판별된 EntityKind (LLM 응답 메시지에 포함).
    let queueRemoveEntity (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) : EntityKind =
        let kind =
            if   store.Projects.ContainsKey(id) then EntityKind.Project
            elif store.Systems.ContainsKey(id)  then EntityKind.System
            elif store.Flows.ContainsKey(id)    then EntityKind.Flow
            elif store.Works.ContainsKey(id)    then EntityKind.Work
            elif store.Calls.ContainsKey(id)    then EntityKind.Call
            elif store.ApiDefs.ContainsKey(id)  then EntityKind.ApiDef
            else
                match addedInPlanKind plan id with
                | Some addedKind ->
                    invalidOp (sprintf "Entity(id=%O, kind=%O) 는 같은 turn 안에서 방금 add_* 로 추가되어 store 에 아직 반영되지 않았습니다. 같은 turn 의 add 직후 remove 는 미지원입니다 — 이 turn 의 add 자체를 취소하려면 add_* tool 호출 자체를 하지 마세요. 이미 추가된 entity 를 제거하려면 응답을 마치고 다음 turn 에서 remove_entity 를 호출하세요." id addedKind)
                | None ->
                    invalidOp $"Entity(id={id}) 가 store 에 없습니다 (Project/System/Flow/Work/Call/ApiDef 어디에도 없음)."
        plan.Add(RemoveEntity(kind, id))
        kind

    /// rename_entity mutation tool. System / ApiDef 만 지원 (Flow/Work/Call 은 자식 cascade 복잡도로 보류).
    /// sanitize 는 호출자 (ModelTools) 가 끝낸 trimmed name 을 받음.
    /// ApiDef 는 같은 System 내 이름 중복 검사 (자기 자신 제외).
    /// 반환: 판별된 EntityKind.
    let queueRenameEntity (plan: ImportPlanBuilder) (store: DsStore) (id: Guid) (newName: string) : EntityKind =
        let kind =
            if   store.Systems.ContainsKey(id) then EntityKind.System
            elif store.ApiDefs.ContainsKey(id) then EntityKind.ApiDef
            else
                invalidOp $"Rename: id={id} 는 System 또는 ApiDef 가 아닙니다 (Phase 2 는 System/ApiDef 만 지원)."

        match kind with
        | EntityKind.ApiDef ->
            let parentSysId = store.ApiDefs.[id].ParentId
            let clash =
                Queries.apiDefsOf parentSysId store
                |> List.exists (fun d -> d.Id <> id && d.Name = newName)
            if clash then
                invalidOp $"같은 System 내에 이미 '{newName}' ApiDef 가 존재합니다."
        | _ -> ()  // System 은 phase 1 의 add_system 과 일관 (이름 중복 검사 생략)

        plan.Add(RenameEntity(kind, id, newName))
        kind

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

    /// list_projects read tool. project 별 system 합계 (active + passive).
    /// 빈 결과는 "프로젝트 자체 없음" 을 명시 — list_systems 가 빈 array 를 반환할 때
    /// "어느 project 에도 system 없음" vs "project 자체가 없음" 을 LLM 이 구분 가능.
    /// 반환: (Id, Name, totalSystems) tuple 의 목록.
    let listProjects (store: DsStore) : (Guid * string * int) list =
        Queries.allProjects store
        |> List.map (fun p ->
            let total = p.ActiveSystemIds.Count + p.PassiveSystemIds.Count
            p.Id, p.Name, total)

    /// list_projects 의 formatted plain text 변환 (LLM 응답 SSOT). 빈 결과는 "(no projects)".
    /// describe / validate / find 의 formatting 패턴과 동일 파일에서 관리하여 후속 schema 변경 시 한 곳에서.
    let formatProjectList (rows: (Guid * string * int) list) : string =
        if rows.IsEmpty then "(no projects)"
        else
            let sb = System.Text.StringBuilder()
            for (id, name, total) in rows do
                sb.AppendLine($"- {name} (id={id:D}, systems={total})") |> ignore
            sb.ToString().TrimEnd()

    /// list_systems read tool. 모든 project 의 active + passive 시스템.
    /// 반환: (Id, Name, IsActive) tuple 의 목록.
    let listSystems (store: DsStore) : (Guid * string * bool) list =
        Queries.allProjects store
        |> List.collect (fun p ->
            let active  = Queries.activeSystemsOf  p.Id store |> List.map (fun s -> s.Id, s.Name, true)
            let passive = Queries.passiveSystemsOf p.Id store |> List.map (fun s -> s.Id, s.Name, false)
            active @ passive)

    /// list_systems 의 formatted plain text. 빈 결과는 "(no systems)".
    let formatSystemList (rows: (Guid * string * bool) list) : string =
        if rows.IsEmpty then "(no systems)"
        else
            let sb = System.Text.StringBuilder()
            for (id, name, isActive) in rows do
                let activeMark = if isActive then "active" else "passive"
                sb.AppendLine($"- {name} (id={id:D}, {activeMark})") |> ignore
            sb.ToString().TrimEnd()

    /// find_by_name 의 formatted plain text. 50 초과면 "... (truncated at 50; refine the name)" footer.
    /// 빈 결과는 "(no matches)".
    let formatFindResults (rows: (EntityKind * Guid * string) list) : string =
        if rows.IsEmpty then "(no matches)"
        else
            let truncated = rows.Length > 50
            let visible = if truncated then rows |> List.truncate 50 else rows
            let sb = System.Text.StringBuilder()
            for (k, id, n) in visible do
                sb.AppendLine($"- {k} \"{n}\" (id={id:D})") |> ignore
            if truncated then
                sb.AppendLine("... (truncated at 50; refine the name)") |> ignore
            sb.ToString().TrimEnd()

    // ─── Read tool 풀세트 (Phase 1d-2) ───────────────────────────────────────
    //
    // 모든 read tool 은 indented plain text 를 반환한다 (JSON 직렬화 비용 / token 절약).
    // entity 1개 = 1줄, 들여쓰기 = 트리 깊이. id 는 full GUID 표기 (LLM 이 mutation 인자로 그대로 사용 가능).

    let private isSystemActiveOpt (store: DsStore) (systemId: Guid) : bool option =
        Queries.allProjects store
        |> List.tryPick (fun p ->
            if p.ActiveSystemIds.Contains(systemId) then Some true
            elif p.PassiveSystemIds.Contains(systemId) then Some false
            else None)

    let private indent (n: int) = String.replicate n "  "

    /// arrowType enum 의 짧은 표기 (ArrowType.Start → "Start").
    let private arrowTypeName (a: ArrowType) = a.ToString()

    /// describe_system. deep=false 면 직계 child 의 이름 + id 만, deep=true 면 Work/Call 트리 + Arrows.
    let describeSystem (store: DsStore) (systemId: Guid) (deep: bool) : string =
        match Queries.getSystem systemId store with
        | None -> $"NOT_FOUND: System(id={systemId}) 가 존재하지 않습니다."
        | Some sys ->
            let sb = System.Text.StringBuilder()
            let activeMark =
                match isSystemActiveOpt store systemId with
                | Some true -> "active"
                | Some false -> "passive"
                | None -> "orphan"
            sb.AppendLine($"DsSystem \"{sys.Name}\" (id={systemId:D}, {activeMark})") |> ignore

            let flows = Queries.flowsOf systemId store
            let apiDefs = Queries.apiDefsOf systemId store

            for flow in flows do
                let works = Queries.worksOf flow.Id store
                sb.AppendLine($"{indent 1}Flow \"{flow.Name}\" (id={flow.Id:D}, works={works.Length})") |> ignore
                if deep then
                    for work in works do
                        let calls = Queries.callsOf work.Id store
                        sb.AppendLine($"{indent 2}Work \"{work.Name}\" (id={work.Id:D}, calls={calls.Length})") |> ignore
                        for call in calls do
                            sb.AppendLine($"{indent 3}Call \"{call.Name}\" (id={call.Id:D})") |> ignore
                        for a in Queries.arrowCallsOf work.Id store do
                            let srcName = Queries.getCall a.SourceId store |> Option.map (fun c -> c.Name) |> Option.defaultValue "?"
                            let tgtName = Queries.getCall a.TargetId store |> Option.map (fun c -> c.Name) |> Option.defaultValue "?"
                            sb.AppendLine($"{indent 3}ArrowCall {srcName}→{tgtName} ({arrowTypeName a.ArrowType}, id={a.Id:D})") |> ignore

            for apiDef in apiDefs do
                sb.AppendLine($"{indent 1}ApiDef \"{apiDef.Name}\" (id={apiDef.Id:D})") |> ignore

            if deep then
                let arrows = Queries.arrowWorksOf systemId store
                for a in arrows do
                    let srcName = Queries.getWork a.SourceId store |> Option.map (fun w -> w.Name) |> Option.defaultValue "?"
                    let tgtName = Queries.getWork a.TargetId store |> Option.map (fun w -> w.Name) |> Option.defaultValue "?"
                    sb.AppendLine($"{indent 1}ArrowWork {srcName}→{tgtName} ({arrowTypeName a.ArrowType}, id={a.Id:D})") |> ignore

            sb.ToString().TrimEnd()

    /// rootId 의 EntityKind 자동 판별 (Project/System/Flow/Work). depth = 트리 추가 깊이.
    /// max 50 entity 노출, 초과 시 "... (truncated, N more)" 표기.
    let describeSubtree (store: DsStore) (rootId: Guid) (depth: int) : string =
        let depth = max 0 (min 5 depth)  // cap [0,5]
        let sb = System.Text.StringBuilder()
        let mutable budget = 50

        let writeLine (level: int) (text: string) =
            if budget > 0 then
                sb.AppendLine($"{indent level}{text}") |> ignore
                budget <- budget - 1
            elif budget = 0 then
                sb.AppendLine($"{indent level}... (truncated)") |> ignore
                budget <- -1
            // budget < 0 이면 추가 출력 안 함

        let rec walkSystem (level: int) (sys: DsSystem) (remaining: int) =
            let activeMark =
                match isSystemActiveOpt store sys.Id with
                | Some true -> "active" | Some false -> "passive" | None -> "orphan"
            writeLine level $"DsSystem \"{sys.Name}\" (id={sys.Id:D}, {activeMark})"
            if remaining > 0 then
                for flow in Queries.flowsOf sys.Id store do
                    walkFlow (level + 1) flow (remaining - 1)
                for apiDef in Queries.apiDefsOf sys.Id store do
                    writeLine (level + 1) $"ApiDef \"{apiDef.Name}\" (id={apiDef.Id:D})"

        and walkFlow (level: int) (flow: Flow) (remaining: int) =
            let works = Queries.worksOf flow.Id store
            writeLine level $"Flow \"{flow.Name}\" (id={flow.Id:D}, works={works.Length})"
            if remaining > 0 then
                for work in works do
                    walkWork (level + 1) work (remaining - 1)

        and walkWork (level: int) (work: Work) (remaining: int) =
            let calls = Queries.callsOf work.Id store
            writeLine level $"Work \"{work.Name}\" (id={work.Id:D}, calls={calls.Length})"
            if remaining > 0 then
                for call in calls do
                    writeLine (level + 1) $"Call \"{call.Name}\" (id={call.Id:D})"
                for a in Queries.arrowCallsOf work.Id store do
                    let srcName = Queries.getCall a.SourceId store |> Option.map (fun c -> c.Name) |> Option.defaultValue "?"
                    let tgtName = Queries.getCall a.TargetId store |> Option.map (fun c -> c.Name) |> Option.defaultValue "?"
                    writeLine (level + 1) $"ArrowCall {srcName}→{tgtName} ({arrowTypeName a.ArrowType}, id={a.Id:D})"

        match Queries.getProject rootId store with
        | Some p ->
            writeLine 0 $"Project \"{p.Name}\" (id={p.Id:D})"
            if depth > 0 then
                for sysId in Seq.append p.ActiveSystemIds p.PassiveSystemIds do
                    match Queries.getSystem sysId store with
                    | Some s -> walkSystem 1 s (depth - 1)
                    | None -> ()
        | None ->
            match Queries.getSystem rootId store with
            | Some s -> walkSystem 0 s depth
            | None ->
                match Queries.getFlow rootId store with
                | Some f -> walkFlow 0 f depth
                | None ->
                    match Queries.getWork rootId store with
                    | Some w -> walkWork 0 w depth
                    | None ->
                        sb.AppendLine($"NOT_FOUND: rootId={rootId:D} 가 Project/System/Flow/Work 어디에도 없습니다.") |> ignore

        // budget 음수 시 writeLine 가 이미 "... (truncated)" 한 줄을 붙였으므로 추가 처리 없음.
        sb.ToString().TrimEnd()

    /// find_by_name. case-insensitive substring 검색. kind None 이면 모든 종류.
    /// 반환: (kind, id, displayName) 목록, 최대 51 (호출자가 50 초과를 truncated 로 판별).
    let findByName (store: DsStore) (needle: string) (kind: EntityKind option) : (EntityKind * Guid * string) list =
        let needle = if isNull needle then "" else needle.Trim().ToLowerInvariant()
        if needle.Length = 0 then []
        else
            let matches (name: string) = name.ToLowerInvariant().Contains(needle)
            let results = ResizeArray<EntityKind * Guid * string>()

            // cap=51 → 호출자가 results.Length > 50 일 때만 truncated 표기 (정확히 50 매치는 false positive 회피)
            let scan (k: EntityKind) (xs: seq<#DsEntity>) =
                if results.Count < 51 then
                    for e in xs do
                        if results.Count < 51 && matches e.Name then
                            results.Add(k, e.Id, e.Name)

            let inline want target = kind |> Option.forall ((=) target)

            if want EntityKind.Project then scan EntityKind.Project (Queries.allProjects store |> Seq.cast<DsEntity>)
            if want EntityKind.System  then scan EntityKind.System  (store.SystemsReadOnly.Values |> Seq.cast<DsEntity>)
            if want EntityKind.Flow    then scan EntityKind.Flow    (store.FlowsReadOnly.Values   |> Seq.cast<DsEntity>)
            if want EntityKind.Work    then scan EntityKind.Work    (store.WorksReadOnly.Values   |> Seq.cast<DsEntity>)
            if want EntityKind.Call    then scan EntityKind.Call    (store.CallsReadOnly.Values   |> Seq.cast<DsEntity>)
            if want EntityKind.ApiDef  then scan EntityKind.ApiDef  (store.ApiDefsReadOnly.Values |> Seq.cast<DsEntity>)

            results |> List.ofSeq

    // ─── validate_model (Phase 1d-3) ─────────────────────────────────────────
    //
    // Mutation tool 의 fail-fast (parent existence + 이름 unique) 와 짝이 되는 retro 자가 검증.
    // turn 종료 직전 LLM 이 "지금까지 누적된 plan + store 합쳐서 일관성 OK?" 확인용. 본 함수는
    // store 만 본다 (plan 검증은 mutation tool 단계에서 끝). golden 시나리오 (1d-6) test step 의
    // assertion 도 본 함수를 재사용 가능.
    //
    // **검사 카테고리** — LLM 이 회복 가능한 단서 위주, schema 무결성 (예: parent kind 가 잘못된
    // arrow) 같은 invariant 는 ImportPlanApply 가 이미 거부하므로 제외:
    //   - DanglingArrow:    Arrow 의 source/target 이 store 에 없는 경우
    //   - EmptyFlow:        Work 0 개인 Flow (수정 의도 stub)
    //   - EmptyWork:        Call 0 개인 Work
    //   - DuplicateName:    같은 parent 안 동명 (mutation 단계가 차단하나 외부 import / 직접 mutation 대비)
    //   - TodoPlaceholder:  TODO / TBD / FIXME / "?" 등 미완성 표식
    //   - Orphan:           project 에 부착되지 않은 System (global scope 만)

    /// validate_model scope.
    type ValidationScope =
        | GlobalScope
        | SystemScope of Guid
        | FlowScope of Guid

    let private placeholderTokens =
        Set.ofList [ "TODO"; "TBD"; "FIXME"; "XXX"; "?"; "??"; "???" ]

    let private isPlaceholderName (name: string) : bool =
        if String.IsNullOrWhiteSpace(name) then true
        else Set.contains (name.Trim().ToUpperInvariant()) placeholderTokens

    /// rootId 의 EntityKind 자동 판별. Project = global 동등 (현재 N=1 가정).
    /// 어디에도 매칭되지 않으면 None — 호출자가 VALIDATION_ERROR 메시지로 변환.
    let private resolveValidationScope (store: DsStore) (rootIdOpt: Guid option) : ValidationScope option =
        match rootIdOpt with
        | None -> Some GlobalScope
        | Some id ->
            match Queries.getProject id store with
            | Some _ -> Some GlobalScope
            | None ->
                match Queries.getSystem id store with
                | Some _ -> Some (SystemScope id)
                | None ->
                    match Queries.getFlow id store with
                    | Some _ -> Some (FlowScope id)
                    | None -> None

    /// 카테고리 출력 순서 — 동일 plain text 비교 (golden test) 안정성 확보.
    let private categoryOrder =
        [ "Orphan"; "DanglingArrow"; "EmptyFlow"; "EmptyWork"; "DuplicateName"; "TodoPlaceholder" ]

    let private formatScopeLabel (scope: ValidationScope) =
        match scope with
        | GlobalScope -> "global"
        | SystemScope id -> $"System(id={id:D})"
        | FlowScope id -> $"Flow(id={id:D})"

    /// validate_model 본체. plain text 반환 (issue 없으면 "(no issues; scope=...)").
    let validateModel (store: DsStore) (scope: ValidationScope) : string =
        let issues = ResizeArray<string * string>()  // (category, line)
        let report category line = issues.Add(category, line)

        // scope → 검사 대상 system 목록 + flow filter (FlowScope 면 단일 flow 만)
        let targetSystems, flowFilterOpt =
            match scope with
            | GlobalScope ->
                store.SystemsReadOnly.Values |> Seq.toList, None
            | SystemScope sysId ->
                match Queries.getSystem sysId store with
                | Some s -> [s], None
                | None -> [], None
            | FlowScope flowId ->
                match Queries.getFlow flowId store with
                | Some f ->
                    match Queries.getSystem f.ParentId store with
                    | Some s -> [s], Some flowId
                    | None -> [], Some flowId
                | None -> [], Some flowId

        let isFlowInScope (flowId: Guid) =
            match flowFilterOpt with
            | Some fid -> flowId = fid
            | None -> true

        // 1. Orphan System (global scope 에서만 의미)
        if scope = GlobalScope then
            let attached =
                Queries.allProjects store
                |> List.collect (fun p -> Seq.append p.ActiveSystemIds p.PassiveSystemIds |> Seq.toList)
                |> Set.ofList
            for sys in store.SystemsReadOnly.Values do
                if not (Set.contains sys.Id attached) then
                    report "Orphan" $"System \"{sys.Name}\" (id={sys.Id:D}) is not attached to any Project"

        let idsCsv (ids: Guid seq) =
            ids |> Seq.map (fun g -> g.ToString("D")) |> String.concat ", "

        for sys in targetSystems do
            // System placeholder
            if flowFilterOpt.IsNone && isPlaceholderName sys.Name then
                report "TodoPlaceholder" $"System \"{sys.Name}\" (id={sys.Id:D})"

            let flows = Queries.flowsOf sys.Id store

            // Flow 이름 중복 (System 단위) — flow scope 면 skip
            if flowFilterOpt.IsNone then
                flows
                |> List.groupBy (fun f -> f.Name)
                |> List.filter (fun (_, xs) -> xs.Length > 1)
                |> List.iter (fun (name, xs) ->
                    let ids = xs |> List.map (fun f -> f.Id) |> idsCsv
                    report "DuplicateName" $"Flow \"{name}\" duplicated {xs.Length}× in System \"{sys.Name}\" (ids=[{ids}])")

            // ApiDef (System 단위) — flow scope 면 skip
            if flowFilterOpt.IsNone then
                let apiDefs = Queries.apiDefsOf sys.Id store
                apiDefs
                |> List.groupBy (fun a -> a.Name)
                |> List.filter (fun (_, xs) -> xs.Length > 1)
                |> List.iter (fun (name, xs) ->
                    let ids = xs |> List.map (fun a -> a.Id) |> idsCsv
                    report "DuplicateName" $"ApiDef \"{name}\" duplicated {xs.Length}× in System \"{sys.Name}\" (ids=[{ids}])")
                for a in apiDefs do
                    if isPlaceholderName a.Name then
                        report "TodoPlaceholder" $"ApiDef \"{a.Name}\" (id={a.Id:D}) in System \"{sys.Name}\""

            // ArrowBetweenWorks (System 단위) — flow scope 면 skip
            if flowFilterOpt.IsNone then
                for arrow in Queries.arrowWorksOf sys.Id store do
                    if Queries.getWork arrow.SourceId store |> Option.isNone then
                        report "DanglingArrow"
                            $"ArrowWork (id={arrow.Id:D}) in System \"{sys.Name}\" has missing source Work (id={arrow.SourceId:D})"
                    if Queries.getWork arrow.TargetId store |> Option.isNone then
                        report "DanglingArrow"
                            $"ArrowWork (id={arrow.Id:D}) in System \"{sys.Name}\" has missing target Work (id={arrow.TargetId:D})"

            for flow in flows do
                if isFlowInScope flow.Id then
                    if isPlaceholderName flow.Name then
                        report "TodoPlaceholder" $"Flow \"{flow.Name}\" (id={flow.Id:D})"

                    let works = Queries.worksOf flow.Id store
                    if works.IsEmpty then
                        report "EmptyFlow" $"Flow \"{flow.Name}\" (id={flow.Id:D}) has no Works"

                    works
                    |> List.groupBy (fun w -> w.LocalName)
                    |> List.filter (fun (_, xs) -> xs.Length > 1)
                    |> List.iter (fun (lname, xs) ->
                        let ids = xs |> List.map (fun w -> w.Id) |> idsCsv
                        report "DuplicateName" $"Work \"{lname}\" duplicated {xs.Length}× in Flow \"{flow.Name}\" (ids=[{ids}])")

                    for work in works do
                        if isPlaceholderName work.LocalName then
                            report "TodoPlaceholder" $"Work \"{work.Name}\" (id={work.Id:D})"

                        let calls = Queries.callsOf work.Id store
                        if calls.IsEmpty then
                            report "EmptyWork" $"Work \"{work.Name}\" (id={work.Id:D}) has no Calls"

                        calls
                        |> List.groupBy (fun c -> c.Name)
                        |> List.filter (fun (_, xs) -> xs.Length > 1)
                        |> List.iter (fun (cname, xs) ->
                            let ids = xs |> List.map (fun c -> c.Id) |> idsCsv
                            report "DuplicateName" $"Call \"{cname}\" duplicated {xs.Length}× in Work \"{work.Name}\" (ids=[{ids}])")

                        for c in calls do
                            if isPlaceholderName c.Name then
                                report "TodoPlaceholder" $"Call \"{c.Name}\" (id={c.Id:D}) in Work \"{work.Name}\""

                        for arrow in Queries.arrowCallsOf work.Id store do
                            if Queries.getCall arrow.SourceId store |> Option.isNone then
                                report "DanglingArrow"
                                    $"ArrowCall (id={arrow.Id:D}) in Work \"{work.Name}\" has missing source Call (id={arrow.SourceId:D})"
                            if Queries.getCall arrow.TargetId store |> Option.isNone then
                                report "DanglingArrow"
                                    $"ArrowCall (id={arrow.Id:D}) in Work \"{work.Name}\" has missing target Call (id={arrow.TargetId:D})"

        let scopeLabel = formatScopeLabel scope
        // Flow scope 에서 의도적으로 skip 한 카테고리 — LLM 이 "왜 안 나오지" 헷갈리지 않게 footer 명시.
        let skippedFooter =
            match scope with
            | FlowScope _ -> Some "(scope=flow: Orphan / sibling-flow DuplicateName / ApiDef / ArrowBetweenWorks checks skipped)"
            | SystemScope _ -> Some "(scope=system: Orphan check skipped)"
            | GlobalScope -> None

        if issues.Count = 0 then
            match skippedFooter with
            | Some f -> $"(no issues; scope={scopeLabel})\n{f}"
            | None -> $"(no issues; scope={scopeLabel})"
        else
            let sb = System.Text.StringBuilder()
            sb.AppendLine($"validate_model scope={scopeLabel}, {issues.Count} issue(s)") |> ignore
            let grouped = issues |> Seq.groupBy fst |> Map.ofSeq
            for cat in categoryOrder do
                match Map.tryFind cat grouped with
                | Some items ->
                    sb.AppendLine($"{cat}:") |> ignore
                    // 카테고리 안 line 정렬 — store dictionary iteration 순서 의존 회피 (golden test 안정성).
                    for (_, line) in items |> Seq.sortBy snd do
                        sb.AppendLine($"  - {line}") |> ignore
                | None -> ()
            match skippedFooter with
            | Some f -> sb.AppendLine(f) |> ignore
            | None -> ()
            sb.ToString().TrimEnd()

    /// C# entry: rootId None = global, Some id = Project/System/Flow 자동 판별.
    /// 매칭 실패 시 "VALIDATION_ERROR: ..." 반환 (RunRead 의 INTERNAL_ERROR 분기 회피).
    let validateModelByGuid (store: DsStore) (rootIdOpt: Guid option) : string =
        match resolveValidationScope store rootIdOpt with
        | Some scope -> validateModel store scope
        | None ->
            let id = rootIdOpt.Value
            $"VALIDATION_ERROR: scope id {id:D} 가 Project/System/Flow 어디에도 해당하지 않습니다."
