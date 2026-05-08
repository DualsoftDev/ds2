namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.Text.Json
open Ds2.Core
open Ds2.Core.Store

/// Pass 6 (b) — Batch tool (`apply_operations`) 의 op 1개 입력.
/// Args 는 op 별 필요 field 가 다른 dynamic JSON object → JsonElement 로 lazy 처리.
/// C# 측이 JsonDocument.Parse 후 array iter → BatchOpInput[] 생성하여 queueBatch 에 전달.
type BatchOpInput = {
    Op: string
    Ref: string option
    Args: JsonElement
}

/// Pass 6 (b) — Batch op 1개 결과.
/// Id = 새 Guid 반환 op (add_*) 만 Some. remove_entity / rename_entity 는 None.
type BatchOpResult = {
    Index: int
    Op: string
    Ref: string option
    Id: Guid option
    Display: string
}

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

    /// helper cascade quota 사전 reject 기준 — `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs:24`
    /// `MutationQuota = 50` 과 sync. 변경 시 양쪽 동시 수정 (drift 시 helper 가 quota 초과 op 를
    /// dispatch 시점에 reject 못하고 batch 도중 RuntimeException 회귀 위험).
    [<Literal>]
    let MutationQuotaSync = 50

    /// Tool 인자 sanitize. 빈 string "" 반환 = valid, 메시지 반환 = invalid.
    let sanitizeName (value: string) (field: string) (maxLength: int) : string =
        if String.IsNullOrWhiteSpace(value) then
            $"VALIDATION_ERROR: {field} 이(가) 비어있습니다."
        else
            let trimmed = value.Trim()
            // Pass 3 (c): name="@xxx" / "$xxx" injection self-loop 방지 (C5 / M10).
            // entity 이름이 변수 참조 prefix 와 모양이 같으면 read tool 출력에서 LLM 자가혼동.
            if trimmed.StartsWith("@") || trimmed.StartsWith("$") then
                $"VALIDATION_ERROR: {field} 가 '@' 또는 '$' 로 시작할 수 없습니다 (예약 prefix)."
            elif trimmed.Length > maxLength then
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

    let private tryFindApiDefInPlan (plan: ImportPlanBuilder) (id: Guid) : ApiDef option =
        tryFindInPlan plan (function AddApiDef d when d.Id = id -> Some d | _ -> None)

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

    /// add_project mutation tool. Pass 5 에서 추가 — fresh store 에서 LLM 이 자율적으로 모델
    /// 빌드 가능 (이전 phase 1 = "사용자가 GUI 로 새 project 생성" 의존을 제거).
    /// 같은 turn 의 add_active_system / add_passive_system 은 첫 project 자동 부착이라 add_project 직후 사용 가능.
    /// 이름 중복 (store + plan 합산) 시 invalidOp.
    /// 반환: 새 project Id.
    let queueAddProject (plan: ImportPlanBuilder) (store: DsStore) (name: string) : Guid =
        requireNonEmpty (nameof name) name "Project name"
        let nameClash =
            hasNameClash plan
                (fun () -> Queries.allProjects store |> List.exists (fun p -> p.Name = name))
                (function AddProject p -> p.Name = name | _ -> false)
        if nameClash then
            invalidOp $"이미 '{name}' 프로젝트가 존재합니다."
        let project = Project(name)
        plan.Add(AddProject project)
        project.Id

    /// 첫 project 자동 부착 — store 우선, 없으면 같은 turn 의 plan 의 AddProject.
    /// Pass 5: add_project 도구 추가 후 같은 turn 안 add_project → add_active_system/add_passive_system chain 패턴 지원.
    let private resolveFirstProjectId (plan: ImportPlanBuilder) (store: DsStore) : Guid =
        match Queries.allProjects store with
        | p :: _ -> p.Id
        | [] ->
            let planProj =
                plan.Operations
                |> Seq.tryPick (function AddProject p -> Some p.Id | _ -> None)
            match planProj with
            | Some id -> id
            | None -> invalidOp "프로젝트가 없습니다. 먼저 add_project 를 호출하거나 GUI 로 생성하세요."

    /// add_active_system mutation tool. 첫 project 에 active 로 부착.
    /// 반환: 새로 생성된 system Id.
    let queueAddActiveSystem (plan: ImportPlanBuilder) (store: DsStore) (name: string) : Guid =
        requireNonEmpty (nameof name) name "System name"
        let projectId = resolveFirstProjectId plan store
        let sys = DsSystem(name)
        plan.Add(AddSystem sys)
        plan.Add(LinkSystemToProject(projectId, sys.Id, true))
        sys.Id

    /// add_passive_system mutation tool. 첫 project 에 passive 로 부착 + SystemType 설정.
    /// deviceType: cylinder/clamp/lifter → "Unit", robot → "Robot", conveyor → "Conveyor" 등 (KnownNames).
    /// 반환: 새로 생성된 system Id.
    let queueAddPassiveSystem (plan: ImportPlanBuilder) (store: DsStore) (name: string) (deviceType: string) : Guid =
        requireNonEmpty (nameof name) name "System name"
        requireNonEmpty (nameof deviceType) deviceType "System deviceType"
        let projectId = resolveFirstProjectId plan store
        let sys = DsSystem(name)
        sys.SystemType <- Some deviceType
        plan.Add(AddSystem sys)
        plan.Add(LinkSystemToProject(projectId, sys.Id, false))
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

    /// System 의 isActive 검사 (store 측). store 에 없으면 None.
    let private isSystemActiveOpt (store: DsStore) (systemId: Guid) : bool option =
        Queries.allProjects store
        |> List.tryPick (fun p ->
            if p.ActiveSystemIds.Contains(systemId) then Some true
            elif p.PassiveSystemIds.Contains(systemId) then Some false
            else None)

    /// add_call mutation tool. parent Work + ApiDef 는 store 또는 plan 에 존재해야 함.
    /// 인자 = (workId, apiDefId) 2개. devicesAlias / apiName 은 ApiDef → ParentSystem.Name / ApiDef.Name 자동 도출.
    /// 룰 B (alias = Passive.Name) = 구문상 차단 (alias 인자 자체 부재).
    /// 룰 C (ApiDef 는 Passive 의 자식) = 런타임 차단 (ApiDef.ParentSystem.IsActive == true 시 BATCH_ERROR).
    /// ApiCall cascade: ApiCall.ApiDefId / OriginFlowId (= workId 의 parent Flow.Id) 자동 set + AddApiCall plan op.
    /// Call.Name 충돌 검사 유지 (같은 Work 안 동일 fullName 중복 차단).
    let queueAddCall (plan: ImportPlanBuilder) (store: DsStore) (workId: Guid) (apiDefId: Guid) : Guid =
        let work = requireWork plan store workId
        // ApiDef lookup (store + plan)
        let apiDef =
            match Queries.getApiDef apiDefId store |> Option.orElseWith (fun () -> tryFindApiDefInPlan plan apiDefId) with
            | Some d -> d
            | None -> invalidOp $"ApiDef(id={apiDefId}) 가 store/plan 어디에도 없습니다."
        // ParentSystem (= ApiDef.ParentId) 의 IsActive 검사 — 룰 C 런타임 차단.
        let parentSysId = apiDef.ParentId
        // store 측 isActive
        let storeIsActive = isSystemActiveOpt store parentSysId
        // plan 측 LinkSystemToProject (같은 turn 안 add_passive_system → add_call chain 지원)
        let planIsActive =
            plan.Operations
            |> Seq.tryPick (function
                | LinkSystemToProject(_, sysId, isActive) when sysId = parentSysId -> Some isActive
                | _ -> None)
        match storeIsActive, planIsActive with
        | Some true, _
        | None, Some true ->
            invalidOp $"ApiDef(id={apiDefId}) 의 parent System(id={parentSysId}) 가 Active 입니다. ApiDef 는 Passive System 의 자식이어야 합니다 (룰 C)."
        | Some false, _
        | None, Some false -> ()
        | None, None ->
            // store/plan 어디에서도 LinkSystemToProject 가 발견되지 않음 (orphan 또는 corrupt). fail-safe.
            invalidOp $"System(id={parentSysId}) 의 IsActive 를 결정할 수 없습니다 (orphan 또는 LinkSystemToProject 누락)."
        // devicesAlias / apiName 자동 도출
        let parentSys =
            match Queries.getSystem parentSysId store |> Option.orElseWith (fun () -> tryFindSystemInPlan plan parentSysId) with
            | Some s -> s
            | None -> invalidOp $"ApiDef.ParentSystem(id={parentSysId}) 가 store/plan 어디에도 없습니다."
        let devicesAlias = parentSys.Name
        let apiName = apiDef.Name
        let fullName = $"{devicesAlias}.{apiName}"
        if hasCallNameClash plan store workId fullName then
            invalidOp $"같은 Work 내에 이미 '{fullName}' Call 이 존재합니다."
        let call = Call(devicesAlias, apiName, workId)
        plan.Add(AddCall call)
        // ApiCall cascade — ApiDefId + OriginFlowId 자동 set (createAndRegisterApiCall 패턴 + OriginFlowId 보강).
        let apiCall = ApiCall(fullName)
        apiCall.ApiDefId <- Some apiDefId
        apiCall.OriginFlowId <- Some work.ParentId  // workId 의 parent Flow.Id
        call.ApiCalls.Add(apiCall)
        plan.Add(AddApiCall apiCall)
        call.Id

    /// add_api_def mutation tool. parent System 존재 + 이름 중복 검사.
    /// txWorkId/rxWorkId 가 주어지면 ApiDef.TxGuid/RxGuid 에 binding 후 ActionType=Normal.
    /// helper (add_cylinder 등) 가 자동 채우는 binding 을 primitive 만으로도 표현 가능하게 함.
    let queueAddApiDef
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (systemId: Guid)
        (txWorkId: Guid option) (rxWorkId: Guid option) : Guid =
        requireNonEmpty (nameof name) name "ApiDef name"
        requireSystem plan store systemId |> ignore
        if hasApiDefNameClash plan store systemId name then
            invalidOp $"같은 System 내에 이미 '{name}' ApiDef 가 존재합니다."
        // Tx/Rx 인자 검증 — Work 가 store/plan 에 존재하는지.
        let validateWork (idOpt: Guid option) (label: string) =
            match idOpt with
            | None -> ()
            | Some id ->
                match Queries.getWork id store |> Option.orElseWith (fun () -> tryFindWorkInPlan plan id) with
                | Some _ -> ()
                | None -> invalidOp $"{label}(id={id}) 가 Work 로 존재하지 않습니다."
        validateWork txWorkId "txWorkId"
        validateWork rxWorkId "rxWorkId"
        let apiDef = ApiDef(name, systemId)
        apiDef.TxGuid <- txWorkId
        apiDef.RxGuid <- rxWorkId
        plan.Add(AddApiDef apiDef)
        apiDef.Id

    // ─── Tier 1 helper — device-class cascade (Phase extend-mcp L2.d) ────────
    //
    // PassiveSystem + Flow + Work×N + ApiDef×N (+ optional ResetReset Arrow) cascade 를 1 op 로.
    // ImportPlanDeviceOps.buildPassiveDeviceCascade 가 raw ResizeArray 에 append 하므로
    // wrapper 가 별도 buffer 만들어 전달 후 plan.Add 로 옮긴다.
    // 반환 = (PassiveSystemId, (apiName * ApiDefId) list) — caller (dispatchBatchOp) 가 batch refTable 에 다중 등록.

    let private runDeviceCascade
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (deviceType: string)
        (apiNames: string list)
        (workDuration: TimeSpan option)
        (wiringMode: ImportPlanDeviceOps.WiringMode) : Guid * (string * Guid) list =
        requireNonEmpty (nameof name) name "Device name"
        requireNonEmpty (nameof deviceType) deviceType "Device deviceType"
        if apiNames.IsEmpty then
            invalidOp "apiNames 가 비어있습니다."
        // 동일 이름 sanitize 1차 — apiNames 도 prompt injection / 빈 문자열 / 중복 방어.
        // 중복 apiName 은 buildPassiveDeviceCascade 의 ensureApiDef 가 같은 System.Id 안 동명 ApiDef 로 dedupe 하므로
        // 결과 ApiDef 수가 입력 apiNames 수와 어긋남 → list_zip 실패 / refTable 매칭 깨짐. 호출자 측에서 1차 거름.
        let dupSet = HashSet<string>()
        for apiName in apiNames do
            if String.IsNullOrWhiteSpace apiName then
                invalidOp "apiNames 에 빈 항목이 포함되어 있습니다."
            if not (dupSet.Add apiName) then
                invalidOp $"apiNames 에 중복 항목 '{apiName}' 이 포함되어 있습니다."
        let projectId = resolveFirstProjectId plan store
        let buffer = ResizeArray<ImportPlanOperation>()
        let result =
            ImportPlanDeviceOps.buildPassiveDeviceCascade
                store projectId buffer name deviceType apiNames workDuration wiringMode
        for op in buffer do
            plan.Add(op)
        result

    /// add_cylinder helper. N=2 (ADV/RET 등 default), Chain wiring (ResetReset 1 개), 500ms.
    let queueAddCylinder
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (apiNames: string list) (workDuration: TimeSpan option) : Guid * (string * Guid) list =
        let names = if apiNames.IsEmpty then ["ADV"; "RET"] else apiNames
        if names.Length <> 2 then
            invalidOp $"add_cylinder: apiNames 는 정확히 2개여야 합니다 (현재 {names.Length})."
        let duration = workDuration |> Option.orElseWith (fun () -> Some (TimeSpan.FromMilliseconds 500.))
        runDeviceCascade plan store name "Unit" names duration ImportPlanDeviceOps.Chain

    /// add_clamp helper. N=2 (CLP/UNCLP default), Chain wiring (ResetReset 1 개), 500ms.
    let queueAddClamp
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (apiNames: string list) (workDuration: TimeSpan option) : Guid * (string * Guid) list =
        let names = if apiNames.IsEmpty then ["CLP"; "UNCLP"] else apiNames
        if names.Length <> 2 then
            invalidOp $"add_clamp: apiNames 는 정확히 2개여야 합니다 (현재 {names.Length})."
        let duration = workDuration |> Option.orElseWith (fun () -> Some (TimeSpan.FromMilliseconds 500.))
        runDeviceCascade plan store name "Unit" names duration ImportPlanDeviceOps.Chain

    /// add_robot helper. opposing default = "none" (rev 11 — 도메인 룰 ROBOT opposing 없음).
    /// "chain" / "all-pairs" 는 사용자 명시 시.
    let queueAddRobot
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (apiNames: string list) (opposing: string) (workDuration: TimeSpan option)
        : Guid * (string * Guid) list =
        if apiNames.IsEmpty then invalidOp "add_robot: apiNames 가 비어있습니다."
        let mode =
            match opposing with
            | "none" | "" -> ImportPlanDeviceOps.NoneMode
            | "chain" -> ImportPlanDeviceOps.Chain
            | "all-pairs" -> ImportPlanDeviceOps.AllPairs
            | other -> invalidOp $"opposing '{other}' 이 유효하지 않습니다. 허용: none|chain|all-pairs."
        // D8 quota 사전 reject (all-pairs N≥9 = 57 op > 50)
        let n = apiNames.Length
        let cascadeOpCount =
            match mode with
            | ImportPlanDeviceOps.NoneMode -> 3 + 2*n
            | ImportPlanDeviceOps.Chain -> 3 + 2*n + (n-1)
            | ImportPlanDeviceOps.AllPairs -> 3 + 2*n + (n*(n-1)/2)
        if cascadeOpCount > MutationQuotaSync then
            invalidOp $"op 수 초과: {cascadeOpCount} > {MutationQuotaSync}, opposing='chain' 또는 'none' 으로 변경 권장 (apiNames 분할은 device 의미 분리)."
        runDeviceCascade plan store name "Robot" apiNames workDuration mode

    /// add_device generic fallback. deviceType 사용자 지정 (KnownNames 권장).
    let queueAddDevice
        (plan: ImportPlanBuilder) (store: DsStore)
        (name: string) (deviceType: string) (apiNames: string list)
        (opposing: string) (workDuration: TimeSpan option)
        : Guid * (string * Guid) list =
        if apiNames.IsEmpty then invalidOp "add_device: apiNames 가 비어있습니다."
        let mode =
            match opposing with
            | "none" | "" -> ImportPlanDeviceOps.NoneMode
            | "chain" -> ImportPlanDeviceOps.Chain
            | "all-pairs" -> ImportPlanDeviceOps.AllPairs
            | other -> invalidOp $"opposing '{other}' 이 유효하지 않습니다. 허용: none|chain|all-pairs."
        let n = apiNames.Length
        let cascadeOpCount =
            match mode with
            | ImportPlanDeviceOps.NoneMode -> 3 + 2*n
            | ImportPlanDeviceOps.Chain -> 3 + 2*n + (n-1)
            | ImportPlanDeviceOps.AllPairs -> 3 + 2*n + (n*(n-1)/2)
        if cascadeOpCount > MutationQuotaSync then
            invalidOp $"op 수 초과: {cascadeOpCount} > {MutationQuotaSync}, opposing='chain' 또는 'none' 으로 변경 권장."
        runDeviceCascade plan store name deviceType apiNames workDuration mode

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
            | AddProject p   when p.Id = id -> Some EntityKind.Project
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
        | _ -> ()  // System 은 add_active_system / add_passive_system 과 일관 (이름 중복 검사 생략)

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

    // ─── Pass 6 (b) — Batch tool (apply_operations) ──────────────────────────
    //
    // 채택 근거: (c) variable binding 의 chain pattern 이 message-level 1 round-trip 은 달성하나
    //   claude CLI 의 internal turn 카운트 (numTurns) 가 N 회 tool_use 별 N 번 cycle 로 부풀림.
    //   (b) 는 단일 tool 1번 호출 = 1 internal turn = 진짜 round-trip 압축. cascade 도 self-contained.
    //
    // ref 해소:
    //   - batch 안 op 의 Args 에 "@<refName>" 패턴이 있으면 직전 op (= array 안 더 앞) 의 결과 Guid 로 치환
    //   - refName scope = 이 batch 호출 안만 (호출 사이 공유 X — Plan 에 누적된 op 는 server 측 ref map 와 무관)
    //
    // fail-fast: 첫 op 실패 시 plan.TruncateTo(snapshotCount) 으로 진입 시점 plan 으로 rollback +
    //   Error(failureIndex, opName, message) 반환. partial 누적이 ApplyImportPlan 시점에 의도와 다른
    //   모델을 만들지 않도록 보장 (= 결정 7 (d) "1 turn = 1 undo" 의미 유지).

    /// Args 에서 string field 추출. 없거나 wrong type 이면 invalidOp.
    let private getStringArg (args: JsonElement) (name: string) : string =
        let mutable prop = JsonElement()
        if args.ValueKind <> JsonValueKind.Object || not (args.TryGetProperty(name, &prop)) then
            invalidOp $"VALIDATION_ERROR: args.{name} 이 존재하지 않습니다."
        elif prop.ValueKind <> JsonValueKind.String then
            invalidOp $"VALIDATION_ERROR: args.{name} 가 string 이 아닙니다 (ValueKind={prop.ValueKind})."
        else
            prop.GetString()

    /// Args 의 optional string field. 없으면 None.
    let private getStringArgOpt (args: JsonElement) (name: string) : string option =
        let mutable prop = JsonElement()
        if args.ValueKind = JsonValueKind.Object && args.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.String then
            Some(prop.GetString())
        else None

    /// Args 의 string array field. 없거나 array 가 아니면 invalidOp.
    let private getStringArrayArg (args: JsonElement) (name: string) : string list =
        let mutable prop = JsonElement()
        if args.ValueKind <> JsonValueKind.Object || not (args.TryGetProperty(name, &prop)) then
            invalidOp $"VALIDATION_ERROR: args.{name} 이 존재하지 않습니다."
        elif prop.ValueKind <> JsonValueKind.Array then
            invalidOp $"VALIDATION_ERROR: args.{name} 가 array 가 아닙니다 (ValueKind={prop.ValueKind})."
        else
            [ for el in prop.EnumerateArray() ->
                if el.ValueKind <> JsonValueKind.String then
                    invalidOp $"VALIDATION_ERROR: args.{name} 의 항목이 string 이 아닙니다."
                el.GetString() ]

    /// Args 의 optional string array field. 없으면 None.
    let private getStringArrayArgOpt (args: JsonElement) (name: string) : string list option =
        let mutable prop = JsonElement()
        if args.ValueKind = JsonValueKind.Object && args.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.Array then
            Some [ for el in prop.EnumerateArray() ->
                    if el.ValueKind <> JsonValueKind.String then
                        invalidOp $"VALIDATION_ERROR: args.{name} 의 항목이 string 이 아닙니다."
                    el.GetString() ]
        else None

    /// Args 의 optional int field (workDurationMs 등). 없으면 None.
    let private getIntArgOpt (args: JsonElement) (name: string) : int option =
        let mutable prop = JsonElement()
        if args.ValueKind = JsonValueKind.Object && args.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.Number then
            let mutable v = 0
            if prop.TryGetInt32(&v) then Some v else None
        else None

    /// Args 의 optional bool field. 없으면 default.
    let private getBoolArg (args: JsonElement) (name: string) (defaultVal: bool) : bool =
        let mutable prop = JsonElement()
        if args.ValueKind = JsonValueKind.Object && args.TryGetProperty(name, &prop) then
            match prop.ValueKind with
            | JsonValueKind.True -> true
            | JsonValueKind.False -> false
            | _ -> defaultVal
        else defaultVal

    /// ref name 형식 검증. 실패 시 invalidOp. 1-32자, [a-zA-Z_][a-zA-Z0-9_]*.
    let private validateRefName (refMap: Dictionary<string, Guid>) (refName: string) =
        if String.IsNullOrEmpty(refName) then
            invalidOp "VALIDATION_ERROR: ref 가 비어있습니다."
        elif refName.Length > 32 then
            invalidOp $"VALIDATION_ERROR: ref 길이 {refName.Length} > 32."
        elif not (System.Text.RegularExpressions.Regex.IsMatch(refName, @"^[a-zA-Z_][a-zA-Z0-9_]*$")) then
            invalidOp $"VALIDATION_ERROR: ref 형식 오류 (1-32자, [a-zA-Z_][a-zA-Z0-9_]*)."
        elif refMap.ContainsKey(refName) then
            invalidOp $"VALIDATION_ERROR: ref '@{refName}' 이 같은 batch 안에 중복 정의되었습니다."

    /// "@refName" → refMap[refName] (Guid). 그 외 → Guid.Parse. 실패 시 invalidOp.
    let private resolveBatchRef (refMap: Dictionary<string, Guid>) (value: string) (field: string) : Guid =
        if String.IsNullOrWhiteSpace(value) then
            invalidOp $"VALIDATION_ERROR: {field} 이(가) 비어있습니다."
        let trimmed = value.Trim()
        if trimmed.StartsWith("@") then
            let refName = trimmed.Substring(1)
            match refMap.TryGetValue(refName) with
            | true, g -> g
            | _ -> invalidOp $"VALIDATION_ERROR: {field} 의 ref '@{refName}' 이 같은 batch 의 이전 op 에서 정의되지 않았습니다."
        else
            match Guid.TryParse(trimmed) with
            | true, g -> g
            | _ -> invalidOp $"VALIDATION_ERROR: {field} 가 유효한 GUID 또는 '@<ref>' 이 아닙니다."

    /// dispatch 시 entity name 인자 sanitize (Cc/Cf reject + @/$ prefix reject + 길이 cap). 실패 시 invalidOp.
    let private sanitizeOrThrow (value: string) (field: string) =
        let err = sanitizeName value field NameMaxLength
        if err <> "" then invalidOp err

    /// queueBatch op 1개 dispatch.
    /// 반환 = ((refName * Guid) list, display).
    /// list = primitive op 의 경우 op.Ref 가 Some 이면 [(refName, id)] 단원소 / None 또는 remove/rename 이면 [].
    ///        helper op (add_cylinder/clamp/robot/device) 의 경우 op.Ref 의 (refName, PassiveSystemId) +
    ///        apiDef*Ref / apiDefRefs 의 (refName, ApiDefId) 다중 등록 (D6 ref-required).
    /// (review M5) 빈 op.Op 검증을 본 함수 첫 줄로 통합 — queueBatch 의 try-with 와 단일 경로로 합쳐 분기 단순화.
    let private dispatchBatchOp
        (plan: ImportPlanBuilder) (store: DsStore)
        (refMap: Dictionary<string, Guid>) (op: BatchOpInput) : (string * Guid) list * string =
        if String.IsNullOrEmpty(op.Op) then
            invalidOp "VALIDATION_ERROR: 'op' 필드가 비어있습니다."
        let mainRef (id: Guid) : (string * Guid) list =
            match op.Ref with Some r -> [r, id] | None -> []
        let parseDuration () =
            getIntArgOpt op.Args "workDurationMs"
            |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))
        match op.Op with
        | "add_project" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let id = queueAddProject plan store (name.Trim())
            mainRef id, $"add_project name=\"{name.Trim()}\" id={id:D}"
        | "add_active_system" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let id = queueAddActiveSystem plan store (name.Trim())
            mainRef id, $"add_active_system name=\"{name.Trim()}\" id={id:D}"
        | "add_passive_system" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let deviceType = getStringArg op.Args "deviceType"
            let id = queueAddPassiveSystem plan store (name.Trim()) (deviceType.Trim())
            mainRef id, $"add_passive_system name=\"{name.Trim()}\" deviceType=\"{deviceType.Trim()}\" id={id:D}"
        | "add_flow" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let sysId = resolveBatchRef refMap (getStringArg op.Args "systemId") "systemId"
            let id = queueAddFlow plan store (name.Trim()) sysId
            mainRef id, $"add_flow name=\"{name.Trim()}\" systemId={sysId:D} id={id:D}"
        | "add_work" ->
            let localName = getStringArg op.Args "localName"
            sanitizeOrThrow localName "localName"
            let flowId = resolveBatchRef refMap (getStringArg op.Args "flowId") "flowId"
            let id = queueAddWork plan store (localName.Trim()) flowId
            mainRef id, $"add_work localName=\"{localName.Trim()}\" flowId={flowId:D} id={id:D}"
        | "add_call" ->
            let workId = resolveBatchRef refMap (getStringArg op.Args "workId") "workId"
            let apiDefId = resolveBatchRef refMap (getStringArg op.Args "apiDefId") "apiDefId"
            let id = queueAddCall plan store workId apiDefId
            mainRef id, $"add_call workId={workId:D} apiDefId={apiDefId:D} id={id:D}"
        | "add_api_def" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let sysId = resolveBatchRef refMap (getStringArg op.Args "systemId") "systemId"
            let txWorkId =
                getStringArgOpt op.Args "txWorkId"
                |> Option.map (fun v -> resolveBatchRef refMap v "txWorkId")
            let rxWorkId =
                getStringArgOpt op.Args "rxWorkId"
                |> Option.map (fun v -> resolveBatchRef refMap v "rxWorkId")
            let id = queueAddApiDef plan store (name.Trim()) sysId txWorkId rxWorkId
            mainRef id, $"add_api_def name=\"{name.Trim()}\" systemId={sysId:D} id={id:D}"
        | "add_arrow" ->
            let srcId = resolveBatchRef refMap (getStringArg op.Args "sourceId") "sourceId"
            let tgtId = resolveBatchRef refMap (getStringArg op.Args "targetId") "targetId"
            let arrowTypeStr = getStringArgOpt op.Args "arrowType" |> Option.defaultValue "Start"
            let arrowType =
                match Enum.TryParse<ArrowType>(arrowTypeStr, true) with
                | true, t -> t
                | _ -> invalidOp $"VALIDATION_ERROR: arrowType '{arrowTypeStr}' 이 유효하지 않습니다. 허용: Unspecified|Start|Reset|StartReset|ResetReset|Group."
            let (id, kind) = queueAddArrow plan store srcId tgtId arrowType
            mainRef id, $"add_arrow kind={kind} type={arrowType} source={srcId:D} target={tgtId:D} id={id:D}"
        | "add_cylinder" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let apiDef1Ref = (getStringArg op.Args "apiDef1Ref").Trim()
            let apiDef2Ref = (getStringArg op.Args "apiDef2Ref").Trim()
            validateRefName refMap apiDef1Ref
            validateRefName refMap apiDef2Ref
            if apiDef1Ref = apiDef2Ref then
                invalidOp $"VALIDATION_ERROR: apiDef1Ref / apiDef2Ref 가 동일합니다 ('{apiDef1Ref}')."
            let apiNames = getStringArrayArgOpt op.Args "apiNames" |> Option.defaultValue []
            let (sysId, apiDefIds) = queueAddCylinder plan store (name.Trim()) apiNames (parseDuration())
            let apiPairs =
                match apiDefIds with
                | [(_, id1); (_, id2)] -> [apiDef1Ref, id1; apiDef2Ref, id2]
                | _ -> invalidOp $"INTERNAL: add_cylinder cascade 가 ApiDef 2개를 반환하지 않았습니다 (got {apiDefIds.Length})."
            mainRef sysId @ apiPairs, $"add_cylinder name=\"{name.Trim()}\" id={sysId:D} apiDefs={apiDefIds.Length}"
        | "add_clamp" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let apiDef1Ref = (getStringArg op.Args "apiDef1Ref").Trim()
            let apiDef2Ref = (getStringArg op.Args "apiDef2Ref").Trim()
            validateRefName refMap apiDef1Ref
            validateRefName refMap apiDef2Ref
            if apiDef1Ref = apiDef2Ref then
                invalidOp $"VALIDATION_ERROR: apiDef1Ref / apiDef2Ref 가 동일합니다 ('{apiDef1Ref}')."
            let apiNames = getStringArrayArgOpt op.Args "apiNames" |> Option.defaultValue []
            let (sysId, apiDefIds) = queueAddClamp plan store (name.Trim()) apiNames (parseDuration())
            let apiPairs =
                match apiDefIds with
                | [(_, id1); (_, id2)] -> [apiDef1Ref, id1; apiDef2Ref, id2]
                | _ -> invalidOp $"INTERNAL: add_clamp cascade 가 ApiDef 2개를 반환하지 않았습니다 (got {apiDefIds.Length})."
            mainRef sysId @ apiPairs, $"add_clamp name=\"{name.Trim()}\" id={sysId:D} apiDefs={apiDefIds.Length}"
        | "add_robot" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let apiNames = getStringArrayArg op.Args "apiNames"
            let apiDefRefs = getStringArrayArg op.Args "apiDefRefs" |> List.map (fun r -> r.Trim())
            if apiNames.Length <> apiDefRefs.Length then
                invalidOp $"VALIDATION_ERROR: add_robot apiNames.length ({apiNames.Length}) != apiDefRefs.length ({apiDefRefs.Length}) (D6 ref-required: 길이 일치)."
            for r in apiDefRefs do validateRefName refMap r
            // 같은 op 안 ref 중복 검사 (refMap 들어가기 전)
            let dupSet = HashSet<string>()
            for r in apiDefRefs do
                if not (dupSet.Add r) then
                    invalidOp $"VALIDATION_ERROR: apiDefRefs 안에 ref '{r}' 이 중복 정의되었습니다."
            let opposing = getStringArgOpt op.Args "opposing" |> Option.defaultValue "none"
            let (sysId, apiDefIds) = queueAddRobot plan store (name.Trim()) apiNames opposing (parseDuration())
            let apiPairs =
                List.zip apiDefRefs (apiDefIds |> List.map snd)
            mainRef sysId @ apiPairs, $"add_robot name=\"{name.Trim()}\" id={sysId:D} apiDefs={apiDefIds.Length} opposing={opposing}"
        | "add_device" ->
            let name = getStringArg op.Args "name"
            sanitizeOrThrow name "name"
            let deviceType = getStringArg op.Args "deviceType"
            let apiNames = getStringArrayArg op.Args "apiNames"
            let apiDefRefs = getStringArrayArg op.Args "apiDefRefs" |> List.map (fun r -> r.Trim())
            if apiNames.Length <> apiDefRefs.Length then
                invalidOp $"VALIDATION_ERROR: add_device apiNames.length ({apiNames.Length}) != apiDefRefs.length ({apiDefRefs.Length}) (D6 ref-required: 길이 일치)."
            for r in apiDefRefs do validateRefName refMap r
            let dupSet = HashSet<string>()
            for r in apiDefRefs do
                if not (dupSet.Add r) then
                    invalidOp $"VALIDATION_ERROR: apiDefRefs 안에 ref '{r}' 이 중복 정의되었습니다."
            let opposing = getStringArgOpt op.Args "opposing" |> Option.defaultValue "none"
            let (sysId, apiDefIds) =
                queueAddDevice plan store (name.Trim()) (deviceType.Trim()) apiNames opposing (parseDuration())
            let apiPairs =
                List.zip apiDefRefs (apiDefIds |> List.map snd)
            mainRef sysId @ apiPairs, $"add_device name=\"{name.Trim()}\" deviceType=\"{deviceType.Trim()}\" id={sysId:D} apiDefs={apiDefIds.Length} opposing={opposing}"
        | "remove_entity" ->
            let entityId = resolveBatchRef refMap (getStringArg op.Args "entityId") "entityId"
            let kind = queueRemoveEntity plan store entityId
            [], $"remove_entity kind={kind} id={entityId:D} (cascade 는 turn end 의 ApplyImportPlan 시점에 적용)"
        | "rename_entity" ->
            let entityId = resolveBatchRef refMap (getStringArg op.Args "entityId") "entityId"
            let newName = getStringArg op.Args "newName"
            sanitizeOrThrow newName "newName"
            let kind = queueRenameEntity plan store entityId (newName.Trim())
            [], $"rename_entity kind={kind} id={entityId:D} newName=\"{newName.Trim()}\""
        | other ->
            invalidOp $"VALIDATION_ERROR: 지원하지 않는 op '{other}'. 허용: add_project|add_active_system|add_passive_system|add_flow|add_work|add_call|add_api_def|add_arrow|add_cylinder|add_clamp|add_robot|add_device|remove_entity|rename_entity."

    /// Batch op array 를 plan 에 누적.
    /// 성공 시 Ok(results) — 모든 op 의 BatchOpResult.
    /// 실패 시 Error(failureIndex, opName, message) — plan 은 진입 시점으로 rollback.
    let queueBatch
        (plan: ImportPlanBuilder) (store: DsStore)
        (ops: BatchOpInput[]) : Result<BatchOpResult[], int * string * string> =
        if isNull ops || ops.Length = 0 then
            Error(0, "", "VALIDATION_ERROR: operations array 가 비어있습니다.")
        else
            let snapshotCount = plan.Count
            let refMap = Dictionary<string, Guid>()
            let results = ResizeArray<BatchOpResult>()
            let mutable failure : (int * string * string) option = None
            let mutable i = 0
            while failure.IsNone && i < ops.Length do
                let op = ops.[i]
                try
                    // (review M5) ref 형식 검사 — `[a-zA-Z_][a-zA-Z0-9_]*` (1-32자). dispatchBatchOp 호출 전에 검증해서
                    // op 성공 후 ref 만 fail 하는 부분 실패 회피.
                    match op.Ref with
                    | Some refName -> validateRefName refMap refName
                    | None -> ()
                    let (refs, display) = dispatchBatchOp plan store refMap op
                    // refs 의 모든 (refName, id) 등록. 중복 시 invalidOp (helper 의 sub-ref 가 main ref 와 충돌하는 경우 포함).
                    for (refName, id) in refs do
                        if refMap.ContainsKey(refName) then
                            invalidOp $"VALIDATION_ERROR: ref '@{refName}' 이 같은 batch 안에 중복 정의되었습니다 (helper sub-ref 와 충돌)."
                        refMap.[refName] <- id
                    // 첫 ref 의 id 를 BatchOpResult.Id 로 노출 (primitive 의 main ref / helper 의 PassiveSystemId).
                    let primaryId =
                        match refs, op.Ref with
                        | [], _ -> None
                        | (_, id) :: _, _ -> Some id
                    results.Add({ Index = i; Op = op.Op; Ref = op.Ref; Id = primaryId; Display = display })
                with ex ->
                    failure <- Some(i, op.Op, ex.Message)
                i <- i + 1
            match failure with
            | Some(idx, opName, msg) ->
                plan.TruncateTo(snapshotCount)
                Error(idx, opName, msg)
            | None ->
                Ok(results.ToArray())
