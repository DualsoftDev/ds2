namespace Ds2.LlmAgent

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ds2.Core
open Ds2.Core.Store

/// Phase 1 YAML protocol — schema v0 parser + dispatcher.
///
/// **Wire = JSON object** (LLM tool_use native). 본 module 은 *얇은 transformer* —
/// `JsonElement` walker 가 schema 키별 dispatch → 기존 `ToolOperations.queueAdd*` 호출.
///
/// SSOT: `Apps/Promaker/Docs/yaml-protocol-v0.md`.
[<RequireQualifiedAccess>]
module ModelProtocol =

    /// log4net logger — 데이터 무결성 fallback (export 측 silent path) forensic 단서.
    /// Phase 2 cycle3 외부 review M1/M2 — None/fallback 분기에서 1회 Warn 출력.
    let private log = log4net.LogManager.GetLogger("Ds2.LlmAgent.ModelProtocol")

    let private VALIDATION_ERROR = "VALIDATION_ERROR"

    /// #7 (todo §10.2 옵션 a) — `Project.Version` 의 entity default SSOT 단일화.
    /// emit 측 default 비교 (`p.Version <> defaultProjectVersion`) 가 hardcode `"1.0.0"` 와
    /// dual SSOT 였던 문제 해소 — Entities.fs 의 `Project.Version` default 변경 시 자동 추적.
    /// `Project` 의 internal constructor 가 `name: string` 받으므로 dummy `""` — Version 만 사용.
    let private defaultProjectVersion = Project("").Version

    /// validate / dispatch 단계의 진단 메시지 누적용.
    type DiagnosticEntry = {
        Path: string
        Message: string
        Suggestion: string option
    }

    /// apply / validate 결과의 진단 메시지 묶음.
    type Diagnostics() =
        let entries = ResizeArray<DiagnosticEntry>()
        member _.Add(path: string, message: string, ?suggestion: string) =
            entries.Add({ Path = path; Message = message; Suggestion = suggestion })
        member _.Entries = entries :> seq<_>
        member _.HasErrors = entries.Count > 0
        member _.Count = entries.Count
        member this.Format() : string =
            if entries.Count = 0 then ""
            else
                let sb = StringBuilder()
                for e in entries do
                    sb.Append(sprintf "%s %s: %s" VALIDATION_ERROR e.Path e.Message) |> ignore
                    match e.Suggestion with
                    | Some s -> sb.AppendLine(sprintf " (제안: %s)" s) |> ignore
                    | None -> sb.AppendLine() |> ignore
                sb.ToString().TrimEnd()

    /// YAML 의 entity 이름을 `.` 구분자 segment list 로 정규화.
    /// SSOT §2.5: `/` → `.` 단일화 + Unicode NFC 정규화.
    let normalizePath (raw: string) : string =
        if String.IsNullOrEmpty raw then raw
        else
            raw.Replace('/', '.').Normalize(System.Text.NormalizationForm.FormC)

    let pathSegments (path: string) : string list =
        normalizePath path
        |> fun p -> p.Split('.', StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    // ─── Device DU literal parser (SSOT §2.3) ────────────────────────────────

    /// `^([A-Za-z][A-Za-z0-9_]*)(?:\(([A-Za-z][A-Za-z0-9_]*)\))?$` — ASCII only.
    let private deviceLiteralRegex =
        System.Text.RegularExpressions.Regex(
            @"^([A-Za-z][A-Za-z0-9_]*)(?:\(([A-Za-z][A-Za-z0-9_]*)\))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    /// device DU literal parse 결과.
    /// - Known case = `cylinder` / `clamp` / `robot` (case-insensitive)
    /// - Custom = `custom(<Type>)`
    /// - Unknown sugar (sugar 미정의) = bare case literal 이 known 3종 외 — validate 에러.
    type DeviceLiteral =
        | KnownCylinder
        | KnownClamp
        | KnownRobot
        | Custom of typeName: string
        | UnknownSugar of raw: string

    let parseDevice (raw: string) : Result<DeviceLiteral, string> =
        if String.IsNullOrWhiteSpace raw then
            Error "device 값이 비어있습니다."
        else
            let m = deviceLiteralRegex.Match(raw.Trim())
            if not m.Success then
                Error (sprintf "'%s' 인식 불가. 형식: <known-case> 또는 custom(<type>) (ASCII only, 영문자 시작)." raw)
            else
                let case = m.Groups.[1].Value
                let typeArg = if m.Groups.[2].Success then Some m.Groups.[2].Value else None
                match case.ToLowerInvariant(), typeArg with
                | "custom", Some t -> Ok (Custom t)
                | "custom", None -> Error "custom 형식은 custom(<TypeName>) 처럼 인자 필요."
                | "cylinder", None -> Ok KnownCylinder
                | "clamp", None -> Ok KnownClamp
                | "robot", None -> Ok KnownRobot
                | other, _ ->
                    // sugar 3종 외 bare literal (pusher 등) — SSOT §3.4.4 정책: validate 에러.
                    Ok (UnknownSugar other)

    // ─── Duration grammar (SSOT §2.3) ───────────────────────────────────────

    /// `^(\d+)(ms|s)$` — wire JSON 도 string 표기. number coercion 없음.
    let private durationRegex =
        System.Text.RegularExpressions.Regex(@"^(\d+)(ms|s)$", System.Text.RegularExpressions.RegexOptions.Compiled)

    let parseDuration (raw: string) : Result<TimeSpan, string> =
        if String.IsNullOrWhiteSpace raw then
            Error "duration 값이 비어있습니다."
        else
            let m = durationRegex.Match(raw.Trim())
            if not m.Success then
                Error (sprintf "'%s' 인식 불가. 형식: <정수>ms 또는 <정수>s (예: 500ms, 2s)." raw)
            else
                let n = Int32.Parse(m.Groups.[1].Value)
                // regex 가 (ms|s) 만 capture — Major 3 review: unreachable fallback 제거.
                if m.Groups.[2].Value = "ms" then Ok (TimeSpan.FromMilliseconds(float n))
                else Ok (TimeSpan.FromSeconds(float n))

    // ─── JsonElement 안전 lookup helpers ────────────────────────────────────

    let tryProp (el: JsonElement) (name: string) : JsonElement option =
        if el.ValueKind <> JsonValueKind.Object then None
        else
            match el.TryGetProperty(name) with
            | true, v -> Some v
            | false, _ -> None

    let tryString (el: JsonElement) : string option =
        if el.ValueKind = JsonValueKind.String then Some (el.GetString())
        else None

    let requireString (el: JsonElement) (path: string) : string =
        match tryString el with
        | Some s -> s
        | None -> invalidOp (sprintf "%s %s: string 기대 (실제 %A)." VALIDATION_ERROR path el.ValueKind)

    let requireProp (el: JsonElement) (name: string) (path: string) : JsonElement =
        match tryProp el name with
        | Some v -> v
        | None -> invalidOp (sprintf "%s %s: '%s' 키 누락." VALIDATION_ERROR path name)

    // ─── Levenshtein distance (validate 의 가까운 후보 제안) ────────────────

    let private levenshtein (a: string) (b: string) : int =
        let la = a.Length
        let lb = b.Length
        if la = 0 then lb
        elif lb = 0 then la
        else
            let v0 = Array.zeroCreate<int> (lb + 1)
            let v1 = Array.zeroCreate<int> (lb + 1)
            for j = 0 to lb do v0.[j] <- j
            for i = 0 to la - 1 do
                v1.[0] <- i + 1
                for j = 0 to lb - 1 do
                    let cost = if a.[i] = b.[j] then 0 else 1
                    v1.[j + 1] <- min (min (v1.[j] + 1) (v0.[j + 1] + 1)) (v0.[j] + cost)
                Array.blit v1 0 v0 0 (lb + 1)
            v0.[lb]

    /// candidates 중 target 과 가까운 top-N (distance <= threshold) 반환.
    let nearestCandidates (target: string) (candidates: string seq) (top: int) : string list =
        candidates
        |> Seq.map (fun c -> c, levenshtein target c)
        |> Seq.sortBy snd
        |> Seq.truncate top
        |> Seq.map fst
        |> Seq.toList

    // ─── Arrow type parse ────────────────────────────────────────────────────

    let parseArrowType (raw: string) : Result<ArrowType, string> =
        match raw.Trim() with
        | "Start" -> Ok ArrowType.Start
        | "Reset" -> Ok ArrowType.Reset
        | "StartReset" -> Ok ArrowType.StartReset
        | "ResetReset" -> Ok ArrowType.ResetReset
        | "Group" -> Ok ArrowType.Group
        | "Unspecified" -> Ok ArrowType.Unspecified
        | other -> Error (sprintf "arrow type '%s' 미지원. 허용: Start|Reset|StartReset|ResetReset|Group|Unspecified." other)

    // ─── Enum parse helpers (Phase 7 §4.2 C-1) ───────────────────────────────
    //
    // SSOT yaml-protocol-v0.md §2.x 의 enum 라벨 ↔ Ds2.Core enum 변환.
    // ConditionType / ContactKind / CallType / ApiDefActionType — emit/apply 양쪽 호출
    // 예정 (C-3 ~ C-5). 본 phase 는 helper 만 추가 — 기존 동작 영향 0건.
    // 형식은 parseArrowType 패턴 답습 (Result<_, string>) — error 메시지에 허용 라벨 enumerate.

    let parseConditionType (raw: string) : Result<ConditionType, string> =
        match raw.Trim() with
        | "AutoAux" -> Ok ConditionType.AutoAux
        | "ComAux" -> Ok ConditionType.ComAux
        | "SkipAction" -> Ok ConditionType.SkipAction
        | other -> Error (sprintf "condition type '%s' 미지원. 허용: AutoAux|ComAux|SkipAction." other)

    let parseContactKind (raw: string) : Result<ContactKind, string> =
        match raw.Trim() with
        | "NoContact" -> Ok ContactKind.NoContact
        | "NcContact" -> Ok ContactKind.NcContact
        | "RisingPulse" -> Ok ContactKind.RisingPulse
        | "FallingPulse" -> Ok ContactKind.FallingPulse
        | "Inverter" -> Ok ContactKind.Inverter
        | other -> Error (sprintf "contactKind '%s' 미지원. 허용: NoContact|NcContact|RisingPulse|FallingPulse|Inverter." other)

    let parseCallType (raw: string) : Result<CallType, string> =
        match raw.Trim() with
        | "WaitForCompletion" -> Ok CallType.WaitForCompletion
        | "SkipIfCompleted" -> Ok CallType.SkipIfCompleted
        | other -> Error (sprintf "callType '%s' 미지원. 허용: WaitForCompletion|SkipIfCompleted." other)

    // v10 ActionType/SensingType grammar — spec §8 Smart Constructor 표기.
    //   Action  : normal | pulse | set | timeAppend(<ms>) | pulseHold(<ms>) | virt | virtPlus(<ms>)
    //   Sensing : normal | edge  | latched | debounce(<ms>) | edgeStable(<ms>) | virt | virtPlus(<ms>)
    // raw DU 표기도 허용: Real(Level, None) / Real(OneShot, Append(<ms>)) / Virtual(None) / Virtual(Append(<ms>))
    let private signalTimeRegex =
        System.Text.RegularExpressions.Regex(
            @"^([A-Za-z][A-Za-z0-9]*)(?:\(\s*(\d+)\s*\))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    // TokenRole — Flags enum (None=0 / Source=1 / Ignore=2 / Sink=4). PoC scope: 단일 flag 만 직접 지원.
    // 복합 flag (예: `Source ||| Sink`) 인 store 값은 emit 시 forensic 라벨 (`Combined(<int>)`) 로 표현 — parse 측은 거부.
    let parseTokenRole (raw: string) : Result<TokenRole, string> =
        match raw.Trim() with
        | "None" -> Ok TokenRole.None
        | "Source" -> Ok TokenRole.Source
        | "Ignore" -> Ok TokenRole.Ignore
        | "Sink" -> Ok TokenRole.Sink
        | other -> Error (sprintf "tokenRole '%s' 미지원. 허용: None|Source|Ignore|Sink (PoC scope — 단일 flag 만)." other)

    let parseActionType (raw: string) : Result<ActionType, string> =
        let trimmed = raw.Trim()
        let m = signalTimeRegex.Match(trimmed)
        if not m.Success then
            Error (sprintf "actionType '%s' 인식 불가. 형식: normal|pulse|set|timeAppend(<ms>)|pulseHold(<ms>)|virt|virtPlus(<ms>)." raw)
        else
            let caseName = m.Groups.[1].Value
            let hasArg = m.Groups.[2].Success
            let arg () = int m.Groups.[2].Value
            match caseName, hasArg with
            | "normal",     false -> Ok (ActionType.Real (Level,   None))
            | "pulse",      false -> Ok (ActionType.Real (OneShot, None))
            | "set",        false -> Ok (ActionType.Real (Latched, None))
            | "timeAppend", true  -> Ok (ActionType.Real (Level,   Some (Append (arg ()))))
            | "pulseHold",  true  -> Ok (ActionType.Real (OneShot, Some (Append (arg ()))))
            | "virt",       false -> Ok (ActionType.Virtual None)
            | "virtPlus",   true  -> Ok (ActionType.Virtual (Some (Append (arg ()))))
            | _ ->
                Error (sprintf "actionType '%s' — case 이름과 인자 개수 불일치. normal/pulse/set/virt 는 인자 없음, timeAppend/pulseHold/virtPlus 는 (ms)." raw)

    let parseSensingType (raw: string) : Result<SensingType, string> =
        let trimmed = raw.Trim()
        let m = signalTimeRegex.Match(trimmed)
        if not m.Success then
            Error (sprintf "sensingType '%s' 인식 불가. 형식: normal|edge|latched|debounce(<ms>)|edgeStable(<ms>)|virt|virtPlus(<ms>)." raw)
        else
            let caseName = m.Groups.[1].Value
            let hasArg = m.Groups.[2].Success
            let arg () = int m.Groups.[2].Value
            match caseName, hasArg with
            | "normal",     false -> Ok (SensingType.Real (Level,   None))
            | "edge",       false -> Ok (SensingType.Real (OneShot, None))
            | "latched",    false -> Ok (SensingType.Real (Latched, None))
            | "debounce",   true  -> Ok (SensingType.Real (Level,   Some (Append (arg ()))))
            | "edgeStable", true  -> Ok (SensingType.Real (OneShot, Some (Append (arg ()))))
            | "virt",       false -> Ok (SensingType.Virtual None)
            | "virtPlus",   true  -> Ok (SensingType.Virtual (Some (Append (arg ()))))
            | _ ->
                Error (sprintf "sensingType '%s' — case 이름과 인자 개수 불일치. normal/edge/latched/virt 는 인자 없음, debounce/edgeStable/virtPlus 는 (ms)." raw)

    // ─── Arrow 표기 parse: "A -> B : Type" ──────────────────────────────────

    /// "A -> B : Type" / "A -> B" (type 누락 — validate 에러) 분해.
    type ArrowSpec = {
        FromRaw: string
        ToRaw: string
        TypeRaw: string option
    }

    /// Arrow 표기 추출 — JsonElement 가 두 형태일 수 있음:
    /// - String: `"Adv -> Ret : Start"` (사용자 explicit quoted)
    /// - Object 1-key: `{"Adv -> Ret": "Start"}` (YAML 자연 형태 — `:` 가 mapping separator 로 해석)
    /// 두 케이스 모두 raw arrow string 으로 정규화.
    let extractArrowString (el: JsonElement) : Result<string, string> =
        match el.ValueKind with
        | JsonValueKind.String -> Ok (el.GetString())
        | JsonValueKind.Object ->
            // 1-key object: key = "<from> -> <to>", value = type string
            let props = el.EnumerateObject() |> Seq.toList
            match props with
            | [ p ] ->
                match tryString p.Value with
                | Some t -> Ok (sprintf "%s : %s" p.Name t)
                | None -> Error "arrow object 의 value 가 string 이 아닙니다."
            | _ -> Error "arrow object 는 정확히 1 key (\"<from> -> <to>\": <Type>) 여야 합니다."
        | _ -> Error (sprintf "arrow 항목은 string 또는 1-key object 기대 (실제 %A)." el.ValueKind)

    let parseArrowSpec (raw: string) : Result<ArrowSpec, string> =
        if String.IsNullOrWhiteSpace raw then
            Error "arrow 표기가 비어있습니다."
        else
            // `:` 분리 — type 부분
            let colonIdx = raw.LastIndexOf(':')
            let beforeType, typeRaw =
                if colonIdx >= 0 then
                    raw.Substring(0, colonIdx), Some (raw.Substring(colonIdx + 1).Trim())
                else raw, None
            // `->` 분리
            let arrowIdx = beforeType.IndexOf("->")
            if arrowIdx < 0 then
                Error (sprintf "arrow 표기 '%s' 형식 위반. '<From> -> <To> : <Type>' 사용." raw)
            else
                let fromR = beforeType.Substring(0, arrowIdx).Trim()
                let toR = beforeType.Substring(arrowIdx + 2).Trim()
                if String.IsNullOrWhiteSpace fromR then
                    Error "arrow source 가 비어있습니다."
                elif String.IsNullOrWhiteSpace toR then
                    Error "arrow target 이 비어있습니다."
                else
                    Ok { FromRaw = fromR; ToRaw = toR; TypeRaw = typeRaw }

    // ─── Name table — 1-pass forward-ref 해소 ───────────────────────────────
    //
    // YAML 의 `calls: [Sys.API]` / arrows source/target 는 forward-ref 가 자유 (선언 순서 무관).
    // SSOT §2.5 의 "1-pass 이름 테이블 구축 → 2-pass GUID resolve" 패턴.

    type SystemEntry = {
        Name: string
        Kind: string  // "active" | "passive"
        SystemId: Guid option ref  // 2-pass 에서 채워짐
        ApiDefIds: Dictionary<string, Guid>  // ApiDef name → Guid (passive 의 cascade 결과)
        FlowIds: Dictionary<string, Guid>    // Flow name → Guid (active)
        // work 이름은 system-unique (D1) — flat lookup 으로 cross-flow work arrow 를 1-pass O(1) resolve.
        // 옛 2-level `WorkIds (flowName→work→Guid)` 는 flow 안 arrows 폐기로 read 용도 소멸 → 평탄 dict 로 단일화.
        WorkIdsByName: Dictionary<string, Guid>  // workLocalName → Guid (system 전역)
    }

    let private newSystemEntry name kind = {
        Name = name
        Kind = kind
        SystemId = ref None
        ApiDefIds = Dictionary<string, Guid>(StringComparer.Ordinal)
        FlowIds = Dictionary<string, Guid>(StringComparer.Ordinal)
        WorkIdsByName = Dictionary<string, Guid>(StringComparer.Ordinal)
    }

    // ─── Schema dispatcher ──────────────────────────────────────────────────
    //
    // 입력 = JsonElement (root = `protocol` / `project` / `systems` / `patch` 키를 가진 object).
    // 출력 = Diagnostics + (성공 시) plan.Operations 누적.

    type ApplyContext = {
        Plan: ImportPlanBuilder
        Store: DsStore
        Diagnostics: Diagnostics
        /// system name → SystemEntry. forward-ref 해소용.
        Systems: Dictionary<string, SystemEntry>
        /// Phase 7 §10.2 #31 S3b — wire 의 `level:` 키가 결정. apply 진입부에서 mutable 1회 set.
        /// Modeling 시 dispatch helper 가 *lookup-first* 분기 (기존 store entity 발견 시 reuse +
        /// leaf-only mutate, missing 키 = no-op). Full 시 기존 동작 (queueAddXxx 중복 throw).
        Level: Internal.ModelingCategory.ExportLevel ref
    }

    let private newContext (plan: ImportPlanBuilder) (store: DsStore) : ApplyContext = {
        Plan = plan
        Store = store
        Diagnostics = Diagnostics()
        Systems = Dictionary<string, SystemEntry>(StringComparer.Ordinal)
        Level = ref Internal.ModelingCategory.Full
    }

    // ─── Plan / Call.Properties helpers (Phase 7 §4.2 C-3/C-5) ───────────────
    //
    // dispatchPassiveSystem / dispatchWork 양쪽에서 사용 — ApplyContext 정의 직후 위치 (forward-ref 회피).
    //
    // #13 (todo §10.2) — `tryFindXxxInPlan` SSOT 완전 일원화. `Ds2.LlmAgent.Internal.PlanLookup`
    // 신규 module 신설 (PlanLookup.fs) → ToolOperations.fs + 본 파일 양쪽 file-scoped 중복 제거.
    // local alias 만 노출 (private scope 유지 — 도메인 의미 변화 없음).

    let private tryFindCallInPlan   = Internal.PlanLookup.tryFindCall
    let private tryFindApiDefInPlan = Internal.PlanLookup.tryFindApiDef
    let private tryFindProjectInPlan= Internal.PlanLookup.tryFindProject
    let private tryFindSystemInPlan = Internal.PlanLookup.tryFindSystem
    let private tryFindWorkInPlan   = Internal.PlanLookup.tryFindWork
    let private tryFindFlowInPlan   = Internal.PlanLookup.tryFindFlow

    // ─── Plan+Store unified lookup (Phase 7 §4.2 helper 3종 추출) ─────────────
    //
    // `tryFindXxxInPlan` (본 turn add) + `Queries.getXxx` (기존 store) 의 2-stage fallback chain
    // 통합. 본 turn 새로 add 된 entity 는 plan operations 에서 추적되고, 기존 store entity 는
    // Queries 에서 lookup. 양쪽 모두 None 이면 None.
    //
    // 명명: 기존 `resolveApiDef` (name 기반, line 677) / `resolveProjectKey` (line 457) 와 충돌
    // 회피 위해 `lookupXxxById` 명명 사용 — apply 측 leaf 키 setter 패턴 (`IRI` / `TokenRole`
    // / `Author` / `Version` / `actionType` / `description` 등) 에서 5+ 회 누적 사용.

    // generic 2-stage fallback (#15 — todo §10.2). plan operations 우선 + store fallback.
    //
    // **사용 시나리오 (apply 단계 — leaf 키 setter)**: YAML 입력 적용 중 외부 GUID 참조 해결.
    // 이번 turn 새로 add 된 entity 는 아직 store commit 전이므로 `ImportPlanBuilder` 의 누적
    // operation 에서만 발견됨 → plan 측을 *우선* 검색. 기존 store entity (이전 turn commit) 는
    // store fallback. 부재 시 `None` 반환 — 호출자는 silent skip + entity-default fallback 정합
    // (SSOT §4 default 정책). leaf 키 setter (`IRI` / `actionType` / `description` 등) 6+ 회 사용.
    //
    // **`ToolOperations.requireFromStoreOrPlan` 와 의도된 비대칭** (외부 reviewer M4 — 통합 보류):
    //   * 본 helper: plan-first → store. Optional 반환. silent skip 정합 (apply 단계).
    //   * `requireFromStoreOrPlan`: store-first → plan. invalidOp 발생. fail-fast 정합 (commit 단계).
    // 검색 순서 통일 시 한쪽이 stale entity 를 lookup 가능 (apply 측이 store-first 면 같은 turn
    // 신규 add 미반영) — 두 helper 분리 유지 정책. 통합 의향이 있다면 시점/반환타입 차이 우선 해소 필요.
    let private lookupById
        (planFinder: ImportPlanBuilder -> Guid -> 'T option)
        (storeFinder: Guid -> DsStore -> 'T option)
        (ctx: ApplyContext) (id: Guid) : 'T option =
        planFinder ctx.Plan id
        |> Option.orElseWith (fun () -> storeFinder id ctx.Store)

    let private lookupSystemById  ctx id = lookupById tryFindSystemInPlan  Queries.getSystem  ctx id
    let private lookupCallById    ctx id = lookupById tryFindCallInPlan    Queries.getCall    ctx id
    let private lookupApiDefById  ctx id = lookupById tryFindApiDefInPlan  Queries.getApiDef  ctx id
    let private lookupProjectById ctx id = lookupById tryFindProjectInPlan Queries.getProject ctx id
    let private lookupWorkById    ctx id = lookupById tryFindWorkInPlan    Queries.getWork    ctx id
    let private lookupFlowById    ctx id = lookupById tryFindFlowInPlan    Queries.getFlow    ctx id

    // ─── Property apply helpers (Phase 7 §4.2 — leaf 키 setter 패턴 통합) ─────
    //
    // `tryProp + tryString + Option.iter` (또는 + parser) 3~4-step 패턴이 6+ 회 누적 — 통합.

    /// 진단 키 합성 — path 가 빈 string (root-level) 인 경우 leading-dot 회피 (외부 review M-1).
    let private joinDiagKey (path: string) (key: string) : string =
        if path = "" then key else path + "." + key

    /// `tryProp el key + tryString + setter` — string property 적용.
    /// 키 부재 → no-op (entity-default fallback 정합, SSOT §4). 키 존재 + non-string → diag 발행
    /// (SSOT §2.7 룰 #21~#24 silent skip 금지 정책 정합 — 외부 reviewer M-F).
    let private applyStringProp
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string)
        (setter: string -> unit) : unit =
        tryProp el key
        |> Option.iter (fun valEl ->
            match tryString valEl with
            | Some s -> setter s
            | None ->
                ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string 기대 (실제 %A)." valEl.ValueKind))

    /// `string option` 도메인 leaf 의 *wire 정규화* 적용 (#16 — M1/M2 외부 review 반영).
    ///
    /// **정책**: `null` / `""` / 키 부재 — 3 case 모두 setter 미 호출 → store 표면값 = entity-default (대부분 `None`).
    /// emit 측 `Option.filter (not << String.IsNullOrEmpty)` 가드와 짝 → wire round-trip 정합.
    /// → 외부 도구가 IRI 등을 reset 하려면 키 *제거* 권장. `iri: null` / `iri: ""` 도 동일 결과 (silent skip).
    ///
    /// **비대칭 주의** (SSOT §6.3): `readStringOptKey` (plc leaf) 는 정반대 정책 — `null → Some None`,
    /// `"" → Some ""` 모두 보존. 같은 `string option` 도메인이라도 *wire 정규화 vs PoC 보존* 두 정책 공존.
    /// 호출자는 entity 의 domain semantic 에 맞춰 helper 선택 (외부 reviewer M2 — 함수명으로 구별 가시화).
    let private applyNonEmptyStringProp
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string)
        (setter: string -> unit) : unit =
        tryProp el key
        |> Option.iter (fun valEl ->
            match valEl.ValueKind with
            | JsonValueKind.Null -> ()  // null → None 정규화 (reset semantic)
            | _ ->
                match tryString valEl with
                | Some s when not (String.IsNullOrEmpty s) -> setter s
                | Some _ -> ()  // 빈 string → None 정규화
                | None ->
                    ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string 기대 (실제 %A)." valEl.ValueKind))

    /// `tryProp el key + tryString + parser + setter` — enum property 적용.
    /// 키 부재 → no-op. 키 존재 + non-string → diag (#23). parse 실패 → diag (#19/#20).
    let private applyEnumProp
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string)
        (parser: string -> Result<'a, string>)
        (setter: 'a -> unit) : unit =
        tryProp el key
        |> Option.iter (fun valEl ->
            match tryString valEl with
            | Some s ->
                match parser s with
                | Ok v -> setter v
                | Error msg -> ctx.Diagnostics.Add(joinDiagKey path key, msg)
            | None ->
                ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string 기대 (실제 %A)." valEl.ValueKind))

    /// Call.Properties 안 SimulationCallProperties 의 CallType 추출 (없으면 None).
    let private callTypeOf (c: Call) : CallType option =
        c.Properties
        |> Seq.tryPick (function
            | SimulationCall props -> Some props.CallType
            | _ -> None)

    /// Call.Properties 안 SimulationCallProperties 의 CallType 설정. 기존 SimulationCall 있으면 mutate, 없으면 append.
    let private setCallType (c: Call) (ct: CallType) : unit =
        let idxOpt =
            c.Properties
            |> Seq.indexed
            |> Seq.tryPick (fun (i, p) ->
                match p with SimulationCall _ -> Some i | _ -> None)
        match idxOpt with
        | Some i ->
            match c.Properties.[i] with
            | SimulationCall props -> props.CallType <- ct
            | _ -> ()
        | None ->
            let props = SimulationCallProperties()
            props.CallType <- ct
            c.Properties.Add(SimulationCall props)

    // ─── PLC metadata apply (Phase 7 §4.2 C-7.1) ─────────────────────────────
    //
    // SSOT §2.2.2 (C-7.1): entity 안 `plc:` sub-section — ControlSystemProperties /
    // ControlFlowProperties / ControlWorkProperties / ControlCallProperties 의 단순
    // leaf scalar 만 처리 (복합 collection 은 C-7.2 후속 phase).
    //
    // **Default 정책**: 키 부재 → no-op (type-default 유지). 키 존재 → mutate.
    // emit 측이 type-default 와 다른 값만 emit 하므로 round-trip 정합.
    //
    // **TimeSpan**: `TimeSpan.TryParse` — `"00:00:05"` / `"00:00:00.005"` (ms) 모두 지원.
    //
    // **Runtime-only 제외** (派 분류): CurrentState / LastExecutionTime / ExecutionCount /
    // ErrorCount (Work) — emit 안 하므로 plc 키 진단에서 unknown 으로 거부 (round-trip 0건).

    let private readBoolKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: bool -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match v.ValueKind with
            | JsonValueKind.True -> setter true
            | JsonValueKind.False -> setter false
            | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "bool 기대 (실제 %A)." v.ValueKind))

    let private readIntKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: int -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            if v.ValueKind = JsonValueKind.Number then
                let ok, n = v.TryGetInt32()
                if ok then setter n
                else ctx.Diagnostics.Add(joinDiagKey path key, "int 기대 (overflow 또는 not integer).")
            else
                ctx.Diagnostics.Add(joinDiagKey path key, sprintf "int 기대 (실제 %A)." v.ValueKind))

    let private readFloatKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: float -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            if v.ValueKind = JsonValueKind.Number then
                let ok, n = v.TryGetDouble()
                if ok then setter n
                else ctx.Diagnostics.Add(joinDiagKey path key, "float 기대.")
            else
                ctx.Diagnostics.Add(joinDiagKey path key, sprintf "float 기대 (실제 %A)." v.ValueKind))

    let private readStringKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: string -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match tryString v with
            | Some s -> setter s
            | None -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string 기대 (실제 %A)." v.ValueKind))

    /// string | null → Some s / None. 빈 string ("") 은 그대로 Some "" (정규화 없음 — PoC).
    let private readStringOptKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: string option -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match v.ValueKind with
            | JsonValueKind.Null -> setter None
            | JsonValueKind.String -> setter (Some (v.GetString()))
            | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string | null 기대 (실제 %A)." v.ValueKind))

    let private readIntOptKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: int option -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match v.ValueKind with
            | JsonValueKind.Null -> setter None
            | JsonValueKind.Number ->
                let ok, n = v.TryGetInt32()
                if ok then setter (Some n)
                else ctx.Diagnostics.Add(joinDiagKey path key, "int 기대 (overflow 또는 not integer).")
            | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "int | null 기대 (실제 %A)." v.ValueKind))

    let private readFloatOptKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: float option -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match v.ValueKind with
            | JsonValueKind.Null -> setter None
            | JsonValueKind.Number ->
                let ok, n = v.TryGetDouble()
                if ok then setter (Some n)
                else ctx.Diagnostics.Add(joinDiagKey path key, "float 기대.")
            | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "float | null 기대 (실제 %A)." v.ValueKind))

    let private readTimeSpanKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: TimeSpan -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match tryString v with
            | Some s ->
                let ok, ts = TimeSpan.TryParse s
                if ok then setter ts
                else ctx.Diagnostics.Add(joinDiagKey path key, sprintf "TimeSpan 형식 위반 ('%s')." s)
            | None -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string 기대 (실제 %A)." v.ValueKind))

    let private readTimeSpanOptKey
        (ctx: ApplyContext) (path: string) (el: JsonElement) (key: string) (setter: TimeSpan option -> unit) : unit =
        tryProp el key
        |> Option.iter (fun v ->
            match v.ValueKind with
            | JsonValueKind.Null -> setter None
            | JsonValueKind.String ->
                let s = v.GetString()
                let ok, ts = TimeSpan.TryParse s
                if ok then setter (Some ts)
                else ctx.Diagnostics.Add(joinDiagKey path key, sprintf "TimeSpan 형식 위반 ('%s')." s)
            | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "string | null 기대 (실제 %A)." v.ValueKind))

    /// 알려진 키 외 entry → 진단 발행 (SSOT §2.7 unknown 키 거부 정합).
    let private warnUnknownPlcKeys
        (ctx: ApplyContext) (path: string) (el: JsonElement) (known: Set<string>) : unit =
        for prop in el.EnumerateObject() do
            if not (Set.contains prop.Name known) then
                ctx.Diagnostics.Add(joinDiagKey path prop.Name, sprintf "알 수 없는 plc 키 '%s'." prop.Name)

    // ─── PLC metadata leaf SSOT (#20 — todo §10.2) ───────────────────────────
    //
    // **#25 (todo §10.2)** — leaves table 정의는 `Ds2.LlmAgent.Internal.PlcMetadata`
    // 로 분리 (capturer 도 같은 SSOT 참조). PlcLeafKind / PlcLeaf / 4 leaves table 모두
    // 본 module 안 type/list 정의 *제거됨* — `open PlcMetadata` 로 case 직접 사용.
    //
    // type-default 비교: `defaultCp` 매개변수 (entity 의 빈 instance — 생성자 결과) 와 cur 비교.
    // emit/apply/hasNonDefault generic helper 는 본 module 안 ApplyContext 의존성 때문에 유지.

    open Ds2.LlmAgent.Internal.PlcMetadata
    open Ds2.LlmAgent.Internal.ModelingCategory

    /// leaves 기반 apply — 미지의 키 진단 발행 포함.
    let private parsePlcLeaves
        (ctx: ApplyContext) (path: string) (plcEl: JsonElement)
        (cp: 'cp) (leaves: PlcLeaf<'cp> list) : unit =
        for leaf in leaves do
            match leaf.Kind with
            | LBool        (_, setter) -> readBoolKey ctx path plcEl leaf.Key (setter cp)
            | LInt         (_, setter) -> readIntKey ctx path plcEl leaf.Key (setter cp)
            | LFloat       (_, setter) -> readFloatKey ctx path plcEl leaf.Key (setter cp)
            | LString      (_, setter) -> readStringKey ctx path plcEl leaf.Key (setter cp)
            | LStringOpt   (_, setter) -> readStringOptKey ctx path plcEl leaf.Key (setter cp)
            | LIntOpt      (_, setter) -> readIntOptKey ctx path plcEl leaf.Key (setter cp)
            | LFloatOpt    (_, setter) -> readFloatOptKey ctx path plcEl leaf.Key (setter cp)
            | LTimeSpan    (_, setter) -> readTimeSpanKey ctx path plcEl leaf.Key (setter cp)
            | LTimeSpanOpt (_, setter) -> readTimeSpanOptKey ctx path plcEl leaf.Key (setter cp)
        let known = leaves |> List.map (fun l -> l.Key) |> Set.ofList
        warnUnknownPlcKeys ctx path plcEl known

    /// entity 별 wrapper — shape 검사 + ControlXxxProperties 인스턴스 ensure + parsePlcLeaves 위임.
    let private parsePlcSystem
        (ctx: ApplyContext) (path: string) (sys: DsSystem) (plcEl: JsonElement) : unit =
        if plcEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "plc: Object 기대 (실제 %A)." plcEl.ValueKind)
        else
            let cp =
                match sys.GetControlProperties() with
                | Some cp -> cp
                | None -> let cp = ControlSystemProperties() in sys.SetControlProperties(cp); cp
            parsePlcLeaves ctx path plcEl cp plcSystemLeaves

    let private parsePlcFlow
        (ctx: ApplyContext) (path: string) (flow: Flow) (plcEl: JsonElement) : unit =
        if plcEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "plc: Object 기대 (실제 %A)." plcEl.ValueKind)
        else
            let cp =
                match flow.GetControlProperties() with
                | Some cp -> cp
                | None -> let cp = ControlFlowProperties() in flow.SetControlProperties(cp); cp
            parsePlcLeaves ctx path plcEl cp plcFlowLeaves

    let private parsePlcWork
        (ctx: ApplyContext) (path: string) (wk: Work) (plcEl: JsonElement) : unit =
        if plcEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "plc: Object 기대 (실제 %A)." plcEl.ValueKind)
        else
            let cp =
                match wk.GetControlProperties() with
                | Some cp -> cp
                | None -> let cp = ControlWorkProperties() in wk.SetControlProperties(cp); cp
            parsePlcLeaves ctx path plcEl cp plcWorkLeaves

    let private parsePlcCall
        (ctx: ApplyContext) (path: string) (c: Call) (plcEl: JsonElement) : unit =
        if plcEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "plc: Object 기대 (실제 %A)." plcEl.ValueKind)
        else
            let cp =
                match c.GetControlProperties() with
                | Some cp -> cp
                | None -> let cp = ControlCallProperties() in c.SetControlProperties(cp); cp
            parsePlcLeaves ctx path plcEl cp plcCallLeaves

    // ─── Pass 1 — 이름 테이블 빌드 ──────────────────────────────────────────

    let private collectSystems (ctx: ApplyContext) (systemsEl: JsonElement) : unit =
        if systemsEl.ValueKind <> JsonValueKind.Array then
            ctx.Diagnostics.Add("systems", "Array 기대.")
        else
            let mutable idx = 0
            for sysEl in systemsEl.EnumerateArray() do
                let path = sprintf "systems[%d]" idx
                match tryProp sysEl "system" with
                | None -> ctx.Diagnostics.Add(path, "'system' 키 누락 (이름 필수).")
                | Some nameEl ->
                    match tryString nameEl with
                    | None -> ctx.Diagnostics.Add(joinDiagKey path "system", "string 기대.")
                    | Some name ->
                        let kindRaw =
                            tryProp sysEl "kind"
                            |> Option.bind tryString
                        match kindRaw with
                        | None ->
                            ctx.Diagnostics.Add(path, "kind 누락. 'active' 또는 'passive' 명시 필요.")
                        | Some kind when kind <> "active" && kind <> "passive" ->
                            ctx.Diagnostics.Add(joinDiagKey path "kind", sprintf "'%s' 미지원. 'active' 또는 'passive' 만 허용." kind)
                        | Some kind ->
                            // kind 와 키 정합성 체크 (SSOT §2.7 룰 6) — active 전용 키 = flows (system 직속 mapping).
                            let hasFlowKey = tryProp sysEl "flows" |> Option.isSome
                            let hasDeviceKey = tryProp sysEl "device" |> Option.isSome
                            if kind = "passive" && hasFlowKey then
                                ctx.Diagnostics.Add(path, "kind=passive 인데 flows 키 존재. 어느 한쪽 수정.")
                            if kind = "active" && hasDeviceKey then
                                ctx.Diagnostics.Add(path, "kind=active 인데 device 키 존재. 어느 한쪽 수정.")
                            if ctx.Systems.ContainsKey name then
                                ctx.Diagnostics.Add(joinDiagKey path "system", sprintf "'%s' 시스템 이름 중복." name)
                            else
                                ctx.Systems.[name] <- newSystemEntry name kind
                idx <- idx + 1

    // ─── Pass 2 — Project / System 생성 + device cascade ────────────────────

    let private resolveProjectKey (ctx: ApplyContext) (root: JsonElement) : Guid option =
        // SSOT §4: project 키 처리 — store 상태 + project 키 조합으로 분기.
        let storeProjects = Queries.allProjects ctx.Store
        let projectKey = tryProp root "project" |> Option.bind tryString
        match storeProjects, projectKey with
        | [], None ->
            ctx.Diagnostics.Add("project", "빈 store 에서 시작하려면 project 이름 명시 필요.")
            None
        | [], Some name ->
            // 새 project 생성
            try
                Some (ToolOperations.queueAddProject ctx.Plan ctx.Store name)
            with ex ->
                ctx.Diagnostics.Add("project", ex.Message)
                None
        | p :: _, None ->
            Some p.Id
        | p :: _, Some name when name = p.Name ->
            Some p.Id
        | p :: _, Some other ->
            ctx.Diagnostics.Add(
                "project",
                sprintf "프로젝트 '%s' 가 이미 열려 있습니다. '%s' 로 바꾸려면 '파일 > 닫기' 후 재시도하세요." p.Name other)
            None

    /// M1 fix: doc-level entity 이름 sanitize 가드 (RLO/ZWJ/Cc/Cf/`@`/`$` prefix/`.` 차단 + 길이 검사).
    /// Phase 5 cleanup 으로 op-layer 도구의 `SanitizeOrThrow` 가 제거되면서 doc-level path 가
    /// sanitize 우회 회귀 — `ToolOperations.sanitizeName` 위임으로 동일 정책 복원.
    /// 메시지 ≠ "" 이면 ctx.Diagnostics.Add 후 false 반환 (호출자는 dispatch skip 책임).
    let private tryValidateName (ctx: ApplyContext) (path: string) (field: string) (name: string) : bool =
        let msg = ToolOperations.sanitizeName name field ToolOperations.NameMaxLength
        if msg = "" then true
        else
            ctx.Diagnostics.Add(path, msg)
            false

    /// device sugar 의 default 매핑 (SSOT §2.3 표). UnknownSugar 는 호출처에서 사전 분기 — 본 함수 도달 불가.
    /// known sugar 3종 = `KnownSugars` SSOT 표 lookup (Phase 2.5 M4). Custom 은 `customDefault*` 상수 (Phase 2.5 cycle2 M1).
    let private deviceDefaults (lit: DeviceLiteral) : string list * string * TimeSpan =
        let pick (spec: KnownSugarSpec) = spec.DefaultApis, spec.DefaultOpposing, spec.DefaultDuration
        match lit with
        | KnownCylinder    -> pick KnownSugars.cylinder
        | KnownClamp       -> pick KnownSugars.clamp
        | KnownRobot       -> pick KnownSugars.robot
        | Custom _         -> KnownSugars.customDefaultApis, KnownSugars.customDefaultOpposing, KnownSugars.customDefaultDuration
        | UnknownSugar raw -> failwithf "deviceDefaults: UnknownSugar '%s' 는 호출처에서 분기 처리되어야 합니다." raw

    // S3b — dispatch helper 의 modeling lookup-first 분기가 사용하므로 이 위치에 위치.
    // 기존에는 line 1478~ (applyPatch 영역 인근) 에 정의됐으나 dispatchActiveSystem / dispatchPassiveSystem
    // 보다 *뒤* 였음 — forward-ref 회피로 이동.
    /// 단일 segment system path → store 안의 DsSystem 검색 (active + passive 합집합).
    /// **Scope 주의**: store 전체 projects 를 walk — 단일 project PoC scope (Promaker 는 SDI, `resolveFirstProjectId`
    /// 가 첫 project 만 사용) 에서는 effectively single-project lookup. 장기 multi-project 확장 시 projectId 인자
    /// 추가 필요 (review M-D — `ToolOperations.findSystemNameClashInProject` 와의 의미 정렬).
    let private findSystemByName (store: DsStore) (sysName: string) : DsSystem option =
        Queries.allProjects store
        |> List.collect (fun p ->
            (Queries.activeSystemsOf p.Id store)
            @ (Queries.passiveSystemsOf p.Id store))
        |> List.tryFind (fun s -> s.Name = sysName)

    /// `<system>` 또는 `<project>.<system>` 형식 path → store 안의 DsSystem 검색.
    let private findSystemByPath (store: DsStore) (rawPath: string) : DsSystem option =
        match pathSegments rawPath with
        | [ sysName ] -> findSystemByName store sysName
        | [ projectName; sysName ] ->
            Queries.allProjects store
            |> List.tryFind (fun p -> p.Name = projectName)
            |> Option.bind (fun p ->
                Queries.projectSystemsOf p.Id store
                |> List.tryFind (fun s -> s.Name = sysName))
        | _ -> None

    /// `<system>.<flow>` 또는 `<project>.<system>.<flow>` 형식 path → store 안의 Flow 검색.
    let private findFlowByPath (store: DsStore) (rawPath: string) : Flow option =
        match pathSegments rawPath with
        | [ sysName; flowName ] ->
            findSystemByName store sysName
            |> Option.bind (fun s ->
                Queries.flowsOf s.Id store
                |> List.tryFind (fun f -> f.Name = flowName))
        | [ projectName; sysName; flowName ] ->
            Queries.allProjects store
            |> List.tryFind (fun p -> p.Name = projectName)
            |> Option.bind (fun p ->
                Queries.projectSystemsOf p.Id store
                |> List.tryFind (fun s -> s.Name = sysName)
                |> Option.bind (fun s ->
                    Queries.flowsOf s.Id store
                    |> List.tryFind (fun f -> f.Name = flowName)))
        | _ -> None

    /// patch.add 의 child 추가 경로에서 기존 store entity 를 resolveApiDef/dispatchWork 가 재사용할 수 있도록
    /// 현재 project systems 를 ctx.Systems 이름 테이블에 주입한다. 이미 doc/patch 안에서 선언된 entry 는 보존.
    let private seedStoreSystems (ctx: ApplyContext) : unit =
        for project in Queries.allProjects ctx.Store do
            let activeIds = HashSet<Guid>(project.ActiveSystemIds)
            Seq.append project.ActiveSystemIds project.PassiveSystemIds
            |> Seq.distinct
            |> Seq.iter (fun sysId ->
                match ctx.Store.Systems.TryGetValue sysId with
                | false, _ -> ()
                | true, sys ->
                    if not (ctx.Systems.ContainsKey sys.Name) then
                        let kind = if activeIds.Contains sys.Id then "active" else "passive"
                        let entry = newSystemEntry sys.Name kind
                        entry.SystemId := Some sys.Id

                        for apiDef in Queries.apiDefsOf sys.Id ctx.Store do
                            entry.ApiDefIds.[apiDef.Name] <- apiDef.Id

                        for flow in Queries.flowsOf sys.Id ctx.Store do
                            entry.FlowIds.[flow.Name] <- flow.Id
                            for work in Queries.worksOf flow.Id ctx.Store do
                                // work 이름 system-unique (D1) — flat 평탄 dict. 기존 store 가 (마이그레이션 이전)
                                // 동명 work 를 가질 수 있으나 seed 단계는 last-wins (export 측 D7 가드가 강제).
                                entry.WorkIdsByName.[work.LocalName] <- work.Id

                        ctx.Systems.[sys.Name] <- entry)

    /// S3b — modeling level Arrow lookup-first 용. 같은 (source, target, ArrowType) Work 간 arrow 가
    /// store 에 이미 있는지 검사. parent system 은 source Work → parent Flow → parent System chain.
    let private arrowWorkExists (store: DsStore) (s: Guid) (t: Guid) (aType: ArrowType) : bool =
        match Queries.getWork s store with
        | None -> false
        | Some srcW ->
            match Queries.getFlow srcW.ParentId store with
            | None -> false
            | Some srcF ->
                Queries.arrowWorksOf srcF.ParentId store
                |> List.exists (fun a -> a.SourceId = s && a.TargetId = t && a.ArrowType = aType)

    /// S3b — modeling level Arrow lookup-first 용. Call 간 arrow 의 parent = source Call.ParentId (= Work.Id).
    let private arrowCallExists (store: DsStore) (s: Guid) (t: Guid) (aType: ArrowType) : bool =
        match Queries.getCall s store with
        | None -> false
        | Some srcC ->
            Queries.arrowCallsOf srcC.ParentId store
            |> List.exists (fun a -> a.SourceId = s && a.TargetId = t && a.ArrowType = aType)

    let private dispatchPassiveSystem
        (ctx: ApplyContext)
        (entry: SystemEntry)
        (sysEl: JsonElement)
        (path: string) : unit =

        // M1 fix: passive system 이름 sanitize 가드.
        if not (tryValidateName ctx (path + ".system") "System name" entry.Name) then () else

        let deviceRaw = tryProp sysEl "device" |> Option.bind tryString
        let apisRaw =
            tryProp sysEl "apis"
            |> Option.bind (fun el ->
                if el.ValueKind = JsonValueKind.Array then
                    el.EnumerateArray()
                    |> Seq.choose tryString
                    |> Seq.toList
                    |> Some
                else None)
            // **Critical 2 (review)**: 사용자가 `apis: []` 명시 시 Some [] 가 반환되어 default 무력화 회피.
            // 빈 list 는 None 으로 정규화 → device 별 default (cylinder = [ADV;RET] 등) 적용.
            |> Option.bind (fun l -> if List.isEmpty l then None else Some l)
        let opposingRaw = tryProp sysEl "opposing" |> Option.bind tryString
        let workDurRaw = tryProp sysEl "workDuration" |> Option.bind tryString

        // duration 키 발견 시 친절 메시지 (SSOT 폐기 표기).
        if tryProp sysEl "duration" |> Option.isSome then
            ctx.Diagnostics.Add(joinDiagKey path "duration", "키 폐기됨. 'workDuration' 으로 변경하세요.")

        let workDuration =
            match workDurRaw with
            | None -> None
            | Some s ->
                match parseDuration s with
                | Ok ts -> Some ts
                | Error msg ->
                    ctx.Diagnostics.Add(joinDiagKey path "workDuration", msg)
                    None

        // S3b — modeling level 시 lookup-first. 기존 Passive 발견 시 device cascade skip +
        // store 의 ApiDef 목록을 entry.ApiDefIds 에 누적 (forward-ref 해소용 — Active 의 calls 가
        // `<Passive>.<ApiDef>` 로 참조 시 resolveApiDef 가 lookup 가능).
        // **M-2 (sub-agent review)**: wire 의 device 키가 store 의 SystemType 매핑과 mismatch
        // 시 silent ignore 회피 — 진단 발행. modeling level 은 device 변경 금지 (cascade 는 store
        // 그대로). apis / opposing / workDuration 도 mismatch 검사 가능하나 현 PoC 는 device 만
        // (가장 빈번한 사용자 입력 변경 = device sugar — apis 변경 시나리오 드물고 검증 분기 복잡).
        let modelingReuseHit =
            match !ctx.Level with
            | Modeling ->
                // #32 — store + plan 합집합 (같은 turn 안 다른 source 가 add 한 Passive 도 reuse).
                let storeHit = findSystemByName ctx.Store entry.Name
                let combinedHit =
                    storeHit |> Option.orElseWith (fun () -> Internal.PlanLookup.tryFindSystemByName ctx.Plan entry.Name)
                match combinedHit with
                | Some sys ->
                    let isPassiveInStore =
                        Queries.allProjects ctx.Store
                        |> List.exists (fun p -> p.PassiveSystemIds.Contains sys.Id)
                    let isPassiveInPlan =
                        Internal.PlanLookup.tryFindSystemLinkKind ctx.Plan sys.Id
                        |> Option.map not
                        |> Option.defaultValue false   // LinkSystemToProject 부재 = "passive 아님" 으로 fail-safe (store-only 신뢰)
                    if not (isPassiveInStore || isPassiveInPlan) then
                        ctx.Diagnostics.Add(path,
                            sprintf "system '%s' 가 store 에서 active 인데 wire 에 kind=passive 로 명시 — modeling level 은 entity kind 변경 금지. patch 사용." entry.Name)
                        None
                    else
                        // **M-2**: device mismatch 검증 — wire 의 device 키가 기존 cascade 와
                        // 다른 sugar 명시 시 진단. parseDevice 결과의 sugar literal 과 store 의
                        // SystemType 매핑 (cylinder/clamp -> "Unit", robot -> "Robot", custom -> raw)
                        // 비교. SystemType=None 인 store (비정상) 는 검증 skip.
                        deviceRaw |> Option.iter (fun raw ->
                            match parseDevice raw with
                            | Error _ -> ()  // device 키 자체 형식 오류는 본 분기 외에서 처리
                            | Ok lit ->
                                match lit with
                                | UnknownSugar bare ->
                                    // **사용자 review Minor #1**: modeling reuse 분기에서 UnknownSugar
                                    // silent skip 회피 — 사용자 오타 (`cylindar` 등) 인지 가능하도록
                                    // wire 입력 검증. reuse 자체는 store entity 정상 사용이라 entity
                                    // 의미 정합. 진단은 사용자 wire 표기 정정 유도.
                                    ctx.Diagnostics.Add(joinDiagKey path "device",
                                        sprintf "'%s' 는 sugar 미정의. device: custom(<Type>), apis: [...] long-form 사용." bare)
                                | _ ->
                                    let wireSystemType =
                                        match lit with
                                        | KnownCylinder | KnownClamp -> Some "Unit"
                                        | KnownRobot -> Some "Robot"
                                        | Custom typeName -> Some typeName
                                        | UnknownSugar _ -> None  // 위 분기에서 처리됨
                                    match wireSystemType, sys.SystemType with
                                    | Some wireT, Some storeT when wireT <> storeT ->
                                        ctx.Diagnostics.Add(joinDiagKey path "device",
                                            sprintf "modeling level 은 device 변경 금지 (store SystemType='%s', wire 매핑='%s'). patch DSL 또는 level: full 사용." storeT wireT)
                                    | _ -> ())
                        entry.SystemId := Some sys.Id
                        for apiDef in Queries.apiDefsOf sys.Id ctx.Store do
                            entry.ApiDefIds.[apiDef.Name] <- apiDef.Id
                        // #32 — plan 측 ApiDef 도 누적 (같은 turn 안 add 된 ApiDef 도 cross-system call 에서 reuse)
                        for op in ctx.Plan.Operations do
                            match op with
                            | AddApiDef d when d.ParentId = sys.Id ->
                                entry.ApiDefIds.[d.Name] <- d.Id
                            | _ -> ()
                        Some sys
                | None -> None
            | Full -> None

        if modelingReuseHit.IsNone then
            match deviceRaw with
            | None ->
                // device 키 부재 — 단순 Passive 만 생성 (SSOT §5 매핑 표 잠정 허용).
                try
                    let id = ToolOperations.queueAddPassiveSystem ctx.Plan ctx.Store entry.Name "Unit"
                    entry.SystemId := Some id
                with ex -> ctx.Diagnostics.Add(path, ex.Message)
            | Some raw ->
                match parseDevice raw with
                | Error msg -> ctx.Diagnostics.Add(joinDiagKey path "device", msg)
                | Ok lit ->
                    match lit with
                    | UnknownSugar bare ->
                        ctx.Diagnostics.Add(
                            joinDiagKey path "device",
                            sprintf "'%s' 는 sugar 미정의. device: custom(<Type>), apis: [...] long-form 사용." bare)
                    | _ ->
                        let defApis, defOpp, defDur = deviceDefaults lit
                        let apis = apisRaw |> Option.defaultValue defApis
                        let opposing = opposingRaw |> Option.defaultValue defOpp
                        let duration = workDuration |> Option.orElseWith (fun () -> Some defDur)
                        try
                            let id, apiPairs =
                                match lit with
                                | KnownCylinder ->
                                    ToolOperations.queueAddCylinder ctx.Plan ctx.Store entry.Name apis duration
                                | KnownClamp ->
                                    ToolOperations.queueAddClamp ctx.Plan ctx.Store entry.Name apis duration
                                | KnownRobot ->
                                    if apis.IsEmpty then
                                        invalidOp "robot 은 apis 명시 필수."
                                    ToolOperations.queueAddRobot ctx.Plan ctx.Store entry.Name apis opposing duration
                                | Custom typeName ->
                                    if apis.IsEmpty then
                                        invalidOp (sprintf "custom(%s) 는 apis 명시 필수." typeName)
                                    ToolOperations.queueAddDevice ctx.Plan ctx.Store entry.Name typeName apis opposing duration
                                | UnknownSugar _ -> failwith "unreachable — UnknownSugar 는 위 분기에서 처리됨"
                            entry.SystemId := Some id
                            for (apiName, apiId) in apiPairs do
                                entry.ApiDefIds.[apiName] <- apiId
                        with ex ->
                            ctx.Diagnostics.Add(path, ex.Message)

        // Phase 7 §4.2 C-6: DsSystem.IRI (Passive — leaf 키)
        // Phase 7 §4.2 C-7.1: ControlSystemProperties plc 키 (Passive System)
        match !entry.SystemId with
        | Some sysId ->
            applyNonEmptyStringProp ctx path sysEl "iri" (fun s ->
                lookupSystemById ctx sysId |> Option.iter (fun sys -> sys.IRI <- Some s))
            tryProp sysEl "plc"
            |> Option.iter (fun plcEl ->
                lookupSystemById ctx sysId
                |> Option.iter (fun sys ->
                    parsePlcSystem ctx (joinDiagKey path "plc") sys plcEl))
        | None -> ()

        // apiDetails — ApiDef 별 추가 property (Phase 7 §4.2 C-5).
        // device sugar 가 ApiDef 를 생성한 분기 (cylinder/clamp/robot/custom) 통과 후 적용.
        // *device 키 부재 시 ApiDef 미생성* — apiDetails entry 가 모두 entry.ApiDefIds lookup 에 실패하여
        // forensic diag 발생. SSOT §2.3 가 `apiDetails` 를 device sugar Passive 한정으로 명시 (외부 reviewer M-C).
        tryProp sysEl "apiDetails"
        |> Option.iter (fun detailsEl ->
            if detailsEl.ValueKind <> JsonValueKind.Object then
                ctx.Diagnostics.Add(joinDiagKey path "apiDetails", sprintf "object 기대 (실제 %A)." detailsEl.ValueKind)
            else
                for prop in detailsEl.EnumerateObject() do
                    let apiName = prop.Name
                    let apiPath = sprintf "%s.apiDetails.%s" path apiName
                    match entry.ApiDefIds.TryGetValue apiName with
                    | true, apiId ->
                        match lookupApiDefById ctx apiId with
                        | Some apiDef ->
                            applyEnumProp ctx apiPath prop.Value "actionType" parseActionType (fun at -> apiDef.ActionType <- at)
                            applyEnumProp ctx apiPath prop.Value "sensingType" parseSensingType (fun st -> apiDef.SensingType <- st)
                            // 외부 reviewer m-4 반영: 빈 string 도 set 하면 store 표면값 (`Some ""`) 이 emit 측 default-skip
                            // 정책 (Some 이고 빈 아닐 때만 emit) 과 비대칭. apply 측에서도 빈 string → None 으로 정규화.
                            tryProp prop.Value "description"
                            |> Option.bind tryString
                            |> Option.filter (fun s -> not (String.IsNullOrEmpty s))
                            |> Option.iter (fun s -> apiDef.Description <- Some s)
                        | None ->
                            ctx.Diagnostics.Add(apiPath, "ApiDef instance 추적 실패 (forensic).")
                    | false, _ ->
                        ctx.Diagnostics.Add(apiPath, sprintf "ApiDef '%s' 가 system 의 apis 목록에 없음." apiName))

    let private dispatchActiveSystem
        (ctx: ApplyContext)
        (entry: SystemEntry)
        (sysEl: JsonElement)
        (path: string) : unit =

        // M1 fix: active system 이름 sanitize 가드.
        if not (tryValidateName ctx (path + ".system") "System name" entry.Name) then () else

        try
            // Lookup-first — modeling/Full 양 level 통합 (사용자 요청 rev2):
            // 기존 store/plan 에 같은 이름 Active 발견 시 silent reuse — queueAddActiveSystem 호출 skip.
            // 다른 이름 active 가 같은 project 에 이미 있는데 wire 가 또 다른 active 추가 시도는 queueAddActiveSystem 단의
            // PLC 1 controller guard 가 fail 처리. wire 의 iri / plc 키는 modeling level walk (S3a) 가 사전 거부.
            let id =
                let storeHit = findSystemByName ctx.Store entry.Name
                let combinedHit =
                    storeHit |> Option.orElseWith (fun () -> Internal.PlanLookup.tryFindSystemByName ctx.Plan entry.Name)
                match combinedHit with
                | Some sys ->
                    // 기존 active 인지 검증 — Passive 인데 wire 가 active 로 명시면 의미 충돌. silent reuse 거부.
                    let isActiveInStore =
                        Queries.allProjects ctx.Store
                        |> List.exists (fun p -> p.ActiveSystemIds.Contains sys.Id)
                    let isActiveInPlan =
                        Internal.PlanLookup.tryFindSystemLinkKind ctx.Plan sys.Id
                        |> Option.defaultValue false   // LinkSystemToProject 부재 = "active 아님" 으로 fail-safe (store-only 신뢰)
                    if not (isActiveInStore || isActiveInPlan) then
                        invalidOp (sprintf "system '%s' 가 store 에서 passive 인데 wire 에 kind=active 로 명시 — entity kind 변경 금지 (silent reuse 불가). patch 사용 또는 다른 이름 사용." entry.Name)
                    sys.Id
                | None -> ToolOperations.queueAddActiveSystem ctx.Plan ctx.Store entry.Name
            entry.SystemId := Some id
            // Phase 7 §4.2 C-6: DsSystem.IRI — Active / Passive 공통 leaf 키
            applyNonEmptyStringProp ctx path sysEl "iri" (fun s ->
                lookupSystemById ctx id |> Option.iter (fun sys -> sys.IRI <- Some s))
            // Phase 7 §4.2 C-7.1: ControlSystemProperties plc 키 (Active System)
            tryProp sysEl "plc"
            |> Option.iter (fun plcEl ->
                lookupSystemById ctx id
                |> Option.iter (fun sys ->
                    parsePlcSystem ctx (joinDiagKey path "plc") sys plcEl))
        with ex ->
            ctx.Diagnostics.Add(path, ex.Message)

    let private buildSystems (ctx: ApplyContext) (systemsEl: JsonElement) : unit =
        if systemsEl.ValueKind <> JsonValueKind.Array then ()
        else
            let mutable idx = 0
            for sysEl in systemsEl.EnumerateArray() do
                let path = sprintf "systems[%d]" idx
                match tryProp sysEl "system" |> Option.bind tryString with
                | None -> ()
                | Some name ->
                    match ctx.Systems.TryGetValue name with
                    | false, _ -> ()
                    | true, entry ->
                        match entry.Kind with
                        | "active" -> dispatchActiveSystem ctx entry sysEl path
                        | "passive" -> dispatchPassiveSystem ctx entry sysEl path
                        | _ -> ()
                idx <- idx + 1

    // ─── Pass 3 — Active Flow / Work / Call / Arrow ─────────────────────────

    /// dotted-path 로 ApiDef 찾기. `Sys.API` (cross-system) 또는 bare `API` (current passive).
    /// ctx.Systems 에서 system name → ApiDefIds 조회.
    let private resolveApiDef
        (ctx: ApplyContext)
        (rawRef: string)
        (path: string) : Guid option =
        let segments = pathSegments rawRef
        match segments with
        | [ sysName; apiName ] ->
            match ctx.Systems.TryGetValue sysName with
            | false, _ ->
                let candidates = nearestCandidates sysName ctx.Systems.Keys 3
                ctx.Diagnostics.Add(
                    path,
                    sprintf "'%s' 시스템이 발견되지 않음." sysName,
                    suggestion = (if candidates.IsEmpty then "" else String.Join(" / ", candidates)))
                None
            | true, sysEntry ->
                match sysEntry.ApiDefIds.TryGetValue apiName with
                | true, id -> Some id
                | false, _ ->
                    let candidates = nearestCandidates apiName sysEntry.ApiDefIds.Keys 3
                    ctx.Diagnostics.Add(
                        path,
                        sprintf "'%s.%s' 의 ApiDef '%s' 가 발견되지 않음." sysName apiName apiName,
                        ?suggestion = (if candidates.IsEmpty then None else Some (String.Join(" / ", candidates))))
                    None
        | _ ->
            ctx.Diagnostics.Add(
                path,
                sprintf "'%s' 형식 위반. '<System>.<ApiDef>' 형식 필요." rawRef)
            None

    // ─── Condition leaf `eq` / `inputSpec` (Phase 2 — ValueSpec sugar) ───────
    //
    // SSOT done-refactor-condition.md Phase 2 / 박제 결정:
    //   * leaf object `{ ref, contactKind?, eq?, inputSpec? }` — `eq` 는 단일 equality sugar,
    //     `inputSpec` 은 Multiple/Ranges/명시 타입 fallback (raw ValueSpec DU).
    //   * `eq` 값의 ValueSpec case 는 JSON token 만으로 추론하지 않고 *대상 ApiDef 의 데이터 타입
    //     metadata* 기준으로 결정한다. ApiDef entity 자체에는 타입 metadata 가 없으므로 (Entities.fs
    //     ApiDef = ActionType/SensingType/Tx/Rx/Description), 해당 ApiDefId 를 참조하는 store/plan
    //     ApiCall 의 InputSpec(우선)/OutputSpec ValueSpec case 를 metadata 출처로 삼는다.
    //   * 숫자 token 은 정수/실수 폭을 token 만으로 확정할 수 없으므로, hint 부재 시 임의 고정 대신
    //     diagnostics 로 거부하고 typed `inputSpec` 사용을 안내한다 (박제 결정 — Int32/Float64 임의 고정 금지).

    /// raw ValueSpec DU (`{"Case":"BoolValue","Fields":[...]}`) deserialize 옵션 (STJ + FSharp DU 컨버터).
    let private valueSpecJsonOptions = JsonOptions.createProjectSerializationOptions ()

    /// 주어진 ApiDefId 를 참조하는 ApiCall 의 InputSpec(우선)/OutputSpec 중 non-Undefined ValueSpec 의
    /// *타입 hint* (case 만 유지한 default). InputSpec/OutputSpec 모두 Undefined 면 None.
    let private apiCallTypeHint (ac: ApiCall) : ValueSpec option =
        match ac.InputSpec with
        | UndefinedValue ->
            match ac.OutputSpec with
            | UndefinedValue -> None
            | other -> Some (ValueSpecText.typeHintOf other)
        | other -> Some (ValueSpecText.typeHintOf other)

    /// 주어진 ApiDefId 를 참조하는 *store* ApiCall 의 타입 hint. apply(parse) 측 hint 의 store 부분.
    /// device sugar(cylinder 등)의 ApiCall 은 InputSpec=UndefinedValue 라 대부분 None.
    let private apiDefValueSpecHintFromStore (store: DsStore) (apiDefId: Guid) : ValueSpec option =
        store.CallsReadOnly.Values
        |> Seq.collect (fun c -> c.ApiCalls :> seq<_>)
        |> Seq.filter (fun ac -> ac.ApiDefId = Some apiDefId)
        |> Seq.tryPick apiCallTypeHint

    /// 주어진 ApiDefId 를 참조하는 store/plan ApiCall 의 InputSpec(우선)/OutputSpec 중
    /// non-Undefined ValueSpec 의 *타입 hint*. store 우선, 없으면 이번 turn plan 신규 Call 보강.
    /// 없으면 None — 이 경우 숫자 eq 는 diagnostics 대상.
    let private apiDefValueSpecHint (ctx: ApplyContext) (apiDefId: Guid) : ValueSpec option =
        match apiDefValueSpecHintFromStore ctx.Store apiDefId with
        | Some _ as fromStore -> fromStore
        | None ->
            // 이번 turn plan operations 의 신규 Call.ApiCalls 도 metadata 출처.
            ctx.Plan.Operations
            |> Seq.choose (function ImportPlanOperation.AddCall c -> Some c | _ -> None)
            |> Seq.collect (fun c -> c.ApiCalls :> seq<_>)
            |> Seq.filter (fun ac -> ac.ApiDefId = Some apiDefId)
            |> Seq.tryPick apiCallTypeHint

    /// `eq` JsonElement → ValueSpec. 대상 ApiDef 타입 hint 기준으로 case 결정.
    /// - bool token: hint 무관 `BoolValue (Single b)` (token 자체로 타입 확정).
    /// - string token: hint 가 비-string 타입이면 그 타입으로 텍스트 파싱, 아니면 `StringValue (Single s)`.
    /// - number token: hint(숫자 case) 필수. hint 없으면 None + diagnostics (숫자 폭 임의 고정 금지).
    let private parseEqValue
        (ctx: ApplyContext) (hint: ValueSpec option) (eqEl: JsonElement) (path: string) : ValueSpec option =
        match eqEl.ValueKind with
        | JsonValueKind.True  -> Some (ValueSpec.singleBool true)
        | JsonValueKind.False -> Some (ValueSpec.singleBool false)
        | JsonValueKind.String ->
            let s = eqEl.GetString()
            match hint with
            | Some h when not (h = ValueSpec.singleString "") ->
                // hint 가 string 이외 타입 — 그 타입으로 텍스트 파싱 (실패 시 diagnostics).
                // strict: tryParseAs 의 inferFromText fallback 이 hint 와 *다른* case 로 파싱한 결과
                // (예: 범위 초과·타입 불일치) 가 eq 로 살아나는 것을 차단 — case 일치까지 확인.
                match ValueSpecText.tryParseAs h s with
                | Some spec when ValueSpecText.isSingleEquality spec && ValueSpecText.sameCase spec h -> Some spec
                | _ ->
                    ctx.Diagnostics.Add(path, sprintf "eq 값 '%s' 을(를) 대상 타입으로 해석할 수 없습니다." s)
                    None
            | _ -> Some (ValueSpec.singleString s)
        | JsonValueKind.Number ->
            match hint with
            | Some h ->
                // 대상 ApiDef 의 숫자 타입 case 로 token 텍스트를 파싱 — 폭 보존.
                // strict: tryParseAs 의 inferFromText fallback (UInt8 hint + "300" → Int64 등) 이
                // hint 와 다른 case 로 살아나는 것을 차단 — case 일치까지 확인.
                let raw = eqEl.GetRawText()
                match ValueSpecText.tryParseAs h raw with
                | Some spec when ValueSpecText.isSingleEquality spec && ValueSpecText.sameCase spec h -> Some spec
                | _ ->
                    ctx.Diagnostics.Add(path, sprintf "eq 숫자 값 '%s' 을(를) 대상 타입으로 해석할 수 없습니다." raw)
                    None
            | None ->
                ctx.Diagnostics.Add(
                    path,
                    "eq 숫자 값의 정수/실수 타입을 결정할 수 없습니다 (대상 ApiDef 의 InputSpec 타입 metadata 부재).",
                    suggestion = "typed inputSpec 포맷 사용 — 예: inputSpec: { Case: Int32Value, Fields: [ { Case: Single, Fields: [ 5 ] } ] }.")
                None
        | other ->
            ctx.Diagnostics.Add(path, sprintf "eq 는 bool/숫자/문자열 scalar 기대 (실제 %A)." other)
            None

    /// typed `inputSpec` fallback — raw ValueSpec DU 를 STJ 로 deserialize.
    /// Multiple / Ranges / 명시 타입 비교 조건을 표현 (eq 로 환원 불가한 케이스, 박제 결정).
    let private parseTypedInputSpec (ctx: ApplyContext) (specEl: JsonElement) (path: string) : ValueSpec option =
        // 외부 입력(잘못된 DU shape) 파싱 실패는 "예상되는 예외" — silent skip 금지 정책상 diagnostics 로 보고.
        try
            Some (JsonSerializer.Deserialize<ValueSpec>(specEl.GetRawText(), valueSpecJsonOptions))
        with ex ->
            ctx.Diagnostics.Add(path, sprintf "inputSpec 파싱 실패 (raw ValueSpec DU 기대): %s" ex.Message)
            None

    // ─── Condition / ContactKind apply helpers (Phase 7 §4.2 C-3) ────────
    //
    // SSOT yaml-protocol-v0.md §2.2.1 dual format — object 형태 call 의 보강 property.
    // `tryFindCallInPlan` / `tryFindApiDefInPlan` / `callTypeOf` / `setCallType` 4 helper 는 본 dispatch
    // helper (dispatchPassiveSystem / dispatchWork) 보다 *앞* 에서 호출되어야 하므로 ApplyContext 정의 직후 (위)
    // 로 이동됨. 본 영역에는 resolveApiDef 의존 helper (parseCondition) 와 ApplyContext 의존 helper (parseIOTag) 만 유지.

    /// IOTag parse — emit 측 `writeIOTag` 의 거울 (Phase 7 §4.2 C-4 PoC scope: Name + Address).
    /// 두 키 모두 부재 시 None — 빈 IOTag instance 생성 회피.
    let private parseIOTag (ctx: ApplyContext) (el: JsonElement) (path: string) : IOTag option =
        if el.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "IOTag object 기대 (실제 %A)." el.ValueKind)
            None
        else
            let nameOpt = tryProp el "name" |> Option.bind tryString
            let addressOpt = tryProp el "address" |> Option.bind tryString
            match nameOpt, addressOpt with
            | None, None -> None
            | _ ->
                let tag = IOTag()
                nameOpt |> Option.iter (fun s -> tag.Name <- s)
                addressOpt |> Option.iter (fun s -> tag.Address <- s)
                Some tag

    /// condition object 의 owning entity context (Phase 1 §남은작업 1 — call/work 분리).
    /// type 생략 보정 정책과 허용 top-level type 이 context 별로 다름:
    /// - Call: top-level type 생략 → `Some AutoAux` 보정. AutoAux/ComAux/SkipAction 모두 허용.
    /// - Work: top-level type 생략 → `Some SkipAction` 보정 (Call AutoAux 보정과 대칭 — Type=None root 의
    ///   Runtime inert 무시 경로 차단, 박제 결정 7번). top-level AutoAux/ComAux 는 fail diagnostics — Work 는 SkipAction 만 의미.
    type private ConditionContext =
        | CallCondition
        | WorkCondition

    /// condition object 의 legacy 허용 키 whitelist (Phase 1 §남은작업 3).
    /// op/items 신설은 이번 범위 밖 — 여기 없는 키 (callCondition / op / items / autoAux 등) 는
    /// silent skip 하지 않고 diagnostics 로 보고한다 (SSOT §2.7 unknown-key 거부 정합).
    let private conditionAllowedKeys =
        Set.ofList [ "type"; "isOR"; "isInverted"; "conditions"; "children" ]

    /// condition leaf object 의 허용 키 whitelist (Phase 2 — eq/inputSpec sugar 추가).
    /// `eq` = 단일 equality sugar, `inputSpec` = Multiple/Ranges/명시 타입 typed fallback.
    /// 여기 없는 키 (op/items/value 등) 는 unknown-key diagnostics 대상.
    let private conditionLeafAllowedKeys =
        Set.ofList [ "ref"; "contactKind"; "eq"; "inputSpec" ]

    /// calls[] element object 의 허용 키 whitelist (SSOT yaml-protocol-v0.md §2.7 룰 #14).
    /// canonical condition 키는 `condition` — `callCondition` alias parse 추가 금지 (박제 결정) → 여기 없으므로
    /// callCondition 입력은 unknown-key diagnostics 대상. v10 에서 skipInputSensor 폐기.
    let private callObjectAllowedKeys =
        Set.ofList [ "ref"; "contactKind"; "inTag"; "outTag"; "callType"; "condition"; "plc" ]

    /// Condition tree (Type / IsOR / IsInverted / Conditions / Children) recursive parse.
    /// conditions leaf 는 ApiCall — dual format (string scalar 또는 object{ref, contactKind?}).
    /// PoC scope 가정: leaf 의 ApiCall 은 ApiDefId + ContactKind 만 set (IO tag binding 은 C-4 phase).
    ///
    /// `context` = owning entity (Call/Work). `topLevel` = root condition 여부 (children 재귀는 false).
    /// type 생략 보정 (Some AutoAux) 은 *top-level Call* 에만 적용 — emit 이 AutoAux type 키를 생략하므로
    /// 보정 없으면 export → apply round-trip 후 Type=None 이 되어 Runtime CallAutoAuxConditions 평가에서 누락됨.
    let rec private parseCondition (ctx: ApplyContext) (context: ConditionContext) (topLevel: bool) (condEl: JsonElement) (path: string) : Condition option =
        if condEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "condition object 기대 (실제 %A)." condEl.ValueKind)
            None
        elif condEl.EnumerateObject() |> Seq.isEmpty then
            // 외부 reviewer m-5 반영: 빈 `condition: {}` 는 의미 0 의 Condition 인스턴스 추가 회피 → None 으로 정규화.
            None
        else
            // unknown-key whitelist (Phase 1 §남은작업 3) — legacy 허용 키 외 entry 는 diagnostics.
            // callCondition alias 입력도 여기서 거부 (박제 결정: condition 만 canonical, callCondition alias parse 추가 금지).
            for prop in condEl.EnumerateObject() do
                if not (Set.contains prop.Name conditionAllowedKeys) then
                    ctx.Diagnostics.Add(
                        joinDiagKey path prop.Name,
                        sprintf "알 수 없는 condition 키 '%s'. 허용: type|isOR|isInverted|conditions|children." prop.Name)
            let cond = Condition()
            // type 보정 정책 (Phase 1 §남은작업 1):
            //   - 명시된 type 은 항상 보존 (legacy child explicit type 보존 포함).
            //   - Work top-level 에서 AutoAux/ComAux 는 fail diagnostics (박제 결정 — Work 는 SkipAction 만 허용).
            //   - Call top-level 에서 type 생략 → Some AutoAux 보정 (round-trip 버그 수정).
            //   - Work top-level 에서 type 생략 → Some SkipAction 보정 (Call 과 대칭 — Type=None root inert 무시 차단).
            //   - child (topLevel=false) 의 type 생략 → None 유지 (legacy 호환).
            match tryProp condEl "type" |> Option.bind tryString with
            | Some s ->
                match parseConditionType s with
                | Ok t ->
                    match context, topLevel, t with
                    | WorkCondition, true, ConditionType.AutoAux
                    | WorkCondition, true, ConditionType.ComAux ->
                        ctx.Diagnostics.Add(
                            joinDiagKey path "type",
                            sprintf "Work condition 은 SkipAction 만 허용. top-level '%s' 는 무효 (Runtime 에서 평가되지 않음)." s)
                    | _ -> cond.Type <- Some t
                | Error msg -> ctx.Diagnostics.Add(joinDiagKey path "type", msg)
            | None ->
                // type 키 부재 — emit 이 default type 을 생략하므로 top-level 은 context 별 default 로 보정.
                // (박제 결정 7번 "의미 없는 조건 저장·무시 차단" — Type=None top-level root 는 Runtime
                //  (Build.fs) 의 `Type = Some _` 필터에 안 걸려 inert → 무시 경로 차단.)
                match context, topLevel with
                | CallCondition, true -> cond.Type <- Some ConditionType.AutoAux
                | WorkCondition, true -> cond.Type <- Some ConditionType.SkipAction  // Work 는 SkipAction 만 의미 (Call AutoAux 보정과 대칭).
                | _ -> ()  // child (topLevel=false) — None 유지 (legacy 호환).
            // isOR / isInverted — bool false default
            let parseBoolKey key target =
                tryProp condEl key
                |> Option.iter (fun el ->
                    match el.ValueKind with
                    | JsonValueKind.True -> target true
                    | JsonValueKind.False -> target false
                    | _ -> ctx.Diagnostics.Add(joinDiagKey path key, sprintf "bool 기대 (실제 %A)." el.ValueKind))
            parseBoolKey "isOR" (fun b -> cond.IsOR <- b)
            parseBoolKey "isInverted" (fun b -> cond.IsInverted <- b)
            // conditions — ApiCall leaf list (dual format)
            tryProp condEl "conditions"
            |> Option.iter (fun condsEl ->
                if condsEl.ValueKind <> JsonValueKind.Array then
                    // SSOT §2.7 룰 #16 — silent skip 금지 (외부 reviewer M-F)
                    ctx.Diagnostics.Add(joinDiagKey path "conditions", sprintf "array 기대 (실제 %A). leaf ApiCall list 또는 nested Condition list 형식 사용." condsEl.ValueKind)
                else
                    let mutable idx = 0
                    for leafEl in condsEl.EnumerateArray() do
                        let leafPath = sprintf "%s.conditions[%d]" path idx
                        let apiCall = ApiCall("")
                        let mutable validLeaf = false
                        match leafEl.ValueKind with
                        | JsonValueKind.String ->
                            let refStr = leafEl.GetString()
                            match resolveApiDef ctx refStr leafPath with
                            | Some apiDefId ->
                                apiCall.ApiDefId <- Some apiDefId
                                validLeaf <- true
                            | None -> ()
                        | JsonValueKind.Object ->
                            // leaf object 허용 키 whitelist (Phase 2) — unknown 키는 silent skip 금지.
                            for prop in leafEl.EnumerateObject() do
                                if not (Set.contains prop.Name conditionLeafAllowedKeys) then
                                    ctx.Diagnostics.Add(
                                        joinDiagKey leafPath prop.Name,
                                        sprintf "알 수 없는 condition leaf 키 '%s'. 허용: ref|contactKind|eq|inputSpec." prop.Name)
                            (match tryProp leafEl "ref" |> Option.bind tryString with
                             | Some refStr ->
                                 match resolveApiDef ctx refStr (leafPath + ".ref") with
                                 | Some apiDefId ->
                                     apiCall.ApiDefId <- Some apiDefId
                                     validLeaf <- true
                                 | None -> ()
                             | None ->
                                 ctx.Diagnostics.Add(leafPath, "object element 는 'ref' 키 필수."))
                            tryProp leafEl "contactKind"
                            |> Option.bind tryString
                            |> Option.iter (fun s ->
                                match parseContactKind s with
                                | Ok k -> apiCall.ContactKind <- k
                                | Error msg -> ctx.Diagnostics.Add(joinDiagKey leafPath "contactKind", msg))
                            // eq / inputSpec (Phase 2) — 기대값 sugar. 동시 지정은 혼용 금지 diagnostics.
                            let hasEq = tryProp leafEl "eq" |> Option.isSome
                            let hasInputSpec = tryProp leafEl "inputSpec" |> Option.isSome
                            if hasEq && hasInputSpec then
                                ctx.Diagnostics.Add(leafPath, "eq 와 inputSpec 은 동시 지정 불가 (eq = 단일 equality sugar, inputSpec = typed fallback). 하나만 사용.")
                            else
                                // eq: 대상 ApiDef 타입 hint 기준 ValueSpec 결정.
                                tryProp leafEl "eq"
                                |> Option.iter (fun eqEl ->
                                    let hint = apiCall.ApiDefId |> Option.bind (apiDefValueSpecHint ctx)
                                    parseEqValue ctx hint eqEl (joinDiagKey leafPath "eq")
                                    |> Option.iter (fun spec -> apiCall.InputSpec <- spec))
                                // inputSpec: raw ValueSpec DU fallback (Multiple/Ranges/명시 타입).
                                tryProp leafEl "inputSpec"
                                |> Option.iter (fun specEl ->
                                    parseTypedInputSpec ctx specEl (joinDiagKey leafPath "inputSpec")
                                    |> Option.iter (fun spec -> apiCall.InputSpec <- spec))
                        | _ ->
                            ctx.Diagnostics.Add(leafPath, sprintf "string 또는 object 기대 (실제 %A)." leafEl.ValueKind)
                        if validLeaf then cond.ApiCalls.Add(apiCall)
                        idx <- idx + 1)
            // children — nested Condition list (recursive)
            tryProp condEl "children"
            |> Option.iter (fun chEl ->
                if chEl.ValueKind <> JsonValueKind.Array then
                    // SSOT §2.7 룰 #16 — silent skip 금지 (외부 reviewer M-F)
                    ctx.Diagnostics.Add(joinDiagKey path "children", sprintf "array 기대 (실제 %A). nested Condition list 형식 사용." chEl.ValueKind)
                else
                    let mutable idx = 0
                    for childEl in chEl.EnumerateArray() do
                        let childPath = sprintf "%s.children[%d]" path idx
                        // child 는 topLevel=false — type 생략 보정 없음 (legacy 호환), explicit type 은 보존.
                        match parseCondition ctx context false childEl childPath with
                        | Some child -> cond.Children.Add(child)
                        | None -> ()
                        idx <- idx + 1)
            Some cond

    // flowName 인자는 더 이상 불필요 — WorkIdsByName 평탄 dict 가 flow scope 무관 (work 이름 system-unique).
    let private dispatchWork
        (ctx: ApplyContext)
        (sysEntry: SystemEntry)
        (flowId: Guid)
        (workLocalName: string)
        (workEl: JsonElement)
        (path: string) : unit =

        try
            // M1 fix: work localName sanitize 가드.
            if not (tryValidateName ctx path "Work localName" workLocalName) then () else
            // S3b — modeling level 시 lookup-first (기존 store/plan 의 같은 (flowId, workLocalName) Work reuse).
            // #32 — store + plan 합집합. plan-only Work 도 reuse. durationOpt 무관 → 가드 전에 산출.
            let reuseId =
                match !ctx.Level with
                | Modeling ->
                    Queries.worksOf flowId ctx.Store
                    |> List.tryFind (fun w -> w.LocalName = workLocalName)
                    |> Option.orElseWith (fun () -> Internal.PlanLookup.tryFindWorkByLocalName ctx.Plan flowId workLocalName)
                    |> Option.map (fun w -> w.Id)
                | Full -> None
            // D1 — work 이름 system-unique. **새 work 생성** 시 이름이 system 에 이미 존재하면 충돌 (silent 덮어쓰기 금지).
            // modeling reuse (기존 entity 재사용) 는 동일 entity 라 충돌 아님 — seedStoreSystems 가 채운 기존 work 의
            // patch-merge reuse 를 가드가 잘못 차단하던 회귀(C-2) 차단. cross-flow 동명/wire dup 의 신규 생성만 fail-fast.
            if reuseId.IsNone && sysEntry.WorkIdsByName.ContainsKey workLocalName then
                ctx.Diagnostics.Add(path, sprintf "Work 이름 '%s' 가 system '%s' 안에서 중복 정의되었습니다 (work 이름은 system-unique — D1)." workLocalName sysEntry.Name) else
            // M3 fix: workDuration 을 queueAddWork 호출 *전* 에 파싱해서 옵션 인자로 전달.
            // 후행 mutation (plan.Operations 재검색 + w.Duration <- ts) 제거 — Operations immutable invariant 보존.
            // wire 의 workDuration 키는 modeling walk (S3a) 가 사전 거부 — modeling 시 durationOpt 는 None 보장.
            let durationOpt =
                tryProp workEl "workDuration" |> Option.bind tryString
                |> Option.bind (fun s ->
                    match parseDuration s with
                    | Ok ts -> Some ts
                    | Error msg ->
                        ctx.Diagnostics.Add(joinDiagKey path "workDuration", msg)
                        None)
            let workId =
                match reuseId with
                | Some id -> id
                | None -> ToolOperations.queueAddWork ctx.Plan ctx.Store workLocalName flowId durationOpt
            // WorkIdsByName 누적 — work 이름 → Guid (system 전역, cross-flow arrow resolve 용).
            sysEntry.WorkIdsByName.[workLocalName] <- workId

            if tryProp workEl "duration" |> Option.isSome then
                ctx.Diagnostics.Add(joinDiagKey path "duration", "키 폐기됨. 'workDuration' 으로 변경하세요.")

            // Phase 7 §4.2 C-6: Work.TokenRole (단순 leaf, default None 면 키 부재)
            applyEnumProp ctx path workEl "tokenRole" parseTokenRole (fun r ->
                lookupWorkById ctx workId |> Option.iter (fun work -> work.TokenRole <- r))

            // Phase 7 §4.2 C-7.1: ControlWorkProperties plc 키
            tryProp workEl "plc"
            |> Option.iter (fun plcEl ->
                lookupWorkById ctx workId
                |> Option.iter (fun work ->
                    parsePlcWork ctx (joinDiagKey path "plc") work plcEl))

            // Condition tree (Work) — WorkCondition context (top-level AutoAux/ComAux 는 fail diagnostics).
            tryProp workEl "condition"
            |> Option.iter (fun ccEl ->
                match parseCondition ctx WorkCondition true ccEl (path + ".condition") with
                | Some cc ->
                    lookupWorkById ctx workId
                    |> Option.iter (fun work -> work.Conditions.Add(cc))
                | None -> ())

            // calls 처리 — dual format (Phase 7 §4.1.5 옵션 C):
            //   string scalar       → default case, callObjOpt = None
            //   object { ref, ... } → non-default case, callObjOpt = Some <full object>
            let callsList : (string * JsonElement option) list =
                tryProp workEl "calls"
                |> Option.bind (fun el ->
                    if el.ValueKind = JsonValueKind.Array then
                        el.EnumerateArray()
                        |> Seq.indexed
                        |> Seq.choose (fun (idx, callEl) ->
                            let callPath = sprintf "%s.calls[%d]" path idx
                            match callEl.ValueKind with
                            | JsonValueKind.String -> Some (callEl.GetString(), None)
                            | JsonValueKind.Object ->
                                match tryProp callEl "ref" |> Option.bind tryString with
                                | Some s -> Some (s, Some callEl)
                                | None ->
                                    ctx.Diagnostics.Add(callPath, "object element 는 'ref' 키 필수.")
                                    None
                            | _ ->
                                ctx.Diagnostics.Add(callPath, sprintf "string 또는 object 기대 (실제 %A)." callEl.ValueKind)
                                None)
                        |> Seq.toList
                        |> Some
                    else None)
                |> Option.defaultValue []

            // arrows (Work 안 ArrowBetweenCalls) 존재 여부 — concurrent vs sequential 분기.
            // YAML 자연 형태 (`- A -> B : T` → mapping `{A -> B: T}`) 와 quoted string 양쪽 지원.
            let workArrowsList =
                tryProp workEl "arrows"
                |> Option.bind (fun el ->
                    if el.ValueKind = JsonValueKind.Array then
                        el.EnumerateArray()
                        |> Seq.map extractArrowString
                        |> Seq.toList
                        |> Some
                    else None)
                |> Option.defaultValue []
            // 중복 ApiDef Call 검출
            let callCounts = Dictionary<string, int>(StringComparer.Ordinal)
            for (callRef, _) in callsList do
                let normalized = normalizePath callRef
                callCounts.[normalized] <- (if callCounts.ContainsKey normalized then callCounts.[normalized] + 1 else 1)
            let hasDup = callCounts.Values |> Seq.exists (fun n -> n > 1)
            // review C3: 사용자 의도 판정은 *arrows 키 자체의 존재 여부* 로 — parse 성공한 entry 만 보면
            // 모두 parse error 인 경우 (사용자는 sequential 의도였음) 가 concurrent path 로 silent 분기됨.
            // parse error 는 별도 diagnostic 으로 누적 (extractArrowString / parseArrowSpec 호출처).
            let useAllowDup = hasDup && workArrowsList.IsEmpty

            // calls 추가 — call name → callId 매핑 (arrows 의 source/target 식별용)
            let callIdMap = Dictionary<string, ResizeArray<Guid>>(StringComparer.Ordinal)
            let mutable callIdx = 0
            for (callRef, callObjOpt) in callsList do
                let callPath = sprintf "%s.calls[%d]" path callIdx
                match resolveApiDef ctx callRef callPath with
                | None -> ()
                | Some apiDefId ->
                    try
                        // S3b — modeling level 시 lookup-first. 같은 (workId, apiDefId) Call 발견 시 reuse.
                        // #32 — store + plan 합집합. plan-only Call 도 reuse.
                        // 보강 property (contactKind / condition / callType 등) mutate 는 그대로 진행 (lookupCallById 가 store + plan 양쪽 lookup).
                        let callId =
                            match !ctx.Level with
                            | Modeling ->
                                let storeHit =
                                    Queries.callsOf workId ctx.Store
                                    |> List.tryFind (fun c ->
                                        c.ApiCalls.Count > 0 && c.ApiCalls.[0].ApiDefId = Some apiDefId)
                                let combinedHit =
                                    storeHit
                                    |> Option.orElseWith (fun () -> Internal.PlanLookup.tryFindCallByApiDef ctx.Plan workId apiDefId)
                                match combinedHit with
                                | Some c -> c.Id
                                | None ->
                                    if useAllowDup then
                                        ToolOperations.queueAddCallAllowDup ctx.Plan ctx.Store workId apiDefId
                                    else
                                        ToolOperations.queueAddCall ctx.Plan ctx.Store workId apiDefId
                            | Full ->
                                if useAllowDup then
                                    ToolOperations.queueAddCallAllowDup ctx.Plan ctx.Store workId apiDefId
                                else
                                    ToolOperations.queueAddCall ctx.Plan ctx.Store workId apiDefId
                        let normalized = normalizePath callRef
                        if not (callIdMap.ContainsKey normalized) then
                            callIdMap.[normalized] <- ResizeArray()
                        callIdMap.[normalized].Add(callId)
                        // 보강 property apply (Phase 7 §4.2 C-3) — object 형태일 때만.
                        // ContactKind: queueAddCall 가 1:1 invariant 로 call.ApiCalls[0] 생성 → 직접 set.
                        // Condition: recursive parse 후 call.Conditions 에 추가.
                        callObjOpt |> Option.iter (fun obj ->
                            // S3b — modeling reuse 시 Call 이 store 에 이미 있으므로 store + plan 양쪽 lookup.
                            // Full path 에서는 plan 측 lookup 이 우선 — `lookupCallById` 가 plan 우선 동작.
                            match lookupCallById ctx callId with
                            | None ->
                                ctx.Diagnostics.Add(callPath, "Call instance 추적 실패 (forensic).")
                            | Some call ->
                                // calls object unknown-key whitelist (SSOT §2.7 룰 #14) — callCondition alias 입력 거부 포함.
                                for prop in obj.EnumerateObject() do
                                    if not (Set.contains prop.Name callObjectAllowedKeys) then
                                        ctx.Diagnostics.Add(
                                            joinDiagKey callPath prop.Name,
                                            sprintf "알 수 없는 calls 키 '%s'. 허용: ref|contactKind|inTag|outTag|callType|condition|plc." prop.Name)
                                // ApiCall 보강 (C-3 ContactKind + InTag / OutTag). 1:1 invariant.
                                // v10: skipInputSensor 폐기 — SensingType=Virtual 로 ApiDef 차원에서 표현.
                                let firstApiCallOpt =
                                    if call.ApiCalls.Count > 0 then Some call.ApiCalls.[0] else None
                                tryProp obj "contactKind"
                                |> Option.bind tryString
                                |> Option.iter (fun s ->
                                    match parseContactKind s with
                                    | Ok k -> firstApiCallOpt |> Option.iter (fun ac -> ac.ContactKind <- k)
                                    | Error msg -> ctx.Diagnostics.Add(joinDiagKey callPath "contactKind", msg))
                                tryProp obj "inTag"
                                |> Option.iter (fun el ->
                                    match parseIOTag ctx el (callPath + ".inTag") with
                                    | Some tag -> firstApiCallOpt |> Option.iter (fun ac -> ac.InTag <- Some tag)
                                    | None -> ())
                                tryProp obj "outTag"
                                |> Option.iter (fun el ->
                                    match parseIOTag ctx el (callPath + ".outTag") with
                                    | Some tag -> firstApiCallOpt |> Option.iter (fun ac -> ac.OutTag <- Some tag)
                                    | None -> ())
                                // C-5: SimulationCallProperties.CallType (Call.Properties 콜렉션 mutation)
                                tryProp obj "callType"
                                |> Option.bind tryString
                                |> Option.iter (fun s ->
                                    match parseCallType s with
                                    | Ok ct -> setCallType call ct
                                    | Error msg -> ctx.Diagnostics.Add(joinDiagKey callPath "callType", msg))
                                // Condition tree (C-3) — CallCondition context (top-level type 생략 → Some AutoAux 보정).
                                tryProp obj "condition"
                                |> Option.iter (fun ccEl ->
                                    match parseCondition ctx CallCondition true ccEl (callPath + ".condition") with
                                    | Some cc -> call.Conditions.Add(cc)
                                    | None -> ())
                                // Phase 7 §4.2 C-7.1: ControlCallProperties plc 키
                                tryProp obj "plc"
                                |> Option.iter (fun plcEl ->
                                    parsePlcCall ctx (joinDiagKey callPath "plc") call plcEl))
                    with ex ->
                        ctx.Diagnostics.Add(callPath, ex.Message)
                callIdx <- callIdx + 1

            // arrows (Work 안) — ArrowBetweenCalls
            let resolveCallId (rawName: string) (subPath: string) : Guid option =
                let normalized = normalizePath rawName
                match callIdMap.TryGetValue normalized with
                | true, ids when ids.Count = 1 -> Some ids.[0]
                | true, ids ->
                    ctx.Diagnostics.Add(
                        subPath,
                        sprintf "'%s' 가 같은 Work 안에서 %d 회 호출되어 source/target 으로 식별 불가. 순차 chain 이면 중복 호출을 다른 Work 로 분리하세요." rawName ids.Count)
                    None
                | false, _ ->
                    let candidates = nearestCandidates normalized callIdMap.Keys 3
                    ctx.Diagnostics.Add(
                        subPath,
                        sprintf "Call '%s' 가 발견되지 않음." rawName,
                        ?suggestion = (if candidates.IsEmpty then None else Some (String.Join(" / ", candidates))))
                    None

            let processOneArrow (arrowPath: string) (arrowRaw: string) : unit =
                match parseArrowSpec arrowRaw with
                | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                | Ok spec ->
                    match spec.TypeRaw with
                    | None -> ctx.Diagnostics.Add(arrowPath, "type 누락. '<from> -> <to> : <Type>' 형식 사용.")
                    | Some tRaw ->
                        match parseArrowType tRaw with
                        | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                        | Ok aType ->
                            let srcOpt = resolveCallId spec.FromRaw (arrowPath + ".from")
                            let tgtOpt = resolveCallId spec.ToRaw (arrowPath + ".to")
                            match srcOpt, tgtOpt with
                            | Some s, Some t ->
                                // S3b — modeling 시 같은 arrow 가 store 에 이미 있으면 중복 add skip.
                                let skipDup =
                                    match !ctx.Level with
                                    | Modeling -> arrowCallExists ctx.Store s t aType
                                    | Full -> false
                                if not skipDup then
                                    try
                                        ToolOperations.queueAddArrow ctx.Plan ctx.Store s t aType |> ignore
                                    with ex ->
                                        ctx.Diagnostics.Add(arrowPath, ex.Message)
                            | _ -> ()

            let mutable arrowIdx = 0
            for arrowResult in workArrowsList do
                let arrowPath = sprintf "%s.arrows[%d]" path arrowIdx
                match arrowResult with
                | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                | Ok arrowRaw -> processOneArrow arrowPath arrowRaw
                arrowIdx <- arrowIdx + 1
        with ex ->
            ctx.Diagnostics.Add(path, ex.Message)

    let private dispatchActiveFlows (ctx: ApplyContext) (sysEntry: SystemEntry) (sysEl: JsonElement) (basePath: string) : unit =
        match sysEntry.SystemId.Value with
        | None -> ()
        | Some sysId ->
            // ── flows: mapping (D5) — flow 별 속성 가능 (value 허용 키 = plc 만) ──
            // flows 를 먼저 생성해야 works 의 `flow:` 속성을 FlowIds 에서 resolve 가능.
            match tryProp sysEl "flows" with
            | Some flowsEl when flowsEl.ValueKind <> JsonValueKind.Object ->
                ctx.Diagnostics.Add(joinDiagKey basePath "flows", "Object 기대.")
            | Some flowsEl ->
                for prop in flowsEl.EnumerateObject() do
                    let flowName = prop.Name
                    let flowEl = prop.Value
                    let flowPath = sprintf "%s.flows.%s" basePath flowName
                    // M1 fix: flow 이름 sanitize 가드.
                    if not (tryValidateName ctx flowPath "Flow name" flowName) then () else
                    // S3b — modeling level 시 lookup-first (기존 store/plan 의 같은 (sysId, flowName) Flow reuse).
                    // #32 — store + plan 합집합. plan-only Flow 도 reuse (multi-stage forward-ref 회귀 가드).
                    let reuseFlowId =
                        match !ctx.Level with
                        | Modeling ->
                            Queries.flowsOf sysId ctx.Store
                            |> List.tryFind (fun f -> f.Name = flowName)
                            |> Option.orElseWith (fun () -> Internal.PlanLookup.tryFindFlowByName ctx.Plan sysId flowName)
                            |> Option.map (fun f -> f.Id)
                        | Full -> None
                    // **새 flow 생성** 시에만 중복 검사 — modeling reuse (seed 된 기존 flow 재참조) 는 제외 (M-1 회귀 차단).
                    if reuseFlowId.IsNone && sysEntry.FlowIds.ContainsKey flowName then
                        // flows mapping key 중복 (wire dup-key) — 첫 등장만 채택.
                        ctx.Diagnostics.Add(flowPath, sprintf "flow '%s' 키 중복." flowName)
                    else
                    try
                        let flowId =
                            match reuseFlowId with
                            | Some id -> id
                            | None -> ToolOperations.queueAddFlow ctx.Plan ctx.Store flowName sysId
                        sysEntry.FlowIds.[flowName] <- flowId

                        // flows value 허용 키 화이트리스트 = plc 만 (D5). 그 외 거부.
                        if flowEl.ValueKind = JsonValueKind.Object then
                            for fp in flowEl.EnumerateObject() do
                                if fp.Name <> "plc" then
                                    ctx.Diagnostics.Add(flowPath, sprintf "키 '%s' 인식 불가. flows value 허용 키 = plc." fp.Name)

                        // Phase 7 §4.2 C-7.1: ControlFlowProperties plc 키
                        tryProp flowEl "plc"
                        |> Option.iter (fun plcEl ->
                            lookupFlowById ctx flowId
                            |> Option.iter (fun flow ->
                                parsePlcFlow ctx (joinDiagKey flowPath "plc") flow plcEl))
                    with ex ->
                        ctx.Diagnostics.Add(flowPath, ex.Message)
            | None -> ()

            // ── works: system 직속 mapping (D6) — 각 work 에 flow: 속성으로 소속 명시 ──
            match tryProp sysEl "works" with
            | Some worksEl when worksEl.ValueKind <> JsonValueKind.Object ->
                ctx.Diagnostics.Add(joinDiagKey basePath "works", "Object 기대.")
            | Some worksEl ->
                for prop in worksEl.EnumerateObject() do
                    let workLocalName = prop.Name
                    let workEl = prop.Value
                    let workPath = sprintf "%s.works.%s" basePath workLocalName
                    if workEl.ValueKind <> JsonValueKind.Object then
                        ctx.Diagnostics.Add(workPath, "Object 기대.")
                    else
                        // flow: 속성 — 소속 flow (D6). 누락/미존재 flow → validate 에러.
                        match tryProp workEl "flow" |> Option.bind tryString with
                        | None ->
                            ctx.Diagnostics.Add(workPath, "work 의 'flow' 속성 누락 — 소속 flow 를 명시하세요.")
                        | Some flowName ->
                            match sysEntry.FlowIds.TryGetValue flowName with
                            | false, _ ->
                                let candidates = nearestCandidates flowName sysEntry.FlowIds.Keys 3
                                ctx.Diagnostics.Add(
                                    joinDiagKey workPath "flow",
                                    sprintf "flow '%s' 가 발견되지 않음." flowName,
                                    ?suggestion = (if candidates.IsEmpty then None else Some (String.Join(" / ", candidates))))
                            | true, flowId ->
                                dispatchWork ctx sysEntry flowId workLocalName workEl workPath
            | None -> ()

            // ── arrows: system 직속 (work 간 arrow, bare 표기 — D2) ──
            // work 이름이 system-unique 이므로 WorkIdsByName flat dict 로 cross-flow 1-pass O(1) resolve.
            let arrowsList =
                tryProp sysEl "arrows"
                |> Option.bind (fun el ->
                    if el.ValueKind = JsonValueKind.Array then
                        el.EnumerateArray()
                        |> Seq.map extractArrowString
                        |> Seq.toList
                        |> Some
                    else None)
                |> Option.defaultValue []
            let resolveWorkId (rawName: string) (subPath: string) : Guid option =
                let normalized = normalizePath rawName
                match sysEntry.WorkIdsByName.TryGetValue normalized with
                | true, id -> Some id
                | _ ->
                    let candidates = nearestCandidates normalized sysEntry.WorkIdsByName.Keys 3
                    ctx.Diagnostics.Add(
                        subPath,
                        sprintf "Work '%s' 가 발견되지 않음." rawName,
                        ?suggestion = (if candidates.IsEmpty then None else Some (String.Join(" / ", candidates))))
                    None
            let processOneWorkArrow (arrowPath: string) (arrowRaw: string) : unit =
                match parseArrowSpec arrowRaw with
                | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                | Ok spec ->
                    match spec.TypeRaw with
                    | None -> ctx.Diagnostics.Add(arrowPath, "type 누락. '<from> -> <to> : <Type>' 형식 사용.")
                    | Some tRaw ->
                        match parseArrowType tRaw with
                        | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                        | Ok aType ->
                            let srcOpt = resolveWorkId spec.FromRaw (arrowPath + ".from")
                            let tgtOpt = resolveWorkId spec.ToRaw (arrowPath + ".to")
                            match srcOpt, tgtOpt with
                            | Some s, Some t ->
                                // S3b — modeling 시 같은 arrow 가 store 에 이미 있으면 중복 add skip.
                                let skipDup =
                                    match !ctx.Level with
                                    | Modeling -> arrowWorkExists ctx.Store s t aType
                                    | Full -> false
                                if not skipDup then
                                    try
                                        ToolOperations.queueAddArrow ctx.Plan ctx.Store s t aType |> ignore
                                    with ex ->
                                        ctx.Diagnostics.Add(arrowPath, ex.Message)
                            | _ -> ()
            let mutable aIdx = 0
            for arrowResult in arrowsList do
                let arrowPath = sprintf "%s.arrows[%d]" basePath aIdx
                match arrowResult with
                | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                | Ok arrowRaw -> processOneWorkArrow arrowPath arrowRaw
                aIdx <- aIdx + 1

    let private buildActiveFlows (ctx: ApplyContext) (systemsEl: JsonElement) : unit =
        if systemsEl.ValueKind <> JsonValueKind.Array then ()
        else
            let mutable idx = 0
            for sysEl in systemsEl.EnumerateArray() do
                let basePath = sprintf "systems[%d]" idx
                match tryProp sysEl "system" |> Option.bind tryString with
                | None -> ()
                | Some name ->
                    match ctx.Systems.TryGetValue name with
                    | true, entry when entry.Kind = "active" ->
                        dispatchActiveFlows ctx entry sysEl basePath
                    | _ -> ()
                idx <- idx + 1

    let private tryStripWorksCollectionSuffix (rawPath: string) : string option =
        match pathSegments rawPath |> List.rev with
        | "works" :: parentRev when parentRev.Length >= 2 ->
            parentRev
            |> List.rev
            |> Array.ofList
            |> fun parts -> Some (String.Join(".", parts))
        | _ -> None

    let private tryGetSeededSystemEntry (ctx: ApplyContext) (systemName: string) =
        seedStoreSystems ctx
        match ctx.Systems.TryGetValue systemName with
        | true, entry -> Some entry
        | false, _ -> None

    let private dispatchPatchAddFlows
        (ctx: ApplyContext)
        (path: string)
        (systemPath: string)
        (entryEl: JsonElement) : unit =

        match findSystemByPath ctx.Store systemPath with
        | None ->
            ctx.Diagnostics.Add(path, sprintf "System path '%s' 가 store 에 없습니다." systemPath)
        | Some system ->
            match tryGetSeededSystemEntry ctx system.Name with
            | Some sysEntry when sysEntry.Kind = "active" ->
                // 새 구조 — active content 키 = flows / works / arrows (system 직속). 하나라도 있어야 함.
                let hasActiveKey =
                    [ "flows"; "works"; "arrows" ]
                    |> List.exists (fun k -> tryProp entryEl k |> Option.isSome)
                if not hasActiveKey then
                    ctx.Diagnostics.Add(path, "`in:` 이 System 을 가리키면 `flows` / `works` / `arrows` 키가 필요합니다.")
                else
                    dispatchActiveFlows ctx sysEntry entryEl path
            | Some _ ->
                ctx.Diagnostics.Add(path, sprintf "System '%s' 는 active 가 아닙니다. Flow 는 active system 아래에만 추가할 수 있습니다." system.Name)
            | None ->
                ctx.Diagnostics.Add(path, sprintf "System '%s' 이름 테이블 구성 실패." system.Name)

    let private dispatchPatchAddWorks
        (ctx: ApplyContext)
        (path: string)
        (flowPath: string)
        (entryEl: JsonElement) : unit =

        match findFlowByPath ctx.Store flowPath with
        | None ->
            ctx.Diagnostics.Add(path, sprintf "Flow path '%s' 가 store 에 없습니다." flowPath)
        | Some flow ->
            match lookupSystemById ctx flow.ParentId with
            | None ->
                ctx.Diagnostics.Add(path, sprintf "Flow '%s' 의 parent System 을 찾을 수 없습니다." flowPath)
            | Some system ->
                match tryGetSeededSystemEntry ctx system.Name with
                | Some sysEntry when sysEntry.Kind = "active" ->
                    let workEntries =
                        entryEl.EnumerateObject()
                        |> Seq.filter (fun p -> p.Name <> "in")
                        |> Seq.toList
                    if workEntries.IsEmpty then
                        ctx.Diagnostics.Add(path, "`in: <System>.<Flow>.works` entry 에 추가할 Work 키가 없습니다.")
                    for prop in workEntries do
                        let workPath = sprintf "%s.%s" path prop.Name
                        if prop.Value.ValueKind <> JsonValueKind.Object then
                            ctx.Diagnostics.Add(workPath, "Object 기대.")
                        else
                            dispatchWork ctx sysEntry flow.Id prop.Name prop.Value workPath
                | Some _ ->
                    ctx.Diagnostics.Add(path, sprintf "Flow '%s' 는 active system 아래 Flow 가 아닙니다. Active Work 만 patch.add 로 추가할 수 있습니다." flowPath)
                | None ->
                    ctx.Diagnostics.Add(path, sprintf "System '%s' 이름 테이블 구성 실패." system.Name)

    let private dispatchPatchAddChild
        (ctx: ApplyContext)
        (idx: int)
        (entryEl: JsonElement) : unit =

        let path = sprintf "patch.add[%d]" idx
        if entryEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "Object 기대 (실제 %A)." entryEl.ValueKind)
        else
            match tryProp entryEl "in" |> Option.bind tryString with
            | None ->
                ctx.Diagnostics.Add(path, "patch.add entry 는 `system:` 또는 `in:` 키가 필요합니다.")
            | Some inPath ->
                match tryStripWorksCollectionSuffix inPath with
                | Some flowPath -> dispatchPatchAddWorks ctx path flowPath entryEl
                | None -> dispatchPatchAddFlows ctx path inPath entryEl

    // ─── Patch DSL — v0 (SSOT §2.6) ─────────────────────────────────────────
    //
    // 본 PoC 는 schema 의 add / arrows.add / rename / remove 4 종 dispatch.
    // 자세한 구현은 후속 cycle — patch path 는 store 가 이미 채워져 있는 경우 주력 시나리오.

    /// SSOT §2.5.1 — dotted-path → (EntityKind, Guid) 변환. path 깊이로 EntityKind 자동 결정.
    /// 1 seg = Project / 2 = System / 3 = ApiDef 또는 Flow (System 직접 자식 ambiguity) /
    /// 4 = Work / 5 = Call. 6+ 는 schema 위반 — None 반환 (호출자가 VALIDATION_ERROR 변환).
    /// 3-segment ambiguity (ApiDef vs Flow) 은 ApiDef → Flow → None 순.
    /// `findSystemByName` / `findFlowByPath` 는 호출지점 그대로 유지
    /// (병존 — `Apps/Promaker/Docs/done-read-surface-guid-cleanup.md` §4.6 정합).
    let tryFindEntity (store: DsStore) (rawPath: string) : (EntityKind * Guid) option =
        let segs = pathSegments rawPath
        if segs.IsEmpty || segs.Length > 5 then None
        else
            let projectName = segs.[0]
            let project =
                Queries.allProjects store
                |> List.tryFind (fun p -> p.Name = projectName)
            match segs.Length with
            | 1 -> project |> Option.map (fun p -> EntityKind.Project, p.Id)
            | _ ->
                project |> Option.bind (fun p ->
                    let sysName = segs.[1]
                    let sys =
                        Queries.projectSystemsOf p.Id store
                        |> List.tryFind (fun s -> s.Name = sysName)
                    match segs.Length with
                    | 2 -> sys |> Option.map (fun s -> EntityKind.System, s.Id)
                    | _ ->
                        sys |> Option.bind (fun s ->
                            let thirdName = segs.[2]
                            match segs.Length with
                            | 3 ->
                                // ApiDef 먼저 (System 직접 자식 ambiguity 해소 순서 — SSOT §2.5.1)
                                let apiHit =
                                    Queries.apiDefsOf s.Id store
                                    |> List.tryFind (fun d -> d.Name = thirdName)
                                match apiHit with
                                | Some d -> Some (EntityKind.ApiDef, d.Id)
                                | None ->
                                    Queries.flowsOf s.Id store
                                    |> List.tryFind (fun f -> f.Name = thirdName)
                                    |> Option.map (fun f -> EntityKind.Flow, f.Id)
                            | _ ->
                                // 4 또는 5 segment — Flow 경로만 (ApiDef 는 깊이 3 cap)
                                Queries.flowsOf s.Id store
                                |> List.tryFind (fun f -> f.Name = thirdName)
                                |> Option.bind (fun f ->
                                    let fourthName = segs.[3]
                                    let work =
                                        Queries.worksOf f.Id store
                                        |> List.tryFind (fun w -> w.LocalName = fourthName)
                                    match segs.Length with
                                    | 4 -> work |> Option.map (fun w -> EntityKind.Work, w.Id)
                                    | 5 ->
                                        work |> Option.bind (fun w ->
                                            let fifthName = segs.[4]
                                            Queries.callsOf w.Id store
                                            |> List.tryFind (fun c -> c.Name = fifthName)
                                            |> Option.map (fun c -> EntityKind.Call, c.Id))
                                    | _ -> None)))

    /// SSOT §2.5.1 역방향 — entity → dotted-path (leading `.` + dot segment).
    /// find_by_name 출력 emit + scope path 형성에 사용. 매칭 실패 / unsupported kind 시 None.
    ///
    /// **kind 별 안정성** (Phase 6 chunk-1c, Outlier 2/3 통합):
    /// - Project / System / Flow / ApiDef / Work / Call: 5 kind 지원. 모두 재귀 호출 패턴으로 통일
    ///   (System 도 `tryPathOf store Project p.Id` 경유 — 직접 sprintf 조립 제거).
    /// - orphan System (project 미부착): None — 1-segment path 가 `tryFindEntity` 역해석 시
    ///   Project 로 round-trip 오인되는 회귀 회피.
    /// - **path-unsupported kinds (None)**: Button / Lamp / Condition / Action / ApiDefCategory /
    ///   DeviceRoot / Arrow 등. dotted-path 어휘 자체가 정의된 5 kind 외엔 명시적 None.
    let rec tryPathOf (store: DsStore) (kind: EntityKind) (id: Guid) : string option =
        match kind with
        | EntityKind.Project ->
            Queries.getProject id store
            |> Option.map (fun p -> "." + p.Name)
        | EntityKind.System ->
            Queries.getSystem id store
            |> Option.bind (fun s ->
                Queries.allProjects store
                |> List.tryFind (fun p ->
                    p.ActiveSystemIds.Contains s.Id || p.PassiveSystemIds.Contains s.Id)
                |> Option.bind (fun p ->
                    tryPathOf store EntityKind.Project p.Id
                    |> Option.map (fun pp -> pp + "." + s.Name)))
        | EntityKind.Flow ->
            Queries.getFlow id store
            |> Option.bind (fun f ->
                tryPathOf store EntityKind.System f.ParentId
                |> Option.map (fun pp -> pp + "." + f.Name))
        | EntityKind.ApiDef ->
            Queries.getApiDef id store
            |> Option.bind (fun d ->
                tryPathOf store EntityKind.System d.ParentId
                |> Option.map (fun pp -> pp + "." + d.Name))
        | EntityKind.Work ->
            Queries.getWork id store
            |> Option.bind (fun w ->
                tryPathOf store EntityKind.Flow w.ParentId
                |> Option.map (fun pp -> pp + "." + w.LocalName))
        | EntityKind.Call ->
            Queries.getCall id store
            |> Option.bind (fun c ->
                tryPathOf store EntityKind.Work c.ParentId
                |> Option.map (fun pp -> pp + "." + c.Name))
        | _ -> None  // path-unsupported: Button/Lamp/Condition/Action/ApiDefCategory/DeviceRoot/Arrow

    /// `tryPathOf` 호환 wrapper — 호출지점이 매칭 실패를 string fallback 으로 처리하는 경우용.
    /// 신규 호출지점은 `tryPathOf` 직접 사용 권장 (orphan / unsupported kind 명시적 처리).
    let pathOf (store: DsStore) (kind: EntityKind) (id: Guid) : string =
        tryPathOf store kind id |> Option.defaultValue ""

    let private applyPatch (ctx: ApplyContext) (patchEl: JsonElement) : unit =
        // patch 의 add — systems list 형태 (existing systems 와 동일 schema)
        // **Critical 1 (review M3.1)**: `apply` 의 systems path 와 동일하게 collectSystems 후
        // diagnostic 게이트 적용 — partial state 회피.
        match tryProp patchEl "add" with
        | Some addEl when addEl.ValueKind = JsonValueKind.Array ->
            let entriesWithIdx = addEl.EnumerateArray() |> Seq.toList |> List.indexed

            let systemsAdd =
                entriesWithIdx
                |> List.choose (fun (_, e) -> if tryProp e "system" |> Option.isSome then Some e else None)
            if not systemsAdd.IsEmpty then
                let arr =
                    let bytes =
                        let ms = new MemoryStream()
                        do
                            use w = new Utf8JsonWriter(ms)
                            w.WriteStartArray()
                            for e in systemsAdd do e.WriteTo(w)
                            w.WriteEndArray()
                            w.Flush()
                        ms.ToArray()
                    JsonDocument.Parse(bytes)
                use _ = arr
                let beforeCount = ctx.Diagnostics.Count
                collectSystems ctx arr.RootElement
                // **Major 1 (review M4)**: store-side 충돌도 검출 — 같은 이름 system 이 store 에 이미 있으면 에러.
                for sysEl in arr.RootElement.EnumerateArray() do
                    match tryProp sysEl "system" |> Option.bind tryString with
                    | Some name when (findSystemByName ctx.Store name).IsSome ->
                        ctx.Diagnostics.Add(
                            sprintf "patch.add[%s]" name,
                            sprintf "System '%s' 가 store 에 이미 존재합니다 (rename / remove 후 add 하세요)." name)
                    | _ -> ()
                if ctx.Diagnostics.Count = beforeCount then
                    buildSystems ctx arr.RootElement
                    buildActiveFlows ctx arr.RootElement

            if not ctx.Diagnostics.HasErrors then
                entriesWithIdx
                |> List.filter (fun (_, e) -> tryProp e "system" |> Option.isNone)
                |> List.iter (fun (i, entry) -> dispatchPatchAddChild ctx i entry)
        | _ -> ()

        // patch.arrows.add / patch.arrows.remove — SSOT §2.6 / §3.4 (C1 — work 간 arrow 는 system-scope, D2)
        // work 간 arrow 는 system 레벨 엔티티 (ArrowBetweenWorks.ParentId=SystemId) 이므로 `in:` = system path.
        // 두 dispatcher 의 outer + middle loop (in/entries/findSystemByPath/extractArrowString/parseArrowSpec/resolveWork)
        // 골격을 iterSystemArrowEntries helper 로 통합. add 는 spec.TypeRaw 필수, remove 는 옵션 분기 (callback 안 위치).
        let iterSystemArrowEntries
            (sectionTag: string)
            (listEl: JsonElement)
            (onSpec: string -> DsSystem -> string -> ArrowSpec -> (string -> Guid option) -> unit) =
            let mutable aIdx = 0
            for entry in listEl.EnumerateArray() do
                let path = sprintf "patch.arrows.%s[%d]" sectionTag aIdx
                let inPath = tryProp entry "in" |> Option.bind tryString
                let entriesEl = tryProp entry "entries"
                match inPath, entriesEl with
                | None, _ -> ctx.Diagnostics.Add(path, "'in' 키 누락 (System path 필요).")
                | _, None -> ctx.Diagnostics.Add(path, "'entries' 키 누락 (arrow 표기 list 필요).")
                | Some systemPath, Some entries when entries.ValueKind = JsonValueKind.Array ->
                    match findSystemByPath ctx.Store systemPath with
                    | None -> ctx.Diagnostics.Add(path, sprintf "System '%s' 가 store 에 없습니다." systemPath)
                    | Some system ->
                        // work 이름 = system-unique (D1) — system 전체 work 테이블 O(W) 1회 빌드 후 O(1) lookup
                        // (arrow 순회 중 재평탄화 금지 — M2). cross-flow resolve 자연 해소.
                        let workTable = Dictionary<string, Guid>(StringComparer.Ordinal)
                        for f in Queries.flowsOf system.Id ctx.Store do
                            for wk in Queries.worksOf f.Id ctx.Store do
                                workTable.[wk.LocalName] <- wk.Id
                        let resolveWork (rawName: string) : Guid option =
                            match workTable.TryGetValue (normalizePath rawName) with
                            | true, id -> Some id
                            | _ -> None
                        let mutable eIdx = 0
                        for arrEl in entries.EnumerateArray() do
                            let entryPath = sprintf "%s.entries[%d]" path eIdx
                            match extractArrowString arrEl with
                            | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                            | Ok raw ->
                                match parseArrowSpec raw with
                                | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                                | Ok spec -> onSpec systemPath system entryPath spec resolveWork
                            eIdx <- eIdx + 1
                | _ -> ctx.Diagnostics.Add(path, "'entries' 가 array 가 아닙니다.")
                aIdx <- aIdx + 1

        match tryProp patchEl "arrows" with
        | Some arrowsEl when arrowsEl.ValueKind = JsonValueKind.Object ->
            // arrows.add — System 단위 entries. spec.TypeRaw 필수.
            match tryProp arrowsEl "add" with
            | Some addList when addList.ValueKind = JsonValueKind.Array ->
                iterSystemArrowEntries "add" addList (fun systemPath _system entryPath spec resolveWork ->
                    match spec.TypeRaw with
                    | None -> ctx.Diagnostics.Add(entryPath, "type 누락. '<from> -> <to> : <Type>' 형식 사용.")
                    | Some tRaw ->
                        match parseArrowType tRaw with
                        | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                        | Ok aType ->
                            match resolveWork spec.FromRaw, resolveWork spec.ToRaw with
                            | Some s, Some t ->
                                try
                                    ToolOperations.queueAddArrow ctx.Plan ctx.Store s t aType |> ignore
                                with ex -> ctx.Diagnostics.Add(entryPath, ex.Message)
                            | None, _ -> ctx.Diagnostics.Add(entryPath + ".from", sprintf "Work '%s' 가 System '%s' 에 없습니다." spec.FromRaw systemPath)
                            | _, None -> ctx.Diagnostics.Add(entryPath + ".to", sprintf "Work '%s' 가 System '%s' 에 없습니다." spec.ToRaw systemPath))
            | _ -> ()
            // arrows.remove — System 단위 entries (arrows.add 와 대칭).
            // 입력: { in: <systemPath>, entries: [ "<from> -> <to>" | "<from> -> <to> : <Type>", ... ] }
            // Type 미지정 시 (from, to) 쌍 unique 일 때만 매칭 — 다중 매칭 시 type 명시 요구.
            match tryProp arrowsEl "remove" with
            | Some remList when remList.ValueKind = JsonValueKind.Array ->
                iterSystemArrowEntries "remove" remList (fun systemPath _system entryPath spec resolveWork ->
                    let typeFilter : Result<ArrowType option, string> =
                        match spec.TypeRaw with
                        | None -> Ok None
                        | Some tRaw -> parseArrowType tRaw |> Result.map Some
                    match resolveWork spec.FromRaw, resolveWork spec.ToRaw, typeFilter with
                    | None, _, _ ->
                        ctx.Diagnostics.Add(entryPath + ".from", sprintf "Work '%s' 가 System '%s' 에 없습니다." spec.FromRaw systemPath)
                    | _, None, _ ->
                        ctx.Diagnostics.Add(entryPath + ".to", sprintf "Work '%s' 가 System '%s' 에 없습니다." spec.ToRaw systemPath)
                    | _, _, Error msg ->
                        ctx.Diagnostics.Add(entryPath, msg)
                    | Some s, Some t, Ok aTypeOpt ->
                        // resolveWork 가 system 전체 work 테이블에서 (s, t) 를 resolve 하므로 cross-flow arrow 도 매칭.
                        // candidate 는 store 전역 ArrowWorks 에서 (s, t) 정확 ID 매칭 — ArrowBetweenWorks.ParentId=SystemId
                        // 이라 자동으로 해당 system scope (Work id 가 unique).
                        let candidates =
                            ctx.Store.ArrowWorksReadOnly.Values
                            |> Seq.filter (fun a -> a.SourceId = s && a.TargetId = t)
                            |> Seq.filter (fun a ->
                                match aTypeOpt with
                                | None -> true
                                | Some aType -> a.ArrowType = aType)
                            |> Seq.toList
                        match candidates with
                        | [] ->
                            ctx.Diagnostics.Add(entryPath, sprintf "Arrow '%s -> %s' 가 System '%s' 에 없습니다." spec.FromRaw spec.ToRaw systemPath)
                        | [ arrow ] ->
                            try
                                ToolOperations.queueRemoveEntity ctx.Plan ctx.Store arrow.Id |> ignore
                            with ex -> ctx.Diagnostics.Add(entryPath, ex.Message)
                        | many ->
                            ctx.Diagnostics.Add(
                                entryPath,
                                sprintf "Arrow '%s -> %s' 가 System '%s' 에 %d 개 — Type 을 명시하세요 (예: '%s -> %s : Start')."
                                    spec.FromRaw spec.ToRaw systemPath many.Length spec.FromRaw spec.ToRaw))
            | _ -> ()
        | _ -> ()

        // patch.rename — [{ <oldPath>: <newName> }, ...]
        match tryProp patchEl "rename" with
        | Some renameEl when renameEl.ValueKind = JsonValueKind.Array ->
            let mutable rIdx = 0
            for entry in renameEl.EnumerateArray() do
                let path = sprintf "patch.rename[%d]" rIdx
                if entry.ValueKind = JsonValueKind.Object then
                    for prop in entry.EnumerateObject() do
                        let oldPath = prop.Name
                        match tryString prop.Value with
                        | None -> ctx.Diagnostics.Add(path, "newName 은 string 이어야 합니다.")
                        | Some newName when not (tryValidateName ctx path "Rename newName" newName) ->
                            () // M1 fix: rename newName sanitize 가드 — 메시지는 tryValidateName 가 Diagnostics 에 추가.
                        | Some newName ->
                            // 현재 PoC 는 System 만 — 단일 segment path
                            let segs = pathSegments oldPath
                            match segs with
                            | [ sysName ] ->
                                match Queries.allProjects ctx.Store with
                                | [] -> ctx.Diagnostics.Add(path, "store 에 project 없음.")
                                | _ ->
                                    let sysOpt =
                                        Queries.allProjects ctx.Store
                                        |> List.collect (fun p ->
                                            (Queries.activeSystemsOf p.Id ctx.Store)
                                            @ (Queries.passiveSystemsOf p.Id ctx.Store))
                                        |> List.tryFind (fun s -> s.Name = sysName)
                                    match sysOpt with
                                    | None ->
                                        ctx.Diagnostics.Add(path, sprintf "System '%s' 가 발견되지 않음." sysName)
                                    | Some s ->
                                        try
                                            ToolOperations.queueRenameEntity ctx.Plan ctx.Store s.Id newName |> ignore
                                        with ex ->
                                            ctx.Diagnostics.Add(path, ex.Message)
                            | _ ->
                                ctx.Diagnostics.Add(path, "PoC 는 single-segment system rename 만 지원.")
                rIdx <- rIdx + 1
        | _ -> ()

        // patch.remove — [<path>, ...]
        // dotted-path 1~5 segment (Project / System / Flow|ApiDef / Work / Call) 전부 지원.
        // tryFindEntity 가 path 깊이로 EntityKind 자동 결정 → queueRemoveEntity 가 cascade 위임 (CascadeRemove).
        // **breaking (Phase A)**: legacy single-segment 'SysName' 호환 폐기 — SSOT §2.5.1 정합으로 segs[0] = Project name 강제.
        //   기존 호출자가 'Cyl1' 형식을 썼다면 'ProjectName.Cyl1' 로 마이그레이션 필요.
        match tryProp patchEl "remove" with
        | Some removeEl when removeEl.ValueKind = JsonValueKind.Array ->
            let mutable rIdx = 0
            for entry in removeEl.EnumerateArray() do
                let path = sprintf "patch.remove[%d]" rIdx
                match tryString entry with
                | None -> ctx.Diagnostics.Add(path, "remove 항목은 path string 이어야 합니다.")
                | Some rawPath ->
                    match tryFindEntity ctx.Store rawPath with
                    | None ->
                        ctx.Diagnostics.Add(path, sprintf "Entity '%s' 가 store 에 없습니다." rawPath)
                    | Some (_, id) ->
                        try
                            ToolOperations.queueRemoveEntity ctx.Plan ctx.Store id |> ignore
                        with ex -> ctx.Diagnostics.Add(path, ex.Message)
                rIdx <- rIdx + 1
        | _ -> ()

    // ─── Public entry — apply / validate ────────────────────────────────────

    /// apply_model_doc 본체. plan 누적까지만 수행 (실제 store commit 은 호출자 측).
    /// 반환: Diagnostics + system name → SystemId 매핑 (refs).
    let apply
        (plan: ImportPlanBuilder)
        (store: DsStore)
        (root: JsonElement) : Diagnostics * Map<string, Guid> =

        let ctx = newContext plan store
        // review C1 (partial-commit transactional leak): 진입 시점 plan 위치 기록 → 종료 시 HasErrors 면
        // 누적된 부분 op 를 TruncateTo 로 rollback (`ImportPlanBuilder.TruncateTo` 와 동일 패턴).
        // 본 fix 없으면 collectSystems→buildSystems→buildActiveFlows→applyPatch 중 *부분 성공* op 가
        // plan 에 남아 EndTurn 시 ApplyImportPlan 으로 store 에 silent commit — 다음 turn 의 retry 가
        // "이미 존재" 에러로 connection 단절.
        let snapshotCount = plan.Count

        // protocol 키 검증
        match tryProp root "protocol" |> Option.bind tryString with
        | None ->
            ctx.Diagnostics.Add("protocol", "키 누락 또는 미지원 버전. 'promaker/v0' 명시 필요.")
        | Some v when v <> "promaker/v0" ->
            ctx.Diagnostics.Add("protocol", sprintf "'%s' 미지원. 'promaker/v0' 만 허용." v)
        | _ -> ()

        // SSOT §2.7 룰 #7 / §2.8: view: partial 은 view-only — apply/validate 재입력 거부.
        // view: full 은 round-trip 시나리오 (self export → apply) 정합으로 허용. unknown 값은 사전 거부.
        // **검사 순서 (C2 sub-agent review)**: partial 거부가 level 검사 *전* — partial 발견 시 level
        // 검사 skip (메시지 중복 회피).
        match tryProp root "view" |> Option.bind tryString with
        | Some "full" -> ()
        | Some "partial" ->
            ctx.Diagnostics.Add("view", "partial export 결과는 view-only — apply/validate 재입력 불가. 전체 export (view: full) 로 다시 호출하거나 'view:' 키를 제거하세요.")
        | Some other ->
            ctx.Diagnostics.Add("view", sprintf "값 '%s' 인식 불가. 'full' 또는 'partial'." other)
        | None -> ()

        // SSOT §2.8: summary 는 partial export 진단 metadata 전용 — apply/validate 재입력 불가.
        // view: partial 과 짝이 되는 진단 신호 ({totalEntities, emitted, budget}). 입력 단에 등장하면 사전 거부.
        match tryProp root "summary" with
        | Some _ ->
            ctx.Diagnostics.Add("summary", "summary 는 partial export 진단 metadata 전용 — apply/validate 재입력 불가. 'summary:' 키를 제거하세요.")
        | None -> ()

        // SSOT §2.7 룰 #29 / Phase 7 §10.2 #31 (S3) — level 키 검증.
        // wire 의 `level:` 키가 SSOT — `apply` 자체에 별도 level 인자 없음 (C1 fix 자동 해소).
        // 'full' 또는 부재 = 기존 apply 동작. 'modeling' = wire walk + B/C/D 키 사전 거부 mode.
        let wireLevel =
            match tryProp root "level" |> Option.bind tryString with
            | Some "full" -> Some Full
            | Some "modeling" -> Some Modeling
            | Some other ->
                ctx.Diagnostics.Add("level", sprintf "값 '%s' 인식 불가. 'full' 또는 'modeling'." other)
                None
            | None -> Some Full

        // SSOT §2.7 룰 #30 / Phase 7 §10.2 #31 (S3) — modeling level wire 에 B/C/D 키 등장 거부.
        // 골격 키 / A_Modeling 키 는 ModelingCategory.nonModelingKeys 부재 → 허용 통과.
        // 재귀 walk — 모든 object property name 점검 (array element 의 path 는 [idx] suffix).
        // **검사 순서 (C2)**: view: partial 거부가 먼저 — 이미 HasErrors 면 본 walk skip 으로 메시지 중복 회피.
        let rec walkAndRejectNonModelingKeys (path: string) (el: JsonElement) : unit =
            match el.ValueKind with
            | JsonValueKind.Object ->
                for prop in el.EnumerateObject() do
                    let propPath = joinDiagKey path prop.Name
                    match Map.tryFind prop.Name nonModelingKeys with
                    | Some cat ->
                        ctx.Diagnostics.Add(propPath,
                            sprintf "키 '%s' 는 level: modeling 입력에서 등장 금지 (분류 = %s — §2.4.1 Category 사전 참조). level 을 'full' 로 변경하거나 키를 제거하세요."
                                prop.Name (categoryLabel cat))
                    | None ->
                        walkAndRejectNonModelingKeys propPath prop.Value
            | JsonValueKind.Array ->
                let mutable idx = 0
                for item in el.EnumerateArray() do
                    walkAndRejectNonModelingKeys (sprintf "%s[%d]" path idx) item
                    idx <- idx + 1
            | _ -> ()

        if not ctx.Diagnostics.HasErrors && wireLevel = Some Modeling then
            walkAndRejectNonModelingKeys "" root

        // S3b — wireLevel 확정 후 ctx.Level set. dispatch helper 가 lookup-first 분기 결정 시 참조.
        wireLevel |> Option.iter (fun lvl -> ctx.Level := lvl)

        if ctx.Diagnostics.HasErrors then
            // protocol 거부 시점 — 본 path 는 plan 미변경이라 truncate no-op. 일관성 위해 호출.
            plan.TruncateTo(snapshotCount)
            ctx.Diagnostics, Map.empty
        else
            // project 키 처리
            let projectIdOpt = resolveProjectKey ctx root

            // Phase 7 §4.2 C-6: Project root-level meta — author / version (단순 leaf 키)
            projectIdOpt |> Option.iter (fun projectId ->
                lookupProjectById ctx projectId |> Option.iter (fun project ->
                    applyStringProp ctx "" root "author" (fun s -> project.Author <- s)
                    applyStringProp ctx "" root "version" (fun s -> project.Version <- s)))

            // systems 처리 (있으면)
            match tryProp root "systems" with
            | Some systemsEl ->
                collectSystems ctx systemsEl
                if not ctx.Diagnostics.HasErrors then
                    buildSystems ctx systemsEl
                    buildActiveFlows ctx systemsEl
            | None -> ()

            // patch 처리 (있으면)
            match tryProp root "patch" with
            | Some patchEl -> applyPatch ctx patchEl
            | None -> ()

            if ctx.Diagnostics.HasErrors then
                // 부분 성공 op 가 plan 에 누적된 상태 — 전체 rollback. refs 도 invalidate.
                plan.TruncateTo(snapshotCount)
                ctx.Diagnostics, Map.empty
            else
                let refs =
                    ctx.Systems
                    |> Seq.choose (fun kv ->
                        match kv.Value.SystemId.Value with
                        | Some id -> Some (kv.Key, id)
                        | None -> None)
                    |> Map.ofSeq
                ctx.Diagnostics, refs

    /// validate_model_doc 본체. dry-run — plan 은 별도 dummy builder, store 는 현재 그대로 사용.
    /// 호출자는 plan 결과를 *commit 하지 않음* (`store.ApplyImportPlan` 호출 안 함).
    /// 본 함수가 반환하는 시점에 plan instance 는 GC 대상 — 사이드이펙트 없음.
    /// 단 `apply` 의 forward-ref 해소 / device cascade 시뮬레이션은 *plan 안에서만* 일어남.
    let validate
        (store: DsStore)
        (root: JsonElement) : Diagnostics =

        let plan = ImportPlanBuilder()
        let diag, _ = apply plan store root
        diag

    // ─── export_model_doc — store → JsonElement ─────────────────────────────
    //
    // 현재 store 상태를 schema v0 의 JSON object 로 직렬화. round-trip 검증의 SSOT.
    // 본 PoC 는 단순 Active/Passive system 노출까지 — Flow / Work / Call 까지 1차 cycle.

    /// TimeSpan → SSOT §2.3 grammar 문자열 ("Nms" 또는 "Ns"). 정수 second 떨어지면 's', 아니면 'ms'.
    let private formatDuration (ts: TimeSpan) : string =
        let totalMs = ts.TotalMilliseconds
        if totalMs >= 1000. && totalMs % 1000. = 0. then
            sprintf "%ds" (int (totalMs / 1000.))
        else
            sprintf "%dms" (int totalMs)

    /// ArrowType enum → SSOT §2.4 type 이름 (Start/Reset/...). %A 의존 회피 (Major 3 review 정합).
    /// Phase 2.5 m7: 테스트 helper (ModelEquivalence) 도 같은 직렬화 사용 — public 노출.
    let formatArrowType (t: ArrowType) : string =
        match t with
        | ArrowType.Start -> "Start"
        | ArrowType.Reset -> "Reset"
        | ArrowType.StartReset -> "StartReset"
        | ArrowType.ResetReset -> "ResetReset"
        | ArrowType.Group -> "Group"
        | ArrowType.Unspecified -> "Unspecified"
        | other -> sprintf "Unknown(%d)" (int other)

    // ─── Enum format helpers (Phase 7 §4.2 C-1) — 위 parse* 함수의 거울 ────
    //
    // 각 enum 의 format 측. parse 측과 1:1 round-trip. unknown case 는 forensic
    // 단서로 `Unknown(<int>)` (formatArrowType 패턴 답습).

    let formatConditionType (t: ConditionType) : string =
        match t with
        | ConditionType.AutoAux -> "AutoAux"
        | ConditionType.ComAux -> "ComAux"
        | ConditionType.SkipAction -> "SkipAction"
        | other -> sprintf "Unknown(%d)" (int other)

    let formatContactKind (k: ContactKind) : string =
        match k with
        | ContactKind.NoContact -> "NoContact"
        | ContactKind.NcContact -> "NcContact"
        | ContactKind.RisingPulse -> "RisingPulse"
        | ContactKind.FallingPulse -> "FallingPulse"
        | ContactKind.Inverter -> "Inverter"
        | other -> sprintf "Unknown(%d)" (int other)

    let formatCallType (t: CallType) : string =
        match t with
        | CallType.WaitForCompletion -> "WaitForCompletion"
        | CallType.SkipIfCompleted -> "SkipIfCompleted"
        | other -> sprintf "Unknown(%d)" (int other)

    let formatTokenRole (t: TokenRole) : string =
        match t with
        | TokenRole.None -> "None"
        | TokenRole.Source -> "Source"
        | TokenRole.Ignore -> "Ignore"
        | TokenRole.Sink -> "Sink"
        | combined -> sprintf "Combined(%d)" (int combined)

    let formatActionType (a: ActionType) : string =
        match a with
        | ActionType.Real (Level,   None)             -> "normal"
        | ActionType.Real (OneShot, None)             -> "pulse"
        | ActionType.Real (Latched, None)             -> "set"
        | ActionType.Real (Level,   Some (Append ms)) -> sprintf "timeAppend(%d)" ms
        | ActionType.Real (OneShot, Some (Append ms)) -> sprintf "pulseHold(%d)" ms
        | ActionType.Real (Latched, Some (Append ms)) -> sprintf "Real(Latched, Append(%d))" ms
        | ActionType.Virtual None                     -> "virt"
        | ActionType.Virtual (Some (Append ms))       -> sprintf "virtPlus(%d)" ms

    let formatSensingType (s: SensingType) : string =
        match s with
        | SensingType.Real (Level,   None)             -> "normal"
        | SensingType.Real (OneShot, None)             -> "edge"
        | SensingType.Real (Latched, None)             -> "latched"
        | SensingType.Real (Level,   Some (Append ms)) -> sprintf "debounce(%d)" ms
        | SensingType.Real (OneShot, Some (Append ms)) -> sprintf "edgeStable(%d)" ms
        | SensingType.Real (Latched, Some (Append ms)) -> sprintf "Real(Latched, Append(%d))" ms
        | SensingType.Virtual None                     -> "virt"
        | SensingType.Virtual (Some (Append ms))       -> sprintf "virtPlus(%d)" ms

    // ─── Condition / ContactKind emit helpers (Phase 7 §4.2 C-3) ─────────
    //
    // dual format (§2.2.1) 의 emit 측 — store 값 inspection 후 default 인지 판단.
    // PoC 가정: Call.Conditions 는 multiple root 가능하나 *첫 root 만 emit*. 후속 phase 가 multiple root 정책 결정.

    /// IOTag 의 content 검사 — Name / Address 중 하나라도 non-empty 면 보강 대상.
    /// emit 측 가드 (`writeIOTag` 진입 차단) + `callHasEnhancement` IOTag 검사 양쪽에서 사용 — 빈 IOTag 인스턴스가
    /// `Some empty` ↔ `None` 비대칭으로 round-trip drift 발생하지 않도록 정합 (외부 reviewer M-B).
    let private ioTagHasContent (tag: IOTag) : bool =
        not (String.IsNullOrEmpty tag.Name) || not (String.IsNullOrEmpty tag.Address)

    /// leaves 기반 hasNonDefault — emit 측 `emitPlcLeaves` 의 skip 조건과 *정확히 동일* (#20 SSOT 통합).
    /// **invariant** (R2 외부 review): `plcHasNonDefault cp def leaves = true` ⟺ `emitPlcLeaves` 가
    /// 동일 인자로 1+ leaf 발행. leaves table 변경 시 두 helper 가 같은 leaves 를 dispatch 하므로 자동 보존.
    let private plcHasNonDefault (cp: 'cp) (defaultCp: 'cp) (leaves: PlcLeaf<'cp> list) : bool =
        leaves |> List.exists (fun leaf ->
            match leaf.Kind with
            | LBool        (g, _) -> g cp <> g defaultCp
            | LInt         (g, _) -> g cp <> g defaultCp
            | LFloat       (g, _) -> g cp <> g defaultCp
            | LString      (g, _) -> g cp <> g defaultCp
            | LStringOpt   (g, _) -> (g cp).IsSome && g cp <> g defaultCp
            | LIntOpt      (g, _) -> (g cp).IsSome && g cp <> g defaultCp
            | LFloatOpt    (g, _) -> (g cp).IsSome && g cp <> g defaultCp
            | LTimeSpan    (g, _) -> g cp <> g defaultCp
            | LTimeSpanOpt (g, _) -> (g cp).IsSome && g cp <> g defaultCp)

    /// Call 의 PLC metadata 가 non-default 인지 — dual format 분기 판단용 (callHasEnhancement 합성).
    let private plcCallHasNonDefault (c: Call) : bool =
        match c.GetControlProperties() with
        | None -> false
        | Some cp -> plcHasNonDefault cp (ControlCallProperties()) plcCallLeaves

    /// Phase 7 §10.2 #31 — level 인자 추가. Modeling level 시 B/C/D 보강은 제외하여 dual format
    /// object 승격을 차단 (B/C/D 만 있는 Call 은 string scalar 유지). A_Modeling 보강 (Condition /
    /// ContactKind / CallType) 만 승격 판단.
    let private callHasEnhancement (level: ExportLevel) (c: Call) : bool =
        let firstApiCall = if c.ApiCalls.Count > 0 then Some c.ApiCalls.[0] else None
        let exists pred = firstApiCall |> Option.exists pred
        let hasNonDefaultCallType =
            callTypeOf c |> Option.exists (fun ct -> ct <> CallType.WaitForCompletion)
        // 외부 reviewer M-B 반영: IOTag.IsSome 만으로는 부족 — content 검사 (Name/Address 중 하나라도 non-empty).
        // 빈 IOTag (Some empty) 가 emit 강제하면 parse 측 None 으로 정규화되어 비대칭 drift.
        let aLevel =
            c.Conditions.Count > 0
            || exists (fun ac -> ac.ContactKind <> ContactKind.NoContact)
            || hasNonDefaultCallType
        match level with
        | Modeling -> aLevel   // B/C/D 보강은 modeling 에서 emit 안 됨 → 승격 불요
        | Full ->
            aLevel
            || exists (fun ac -> ac.InTag |> Option.exists ioTagHasContent)
            || exists (fun ac -> ac.OutTag |> Option.exists ioTagHasContent)
            || plcCallHasNonDefault c

    /// IOTag emit — Name + Address 두 키만 (Phase 7 §4.2 C-4 PoC scope).
    /// DataType / Description / DefaultValue 등 IOTag 의 부속 property 는 후속 phase.
    /// 호출자는 `ioTagHasContent` 통과 후에만 진입 — 둘 다 빈 string 인 경우 emit 자체 skip.
    let private writeIOTag (w: Utf8JsonWriter) (key: string) (tag: IOTag) : unit =
        w.WritePropertyName key
        w.WriteStartObject()
        if not (String.IsNullOrEmpty tag.Name) then
            w.WriteString("name", tag.Name)
        if not (String.IsNullOrEmpty tag.Address) then
            w.WriteString("address", tag.Address)
        w.WriteEndObject()

    /// condition leaf 의 InputSpec → wire 키 emit (Phase 2; m1 검열 → 7-reviewer Critical round-trip 완전 해소).
    /// 호출자는 object scalar 안에서 진입.
    /// - UndefinedValue: 키 생략 (default — parse 측 entity-default 정합).
    /// - Single (bool): token 자체로 타입 확정 → `eq` scalar (hint 불요, 항상 round-trip 안전).
    /// - Single (string): 기존 정책 유지 → `eq` scalar (hint 불요, 항상 round-trip 안전).
    /// - Single (numeric, 정수/실수 전부): typed `inputSpec` raw DU (case 무손실). **옵션 (1) 확정** —
    ///   numeric `eq` 는 re-parse 시 *대상 ApiDef 타입 hint* (참조 Call.ApiCalls 의 InputSpec/OutputSpec
    ///   case) 가 있어야 폭/정수·실수를 복원할 수 있는데, 그 hint 출처는 정상 wire round-trip 경로에서
    ///   emit 되지 않아 (Call.ApiCalls InputSpec 은 wire 미직렬화) re-parse store 에서 항상 부재 → 어떤
    ///   numeric `eq` 도 re-parse 복원 불가 (round-trip 비대칭). emit store 에 일시적 hint 가 있어도
    ///   re-parse hint 는 구조적으로 None 이므로 동적 eq 는 round-trip 을 깨뜨린다. → numeric 은 일괄
    ///   `inputSpec` 으로 emit 해 round-trip 을 무조건 보장 (numeric eq sugar 는 의도적으로 미사용).
    /// - Multiple / Ranges: eq 로 환원 불가 → raw ValueSpec DU `inputSpec` fallback (박제 결정).
    let private writeLeafInputSpec (w: Utf8JsonWriter) (spec: ValueSpec) : unit =
        match spec with
        | UndefinedValue -> ()
        | BoolValue   (Single v) -> w.WriteBoolean("eq", v)        // token 확정 — 항상 eq.
        | StringValue (Single v) -> w.WriteString("eq", v)         // 기존 정책 — 항상 eq.
        | _ ->
            // numeric Single (정수/실수 전부) / Multiple / Ranges — raw DU inputSpec (case 무손실, round-trip 보장).
            w.WritePropertyName "inputSpec"
            JsonSerializer.Serialize(w, spec, valueSpecJsonOptions)

    /// condition leaf 가 object 승격 대상인지 — ContactKind non-default 또는 InputSpec non-Undefined.
    let private leafNeedsObject (ac: ApiCall) : bool =
        ac.ContactKind <> ContactKind.NoContact || ac.InputSpec <> UndefinedValue

    /// Condition tree recursive emit. `apiCallRef` 람다: ApiCall → "<System>.<ApiDef>" path 도출
    /// (caller 가 store 컨텍스트 제공).
    /// conditions leaf 는 ContactKind default + InputSpec Undefined 면 string scalar, 아니면 object
    /// (ref + contactKind? + eq?/inputSpec?).
    let rec private emitCondition
        (w: Utf8JsonWriter)
        (apiCallRef: ApiCall -> string)
        (cond: Condition) : unit =
        w.WriteStartObject()
        // type — AutoAux default 면 생략
        (match cond.Type with
         | Some t when t <> ConditionType.AutoAux ->
             w.WriteString("type", formatConditionType t)
         | _ -> ())
        if cond.IsOR then w.WriteBoolean("isOR", true)
        if cond.IsInverted then w.WriteBoolean("isInverted", true)
        if cond.ApiCalls.Count > 0 then
            w.WritePropertyName "conditions"
            w.WriteStartArray()
            for ac in cond.ApiCalls do
                let leafRef = apiCallRef ac
                if leafNeedsObject ac then
                    w.WriteStartObject()
                    w.WriteString("ref", leafRef)
                    if ac.ContactKind <> ContactKind.NoContact then
                        w.WriteString("contactKind", formatContactKind ac.ContactKind)
                    writeLeafInputSpec w ac.InputSpec
                    w.WriteEndObject()
                else
                    w.WriteStringValue(leafRef)
            w.WriteEndArray()
        if cond.Children.Count > 0 then
            w.WritePropertyName "children"
            w.WriteStartArray()
            for child in cond.Children do
                emitCondition w apiCallRef child
            w.WriteEndArray()
        w.WriteEndObject()

    // ─── Multi-root condition emit (Phase 3 — done-refactor-condition.md §남은작업 4) ──
    //
    // SSOT 박제 결정: "같은 ConditionType 의 여러 top-level root 는 implicit AND".
    // Runtime (SimIndex/Build.fs `buildConditionExpression`) 은 `Conditions` 에서
    // `Type = Some conditionType` 인 root 만 필터링해 각 root 를 `convertOne` 한 뒤 `And [...]`
    // 로 결합한다. 따라서 wire emit 은 *같은 타입* root 들을 AND 로 보존해야 한다.
    //
    // **기존 버그**: emit 이 `Conditions.[0]` 만 내보내 같은 타입 root 가 2개 이상이면 data loss.
    //
    // **전략** (의미 보존 최우선):
    //   * Runtime 이 평가하는 root 는 `Type = Some _` 인 것뿐 (Type=None root 는 어떤 타입
    //     필터에도 안 걸려 inert). emit 대상 root 도 동일 기준으로 그룹화한다.
    //   * 단일 root 1개만 있으면 그 root 를 그대로 emit (기존 동작 100% 보존 — 회귀 0).
    //     (Type=None 단일 root 도 이 경로 — 기존처럼 type 키 없이 emit → top-level Call apply 시 AutoAux 보정.)
    //   * 같은 타입 root 가 2개 이상이면 그 root 들을 `Children` 으로 갖는 단일 wrapper
    //     Condition (Type=그 타입, IsOR=false, IsInverted=false, ApiCalls 비어있음) 으로 묶어 emit.
    //     apply 측 parseCondition 이 wrapper 를 1개 root (Type=그 타입, Children=[roots]) 로 복원하고,
    //     Runtime convertOne(wrapper) = And (빈 leaf @ [convertOne r1; convertOne r2; ...])
    //                                 = And [convertOne r1; convertOne r2; ...] (원본과 정확히 동등).
    //   * 서로 다른 타입 root 가 동시에 존재하면 wire 의 condition 키 1개로는 1개 type 만 표현 가능
    //     (condition object 는 단일 type). 이는 정상 wire round-trip 경로 (parse 가 entity 당 1 root 만
    //     생성) 에서는 발생하지 않으나, Editor 등 다른 경로로 store 에 혼재할 수 있다. export 경로엔
    //     Diagnostics 객체가 없으므로 (`exportToJsonWithLevel` 미수신) 기존 export 측 `log.Warn` 패턴으로
    //     forensic 단서를 남기고, 첫 등장 type 그룹을 보존해 emit 한다 (silent drop 회피 — SSOT 정책 정합).

    /// `Conditions` 컬렉션에서 emit 할 단일 Condition 을 SSOT 박제 결정 (같은 타입 implicit AND) 에
    /// 맞춰 산출. emit 대상 root 가 없으면 None.
    /// `entityRefForLog`: 다중 타입 혼재 forensic 로그용 entity 식별자.
    let private selectConditionRootForEmit
        (conditions: ResizeArray<Condition>)
        (entityRefForLog: string) : Condition option =
        // Runtime 평가 대상 = Type = Some _ 인 root. 등장 순서 보존하며 type 별 그룹화.
        let typed =
            conditions
            |> Seq.choose (fun c -> match c.Type with Some t -> Some (t, c) | None -> None)
            |> Seq.toList
        match typed with
        | [] ->
            // typed root 0건. 단, 기존 동작 보존: Type=None 단일 root 만 있는 (legacy / 미보정) 케이스는
            // 첫 root 를 그대로 emit (apply 측 top-level Call AutoAux 보정에 의존하던 기존 round-trip 보존).
            if conditions.Count > 0 then Some conditions.[0] else None
        | _ ->
            // type 별 그룹화 — 등장 순서 유지 (List.groupBy 가 첫 등장 순서로 key 정렬).
            let groups = typed |> List.groupBy fst
            let chosenType, chosenPairs =
                match groups with
                | [ single ] -> single
                | first :: _ ->
                    // 다중 타입 혼재 — wire condition 1개로 1 type 만 표현 가능. 첫 type 그룹 보존 + forensic 로그.
                    let lostTypes =
                        groups |> List.skip 1 |> List.map (fst >> formatConditionType) |> String.concat ", "
                    log.Warn(
                        sprintf "[exportToJson] %s: 서로 다른 ConditionType 의 top-level root 혼재 — wire condition 은 단일 type 만 표현 가능. 첫 type '%s' 보존, '%s' 누락 (Editor 경로 등에서 생성된 혼재 store)."
                            entityRefForLog (formatConditionType (fst first)) lostTypes)
                    first
                | [] -> failwith "unreachable: typed 가 비어있지 않으므로 groups 도 비어있지 않음."
            let roots = chosenPairs |> List.map snd
            match roots with
            | [ single ] ->
                // 같은 타입 root 1개 — 기존 동작 보존 (wrapper 합성 없이 그대로 emit).
                Some single
            | _ ->
                // 같은 타입 root 2개 이상 — AND wrapper (Children=roots) 로 묶어 의미 보존.
                let wrapper = Condition(Type = Some chosenType)
                for r in roots do wrapper.Children.Add(r)
                Some wrapper

    /// `Conditions` → SSOT 박제 결정에 맞춘 단일 condition root 를 `keyName` 키로 emit.
    /// emit 대상 root 가 없으면 키 자체를 발행하지 않는다 (WritePropertyName 후 값 누락 = invalid JSON 회피).
    let private emitConditionRoots
        (w: Utf8JsonWriter)
        (keyName: string)
        (apiCallRef: ApiCall -> string)
        (conditions: ResizeArray<Condition>)
        (entityRefForLog: string) : unit =
        match selectConditionRootForEmit conditions entityRefForLog with
        | Some cond ->
            w.WritePropertyName keyName
            emitCondition w apiCallRef cond
        | None -> ()

    // ─── PLC metadata emit (Phase 7 §4.2 C-7.1) ─────────────────────────────
    //
    // SSOT §2.2.2 (C-7.1): entity 안 `plc:` sub-section. ControlSystemProperties /
    // ControlFlowProperties / ControlWorkProperties / ControlCallProperties 의 단순 leaf
    // scalar 만 emit (복합 collection 은 C-7.2 후속 phase).
    //
    // **Default 정책**: type-default (= 빈 생성자 호출 결과) 와 다른 값만 emit.
    // 모든 leaf default 면 plc 키 자체 생략 — dual format §4.1.5 옵션 C 정합.
    //
    // **TimeSpan**: `ToString("c")` = `[-][d.]hh:mm:ss[.fffffff]`. ms 표현 가능.
    //
    // **Runtime-only 제외** (派 분류): CurrentState / LastExecutionTime / ExecutionCount /
    // ErrorCount (Work) — 시뮬 lossy 4-set 유사. emit 안 함.
    //
    // **call dual format**: Call 의 plc 변경 시 `callHasEnhancement` true → object 승격.

    /// emit generic — leaves table 순회 + type-default 비교. 모든 leaf default 면 `plc:` 키 미 emit.
    let private emitPlcLeaves
        (w: Utf8JsonWriter) (cp: 'cp) (defaultCp: 'cp) (leaves: PlcLeaf<'cp> list) : unit =
        let mutable opened = false
        let openIfNeeded () =
            if not opened then
                w.WritePropertyName "plc"
                w.WriteStartObject()
                opened <- true
        for leaf in leaves do
            match leaf.Kind with
            | LBool (g, _) ->
                let c = g cp in if c <> g defaultCp then openIfNeeded(); w.WriteBoolean(leaf.Key, c)
            | LInt (g, _) ->
                let c = g cp in if c <> g defaultCp then openIfNeeded(); w.WriteNumber(leaf.Key, c)
            | LFloat (g, _) ->
                let c = g cp in if c <> g defaultCp then openIfNeeded(); w.WriteNumber(leaf.Key, c)
            | LString (g, _) ->
                let c = g cp in if c <> g defaultCp then openIfNeeded(); w.WriteString(leaf.Key, c)
            | LStringOpt (g, _) ->
                match g cp with
                | Some s when g cp <> g defaultCp -> openIfNeeded(); w.WriteString(leaf.Key, s)
                | _ -> ()
            | LIntOpt (g, _) ->
                match g cp with
                | Some n when g cp <> g defaultCp -> openIfNeeded(); w.WriteNumber(leaf.Key, n)
                | _ -> ()
            | LFloatOpt (g, _) ->
                match g cp with
                | Some n when g cp <> g defaultCp -> openIfNeeded(); w.WriteNumber(leaf.Key, n)
                | _ -> ()
            | LTimeSpan (g, _) ->
                let c = g cp in if c <> g defaultCp then openIfNeeded(); w.WriteString(leaf.Key, c.ToString("c"))
            | LTimeSpanOpt (g, _) ->
                match g cp with
                | Some s when g cp <> g defaultCp -> openIfNeeded(); w.WriteString(leaf.Key, s.ToString("c"))
                | _ -> ()
        if opened then w.WriteEndObject()

    let private emitPlcSystem (w: Utf8JsonWriter) (cp: ControlSystemProperties) : unit =
        emitPlcLeaves w cp (ControlSystemProperties()) plcSystemLeaves

    let private emitPlcFlow (w: Utf8JsonWriter) (cp: ControlFlowProperties) : unit =
        emitPlcLeaves w cp (ControlFlowProperties()) plcFlowLeaves

    let private emitPlcWork (w: Utf8JsonWriter) (cp: ControlWorkProperties) : unit =
        emitPlcLeaves w cp (ControlWorkProperties()) plcWorkLeaves

    let private emitPlcCall (w: Utf8JsonWriter) (cp: ControlCallProperties) : unit =
        emitPlcLeaves w cp (ControlCallProperties()) plcCallLeaves

    /// Passive system 의 internal Flow 의 ResetReset arrow 갯수 → opposing 추정.
    /// chain: N-1 / all-pairs: N*(N-1)/2 / none: 0
    let private inferOpposing (apiCount: int) (resetResetCount: int) : string =
        if apiCount <= 1 || resetResetCount = 0 then "none"
        elif resetResetCount = apiCount - 1 then "chain"
        elif resetResetCount = apiCount * (apiCount - 1) / 2 then "all-pairs"
        else "none"  // unknown shape — conservative

    /// Phase 7 §10.2 #31 — `level` 인자 추가 entry. 기존 `exportToJson` 은 thin wrapper.
    /// Modeling level 시 A_Modeling 만 emit (B/C/D + workDuration + apiDetails.description 생략)
    /// + wire 에 `level: modeling` 키 추가 (Full 시 키 부재 — wire payload 최소화 / 기존 호환).
    let exportToJsonWithLevel (store: DsStore) (level: ExportLevel) : JsonDocument =
        let projects = Queries.allProjects store
        let ms = new MemoryStream()
        do
            use w = new Utf8JsonWriter(ms)
            w.WriteStartObject()
            w.WriteString("protocol", "promaker/v0")
            // SSOT §2.8 — 전체 export 는 항상 view: full. partial 변형은 별도 함수 (Phase 6 후속 commit).
            w.WriteString("view", "full")
            // Phase 7 §10.2 #31 (S1.1) — level 키는 Modeling 일 때만 emit (Full = default 라 생략).
            // wire 의 `level: modeling` flag = self-tagged — apply 측이 modeling-mode 진입 강제.
            if level = Modeling then
                w.WriteString("level", formatLevel level)
            match projects with
            | [] -> ()
            | p :: _ ->
                w.WriteString("project", p.Name)
                // Phase 7 §4.2 C-6: Project root-level meta (default "" / "1.0.0" 면 생략).
                // #7 옵션 a — Version default 비교는 entity SSOT (`defaultProjectVersion`) 사용.
                // #31 — author/version 은 C_Meta — Modeling 시 emit 생략.
                if isEmittedIn level C_Meta then
                    if not (String.IsNullOrEmpty p.Author) then
                        w.WriteString("author", p.Author)
                    if not (String.IsNullOrEmpty p.Version) && p.Version <> defaultProjectVersion then
                        w.WriteString("version", p.Version)
                w.WriteStartArray("systems")

                let actives = Queries.activeSystemsOf p.Id store
                let passives = Queries.passiveSystemsOf p.Id store

                for s in actives do
                    w.WriteStartObject()
                    w.WriteString("system", s.Name)
                    w.WriteString("kind", "active")
                    // Phase 7 §4.2 C-6: DsSystem.IRI (Some non-empty 인 경우만 emit — #16 Some "" 가드)
                    // #31 — iri 는 C_Meta — Modeling 시 emit 생략.
                    if isEmittedIn level C_Meta then
                        s.IRI
                        |> Option.filter (not << String.IsNullOrEmpty)
                        |> Option.iter (fun iri -> w.WriteString("iri", iri))
                    // Phase 7 §4.2 C-7.1: ControlSystemProperties plc 키 (Active System)
                    // #31 — plc 는 D_Plc — Modeling 시 emit 생략.
                    if isEmittedIn level D_Plc then
                        s.GetControlProperties() |> Option.iter (emitPlcSystem w)

                    let flows = Queries.flowsOf s.Id store

                    // ── flows: mapping (D5) — flow 를 key 로, flow 별 속성(plc) 가능. 없으면 빈 객체 {} ──
                    if not flows.IsEmpty then
                        w.WritePropertyName "flows"
                        w.WriteStartObject()
                        for f in flows do
                            w.WritePropertyName f.Name
                            w.WriteStartObject()
                            // Phase 7 §4.2 C-7.1: ControlFlowProperties plc 키 — #31 D_Plc
                            if isEmittedIn level D_Plc then
                                f.GetControlProperties() |> Option.iter (emitPlcFlow w)
                            w.WriteEndObject()
                        w.WriteEndObject()

                    // ── works: system 직속 mapping (D6) — work 이름 = system-unique key, flow: 속성으로 소속 명시 ──
                    // 공용 apiCallRef (Work.Conditions / Call.Conditions / leaf 도출 모두 동일 — 중복 helper 제거).
                    // ApiDefId/getApiDef/getSystem 어느 단계든 실패 시 ApiCall.Name fallback (데이터 무결성 깨진 케이스).
                    let workApiCallRef (ac: ApiCall) : string =
                        match ac.ApiDefId with
                        | Some apiDefId ->
                            match Queries.getApiDef apiDefId store with
                            | Some apiDef ->
                                match Queries.getSystem apiDef.ParentId store with
                                | Some sys -> sprintf "%s.%s" sys.Name apiDef.Name
                                | None -> ac.Name
                            | None -> ac.Name
                        | None -> ac.Name
                    // 전 flow 의 work 를 평탄 수집 ((flow, work) pair). work 이름 = mapping key.
                    let allWorks =
                        flows |> List.collect (fun f -> Queries.worksOf f.Id store |> List.map (fun wk -> f, wk))
                    if not allWorks.IsEmpty then
                        w.WritePropertyName "works"
                        w.WriteStartObject()
                        // D7 — system 내 동명 work 발견 → 즉시 exception. flat works mapping 은 system-unique key 라
                        // JSON key 충돌 시 silent overwrite (data loss) 발생 → fail-fast 로 차단 (자동 rename 금지).
                        let seenWorkNames = HashSet<string>(StringComparer.Ordinal)
                        for (f, wk) in allWorks do
                            if not (seenWorkNames.Add wk.LocalName) then
                                invalidOp (sprintf "Export 실패: active system '%s' 안에 work 이름 '%s' 가 중복되어 system-unique works mapping 으로 직렬화할 수 없습니다 (work 이름은 system-unique — D7). work 이름을 고유하게 변경 후 다시 export 하세요." s.Name wk.LocalName)
                            w.WritePropertyName wk.LocalName
                            w.WriteStartObject()
                            // flow: 소속 flow (D6) — Work.ParentId=FlowId 복원에 필요. 항상 emit.
                            w.WriteString("flow", f.Name)
                            // Phase 7 §4.2 C-6: Work.TokenRole (default None 면 생략) — #31 A_Modeling (그대로 emit)
                            if wk.TokenRole <> TokenRole.None then
                                w.WriteString("tokenRole", formatTokenRole wk.TokenRole)
                            // Phase 7 §4.2 C-7.1: ControlWorkProperties plc 키 — #31 D_Plc
                            if isEmittedIn level D_Plc then
                                wk.GetControlProperties() |> Option.iter (emitPlcWork w)
                            // Work.Conditions (SkipAction 등) — 같은 ConditionType 의 모든 top-level root 를
                            // implicit AND 로 보존해 emit (Phase 3). condition 키 이름은 helper 가 발행.
                            emitConditionRoots w "condition" workApiCallRef wk.Conditions (sprintf "work '%s'" wk.LocalName)
                            let calls = Queries.callsOf wk.Id store
                            if not calls.IsEmpty then
                                w.WritePropertyName "calls"
                                w.WriteStartArray()
                                for c in calls do
                                    // SSOT §1.7: Call 참조는 DevicesAlias 가 아닌 *Passive system 이름* 으로 emit.
                                    // ApiDef.ParentId → system.Name 으로 정정. GUI 사용자가 부여한 alias 는
                                    // doc-level 추상화에서 무시.
                                    //
                                    // *invariant 가정* (M1, 자가 검열): Call.ApiCalls 는 본 PoC scope (cylinder/clamp/
                                    // robot sugar) 에서 1:1 매핑 — `Seq.tryHead` 로 canonical ApiDef 식별. multi-entry
                                    // 케이스 (Paste.DeviceOps 등) 가 들어와도 첫 항목 = 정답으로 가정.
                                    //
                                    // *fallback* (M2, 외부 review 적용): 다음 4 케이스에서 alias 그대로 emit (= 기존 동작):
                                    // (a) ApiCalls 빈 list / (b) ApiDefId None / (c) getApiDef None / (d) getSystem None.
                                    // 모두 데이터 무결성 깨진 상태 — fallback 유지 + logWarn 으로 forensic 단서 남김.
                                    let resolved =
                                        Queries.tryResolveCallTargetSystem c store
                                        |> Option.map (fun sys -> sys.Name)
                                    let sysName =
                                        match resolved with
                                        | Some n -> n
                                        | None ->
                                            log.Warn(sprintf "[exportToJson] call '%s.%s' systemName resolution 실패 — DevicesAlias fallback" c.DevicesAlias c.ApiName)
                                            c.DevicesAlias
                                    let callRef = sprintf "%s.%s" sysName c.ApiName
                                    // Phase 7 §4.1.5 dual format — enhancement 없으면 string scalar (legacy 동일).
                                    // 있으면 object 승격 + 보강 property (현 phase: contactKind / condition).
                                    // #31 — callHasEnhancement level 인자로 modeling 시 B/C/D 무시 (string scalar 유지).
                                    if callHasEnhancement level c then
                                        w.WriteStartObject()
                                        w.WriteString("ref", callRef)
                                        if c.ApiCalls.Count > 0 then
                                            let ac = c.ApiCalls.[0]
                                            // A_Modeling (그대로 emit)
                                            if ac.ContactKind <> ContactKind.NoContact then
                                                w.WriteString("contactKind", formatContactKind ac.ContactKind)
                                            // #31 — inTag/outTag 는 B_Addressing — Modeling 시 생략.
                                            // 외부 reviewer M-B: 빈 IOTag (Some empty) 는 emit 자체 skip
                                            if isEmittedIn level B_Addressing then
                                                ac.InTag
                                                |> Option.filter ioTagHasContent
                                                |> Option.iter (writeIOTag w "inTag")
                                                ac.OutTag
                                                |> Option.filter ioTagHasContent
                                                |> Option.iter (writeIOTag w "outTag")
                                        // C-5: SimulationCallProperties.CallType (default WaitForCompletion 면 생략) — A_Modeling
                                        match callTypeOf c with
                                        | Some ct when ct <> CallType.WaitForCompletion ->
                                            w.WriteString("callType", formatCallType ct)
                                        | _ -> ()
                                        // A_Modeling (그대로 emit) — 같은 ConditionType 의 모든 top-level root 를
                                        // implicit AND 로 보존해 emit (Phase 3). condition 키 이름은 helper 가 발행.
                                        emitConditionRoots w "condition" workApiCallRef c.Conditions (sprintf "call '%s'" callRef)
                                        // Phase 7 §4.2 C-7.1: ControlCallProperties plc 키 — #31 D_Plc
                                        if isEmittedIn level D_Plc then
                                            c.GetControlProperties() |> Option.iter (emitPlcCall w)
                                        w.WriteEndObject()
                                    else
                                        w.WriteStringValue(callRef)
                                w.WriteEndArray()
                            // arrows (Work 안 — ArrowBetweenCalls). round-trip 정합: apply 측 callIdMap 키
                            // (`sysName.apiName`) 와 동일한 normalized 표현 사용 → load 시 resolveCallId 매칭 보장.
                            let callArrows = Queries.arrowCallsOf wk.Id store
                            if not callArrows.IsEmpty then
                                let toCallRef (c: Call) =
                                    let sysName =
                                        Queries.tryResolveCallTargetSystem c store
                                        |> Option.map (fun sys -> sys.Name)
                                        |> Option.defaultValue c.DevicesAlias
                                    sprintf "%s.%s" sysName c.ApiName
                                w.WritePropertyName "arrows"
                                w.WriteStartArray()
                                for a in callArrows do
                                    match Queries.getCall a.SourceId store, Queries.getCall a.TargetId store with
                                    | Some sc, Some tc ->
                                        w.WriteStringValue(
                                            sprintf "%s -> %s : %s"
                                                (toCallRef sc) (toCallRef tc) (formatArrowType a.ArrowType))
                                    | _ ->
                                        log.Warn(sprintf "[exportToJson] ArrowBetweenCalls %O source/target Call resolution 실패 — emit 누락" a)
                                w.WriteEndArray()
                            // Active Work duration override (default 500ms 와 다른 경우만 emit)
                            // #31 — workDuration 는 C_Meta (사용자 결정 — modeling 제외)
                            if isEmittedIn level C_Meta then
                                match wk.Duration with
                                | Some d when d <> TimeSpan.FromMilliseconds 500. ->
                                    w.WriteString("workDuration", formatDuration d)
                                | _ -> ()
                            w.WriteEndObject()
                        w.WriteEndObject()

                    // ── arrows: system 직속 (work 간 arrow, bare 표기 — D2) ──
                    // ArrowBetweenWorks.ParentId = SystemId 와 1:1 — source-flow 배분 없이 system 전체를 평탄 emit.
                    let workArrows = Queries.arrowWorksOf s.Id store
                    if not workArrows.IsEmpty then
                        w.WritePropertyName "arrows"
                        w.WriteStartArray()
                        for a in workArrows do
                            match Queries.getWork a.SourceId store, Queries.getWork a.TargetId store with
                            | Some sw, Some tw ->
                                w.WriteStringValue(sprintf "%s -> %s : %s" sw.LocalName tw.LocalName (formatArrowType a.ArrowType))
                            | _ -> ()
                        w.WriteEndArray()
                    w.WriteEndObject()

                for s in passives do
                    w.WriteStartObject()
                    w.WriteString("system", s.Name)
                    w.WriteString("kind", "passive")
                    // Phase 7 §4.2 C-6: DsSystem.IRI (Some non-empty 인 경우만 emit — #16 Some "" 가드)
                    // #31 — iri 는 C_Meta — Modeling 시 emit 생략.
                    if isEmittedIn level C_Meta then
                        s.IRI
                        |> Option.filter (not << String.IsNullOrEmpty)
                        |> Option.iter (fun iri -> w.WriteString("iri", iri))
                    // Phase 7 §4.2 C-7.1: ControlSystemProperties plc 키 (Passive System) — #31 D_Plc
                    if isEmittedIn level D_Plc then
                        s.GetControlProperties() |> Option.iter (emitPlcSystem w)
                    let apis = Queries.apiDefsOf s.Id store |> List.map (fun d -> d.Name)
                    // device 추정 (Phase 2 §3.1 #5) — SystemType + apis 패턴 fingerprint 매칭.
                    // sugar fingerprint:
                    //   - cylinder: SystemType="Unit" + apis={ADV, RET}
                    //   - clamp:    SystemType="Unit" + apis={CLP, UNCLP}
                    //   - robot:    SystemType="Robot" + apis 명시
                    // mismatch 시 custom(<SystemType>) + apis 명시 long-form.
                    // SystemType=None (비정상 store) → fail-safe custom(Unknown) + apis.
                    //
                    // workDuration / opposing override 는 sugar short-form 위에 *키로 적용* —
                    // round-trip 시 cylinder cascade + override 로 매핑 정합 보장.
                    // Phase 2.5 M4: `KnownSugars.tryMatchFingerprint` SSOT 표 lookup 으로 통합.
                    // 매칭 없으면 SystemType 별 custom 분기 — None 은 fail-safe custom(Unknown) + logWarn.
                    // Phase 2.5 cycle2 C1 (5인 review): defaultOpposing 도 spec.DefaultOpposing 직접 사용 — SSOT 통합 완성.
                    // custom fallback (매칭 실패) 의 opposing default = "none" (sugar 미적용 시 보수적 추정).
                    let deviceCase, emitApisAlways, defaultOpp =
                        match s.SystemType with
                        | Some st ->
                            match KnownSugars.tryMatchFingerprint st apis with
                            | Some spec -> spec.DeviceCase, spec.EmitApisAlways, spec.DefaultOpposing
                            | None -> sprintf "custom(%s)" st, true, "none"
                        | None ->
                            // M1 (외부 review): SystemType=None 은 비정상 store — fail-safe custom(Unknown).
                            // round-trip 시 Custom "Unknown" 으로 굳어 silent type mutation 가능 — forensic 단서로 logWarn.
                            log.Warn(sprintf "[exportToJson] Passive system '%s' SystemType=None — custom(Unknown) fallback. round-trip 시 SystemType 이 'Unknown' 으로 굳음." s.Name)
                            "custom(Unknown)", true, "none"
                    w.WriteString("device", deviceCase)
                    if emitApisAlways then
                        w.WritePropertyName "apis"
                        w.WriteStartArray()
                        for a in apis do w.WriteStringValue a
                        w.WriteEndArray()
                    // **Major 2 (review)**: workDuration / opposing override emit — round-trip 보장.
                    // workDuration: passive 내부 Flow 의 첫 Work duration 이 default (500ms) 와 다르면 emit.
                    // *가정* (W1): sugar (queueAddCylinder/Clamp/Robot/Device) 가 모든 internal Work 를 *동일 duration* 으로 생성.
                    // 첫 Work duration 만 대표값으로 사용. 후속 cycle 에서 sugar 가 Work 별 다른 duration 을 만드는 케이스
                    // 도입 시 본 가정 깨짐 — emit 정책 재검토 필요.
                    let internalFlow =
                        Queries.flowsOf s.Id store
                        |> List.tryHead
                    let firstWorkDur =
                        internalFlow
                        |> Option.bind (fun f -> Queries.worksOf f.Id store |> List.tryHead)
                        |> Option.bind (fun w -> w.Duration)
                    // #31 — workDuration 는 C_Meta (사용자 결정 — modeling 제외)
                    if isEmittedIn level C_Meta then
                        match firstWorkDur with
                        | Some d when d <> TimeSpan.FromMilliseconds 500. ->
                            w.WriteString("workDuration", formatDuration d)
                        | _ -> ()
                    // opposing: 내부 Flow 의 ResetReset arrow 갯수 → 추정 → device default 와 다르면 emit.
                    let resetResetCount =
                        if internalFlow.IsSome then
                            Queries.arrowWorksOf s.Id store
                            |> List.filter (fun a -> a.ArrowType = ArrowType.ResetReset)
                            |> List.length
                        else 0
                    let inferredOpp = inferOpposing apis.Length resetResetCount
                    if inferredOpp <> defaultOpp then
                        w.WriteString("opposing", inferredOpp)
                    // C-5: apiDetails — ApiDef 별 actionType / description (default 면 키 자체 생략).
                    // #31 — actionType 은 A_Modeling, description 은 C_Meta (사용자 결정).
                    // Modeling 시 description 키 제외 + description 만 있는 entry 자체 skip.
                    let emitDescription = isEmittedIn level C_Meta
                    let apiDefEntities = Queries.apiDefsOf s.Id store
                    let detailsEntries =
                        apiDefEntities
                        |> List.choose (fun ad ->
                            let defaultAction = ActionType.Real (Level, None)
                            let defaultSensing = SensingType.Real (Level, None)
                            let hasNonDefaultAction = ad.ActionType <> defaultAction
                            let hasNonDefaultSensing = ad.SensingType <> defaultSensing
                            let hasDescription =
                                emitDescription
                                && ad.Description.IsSome
                                && not (String.IsNullOrEmpty ad.Description.Value)
                            if hasNonDefaultAction || hasNonDefaultSensing || hasDescription then Some ad else None)
                    if not detailsEntries.IsEmpty then
                        w.WritePropertyName "apiDetails"
                        w.WriteStartObject()
                        for ad in detailsEntries do
                            let defaultAction = ActionType.Real (Level, None)
                            let defaultSensing = SensingType.Real (Level, None)
                            w.WritePropertyName ad.Name
                            w.WriteStartObject()
                            if ad.ActionType <> defaultAction then
                                w.WriteString("actionType", formatActionType ad.ActionType)
                            if ad.SensingType <> defaultSensing then
                                w.WriteString("sensingType", formatSensingType ad.SensingType)
                            if emitDescription then
                                (match ad.Description with
                                 | Some s when not (String.IsNullOrEmpty s) ->
                                     w.WriteString("description", s)
                                 | _ -> ())
                            w.WriteEndObject()
                        w.WriteEndObject()
                    w.WriteEndObject()

                w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
        ms.Position <- 0L
        JsonDocument.Parse(ms.ToArray())

    /// Phase 7 §10.2 #31 — 후방 호환 wrapper. 기존 호출처 (테스트 / YamlIO / Mermaid / ModelTools 등)
    /// 시그니처 변경 없이 `Full` 으로 delegate. 새 호출처는 `exportToJsonWithLevel` 또는
    /// `exportToJsonScoped` (level 인자) 직접 호출.
    let exportToJson (store: DsStore) : JsonDocument =
        exportToJsonWithLevel store Full

    // ─── exportToJsonScoped (Phase 6 chunk-1c) ───────────────────────────────
    //
    // SSOT `yaml-protocol-v0.md §2.8` — partial export view-only spec.
    // 일소된 list_projects / list_systems / describe_system / describe_subtree 흡수.
    // `Apps/Promaker/Docs/done-read-surface-guid-cleanup.md` §3.1 / §4.1 / §4.7 / closure #2/#4 정합.

    /// envelope 의 `view` 키 갱신. 모든 단계 끝 `truncated` 상태에 따라 partial/full 결정.
    let private setView (root: JsonObject) (view: string) : unit =
        root.["view"] <- JsonValue.Create(view)

    /// system entry (JsonObject) 가 active 인지 — `kind: active` literal lookup.
    let private isActiveSystem (sysObj: JsonObject) : bool =
        match sysObj.TryGetPropertyValue("kind") with
        | true, kv when kv <> null -> kv.ToString() = "active"
        | _ -> false

    /// path scope 적용 — root 의 systems[] 와 안쪽 flow*/works/calls/apis 를 segments 별 필터.
    /// segs[0]=project, [1]=system, [2]=flow|apidef, [3]=work, [4]=call. 매칭 외 요소 제거 + truncated set.
    let private applyPathScope (root: JsonObject) (segs: string list) (truncated: bool ref) : unit =
        match segs with
        | [] -> ()
        | _ ->
            // segs[0] = project — root.project 와 mismatch 면 모든 systems 제거 (현 single-project export)
            let rootProj =
                match root.TryGetPropertyValue("project") with
                | true, v when v <> null -> v.ToString()
                | _ -> ""
            if segs.[0] <> rootProj then
                // path 가 다른 project — systems 비우고 project 키도 정합 위해 path 의 project 로 교체
                truncated := true
                root.["project"] <- JsonValue.Create(segs.[0])
                root.["systems"] <- JsonArray()
            else
                match root.TryGetPropertyValue("systems") with
                | true, (:? JsonArray as systemsArr) when segs.Length >= 2 ->
                    // segs[1] = system 이름 — 그 외 모두 제거
                    let kept = ResizeArray<JsonNode>()
                    let mutable removedAny = false
                    let original = systemsArr |> Seq.toArray
                    for node in original do
                        match node with
                        | :? JsonObject as sysObj ->
                            let name =
                                match sysObj.TryGetPropertyValue("system") with
                                | true, v when v <> null -> v.ToString()
                                | _ -> ""
                            if name = segs.[1] then
                                kept.Add(sysObj)
                            else
                                removedAny <- true
                        | _ -> ()
                    if removedAny then truncated := true
                    systemsArr.Clear()
                    for k in kept do
                        // detach from any parent then re-add (JsonNode requires reparenting)
                        let raw = k.ToJsonString()
                        systemsArr.Add(JsonNode.Parse(raw))

                    // 3+ segment — 안쪽 필터
                    if segs.Length >= 3 && systemsArr.Count = 1 then
                        match systemsArr.[0] with
                        | :? JsonObject as sysObj ->
                            let activeSys = isActiveSystem sysObj
                            if activeSys then
                                // 새 구조: flows: mapping / works: system 직속(flow: 속성) / arrows: system 직속(work 간).
                                // segs[2] = flow 이름. segs[3] = work (work scope). segs[4] = call.
                                let targetFlow = segs.[2]
                                // 1) flows: — targetFlow 외 제거
                                match sysObj.TryGetPropertyValue("flows") with
                                | true, (:? JsonObject as flowsObj) ->
                                    let toRemove =
                                        flowsObj |> Seq.filter (fun kv -> kv.Key <> targetFlow)
                                        |> Seq.map (fun kv -> kv.Key) |> Seq.toList
                                    for k in toRemove do
                                        flowsObj.Remove(k) |> ignore
                                        truncated := true
                                | _ -> ()
                                // 2) works: — work scope (segs[3]) 면 그 work 만, flow scope 면 targetFlow 소속 work 만 유지
                                let keptWorks = HashSet<string>(StringComparer.Ordinal)
                                match sysObj.TryGetPropertyValue("works") with
                                | true, (:? JsonObject as worksObj) ->
                                    let workKeys = worksObj |> Seq.map (fun kv -> kv.Key) |> Seq.toList
                                    for wk in workKeys do
                                        let workFlow =
                                            match worksObj.TryGetPropertyValue(wk) with
                                            | true, (:? JsonObject as wo) ->
                                                match wo.TryGetPropertyValue("flow") with
                                                | true, v when v <> null -> v.ToString()
                                                | _ -> ""
                                            | _ -> ""
                                        let keep =
                                            if segs.Length >= 4 then wk = segs.[3]
                                            else workFlow = targetFlow
                                        if keep then keptWorks.Add wk |> ignore
                                        else
                                            worksObj.Remove(wk) |> ignore
                                            truncated := true
                                | _ -> ()
                                // 3) arrows: (work 간) — 양 끝 work 가 모두 keptWorks 에 있는 것만 유지
                                match sysObj.TryGetPropertyValue("arrows") with
                                | true, (:? JsonArray as arrowsArr) ->
                                    let kept = ResizeArray<JsonNode>()
                                    let mutable removed = false
                                    for an in (arrowsArr |> Seq.toArray) do
                                        let raw = if an = null then "" else an.ToString()
                                        let inScope =
                                            match parseArrowSpec raw with
                                            | Ok spec ->
                                                keptWorks.Contains(normalizePath spec.FromRaw)
                                                && keptWorks.Contains(normalizePath spec.ToRaw)
                                            | Error _ -> false
                                        if inScope then kept.Add(JsonNode.Parse(if an = null then "null" else an.ToJsonString()))
                                        else removed <- true
                                    if removed then truncated := true
                                    arrowsArr.Clear()
                                    for k in kept do arrowsArr.Add(k)
                                | _ -> ()
                                // 4) segs[4] = call 필터 (해당 work 의 calls[] — string scalar 또는 object ref)
                                if segs.Length >= 5 then
                                    match sysObj.TryGetPropertyValue("works") with
                                    | true, (:? JsonObject as worksObj) ->
                                        match worksObj.TryGetPropertyValue(segs.[3]) with
                                        | true, (:? JsonObject as workObj) ->
                                            match workObj.TryGetPropertyValue("calls") with
                                            | true, (:? JsonArray as callsArr) ->
                                                let kept = ResizeArray<JsonNode>()
                                                let mutable removed = false
                                                for cn in (callsArr |> Seq.toArray) do
                                                    // call element 은 "SysName.ApiName" string 또는 { ref: ... } object.
                                                    let callStr =
                                                        match cn with
                                                        | :? JsonObject as co ->
                                                            match co.TryGetPropertyValue("ref") with
                                                            | true, v when v <> null -> v.ToString()
                                                            | _ -> ""
                                                        | _ -> if cn = null then "" else cn.ToString()
                                                    let lastDot = callStr.LastIndexOf('.')
                                                    let apiPart = if lastDot >= 0 then callStr.Substring(lastDot + 1) else callStr
                                                    if apiPart = segs.[4] then
                                                        kept.Add(JsonNode.Parse(if cn = null then "null" else cn.ToJsonString()))
                                                    else
                                                        removed <- true
                                                if removed then truncated := true
                                                callsArr.Clear()
                                                for k in kept do callsArr.Add(k)
                                            | _ -> ()
                                        | _ -> ()
                                    | _ -> ()
                            else
                                // passive system — segs[2] = ApiDef 이름. apis[] 필터.
                                if segs.Length >= 3 then
                                    match sysObj.TryGetPropertyValue("apis") with
                                    | true, (:? JsonArray as apisArr) ->
                                        let kept = ResizeArray<JsonNode>()
                                        let mutable removed = false
                                        let orig = apisArr |> Seq.toArray
                                        for an in orig do
                                            let s = if an = null then "" else an.ToString()
                                            if s = segs.[2] then
                                                kept.Add(JsonValue.Create(s))
                                            else
                                                removed <- true
                                        if removed then truncated := true
                                        apisArr.Clear()
                                        for k in kept do apisArr.Add(k)
                                    | _ -> ()
                        | _ -> ()
                | true, (:? JsonArray as _systemsArr) ->
                    // segs.Length = 1 (path = project 만) — systems 그대로
                    ()
                | _ -> ()

    /// depth cap — scope root 로부터 d 단계 자식까지 유지. 그 너머는 제거 + truncated set.
    /// `baseDepth` = scope entity 의 entity-level depth (0=project, 1=system, 2=flow/api, 3=work, 4=call — §2.5.1 정합).
    /// `maxAbsDepth` = baseDepth + d. **entity-level** 절단 (disk JSON 평탄화로 flows/works/arrows 가 system content
    /// 형제 키가 되었으므로 literal JSON 중첩 depth 가 아닌 entity 레벨 기준 — §4.3):
    ///   level<1 → systems 배열 제거 / level<2 → system identity(system/kind/device)만 / level<3 → flows·apis(레벨2)
    ///   유지 + works·arrows(work=레벨3) 제거 / level<4 → works skeleton 유지 + 각 work 의 calls·call-arrows(call=레벨4)
    ///   제거 / level>=4 → calls 까지 유지.
    let private applyDepthCap (root: JsonObject) (maxAbsDepth: int) (truncated: bool ref) : unit =
        // level<1 : systems 배열 제거 (envelope only)
        if maxAbsDepth < 1 then
            match root.TryGetPropertyValue("systems") with
            | true, (:? JsonArray as sa) when sa.Count > 0 ->
                truncated := true
                sa.Clear()
            | _ -> ()
        else
            match root.TryGetPropertyValue("systems") with
            | true, (:? JsonArray as systemsArr) ->
                for node in systemsArr do
                    match node with
                    | :? JsonObject as sysObj ->
                        if maxAbsDepth < 2 then
                            // level 1 — system identity 만 (system/kind/device). flows/works/arrows/apis/iri/plc 등 제거.
                            let keysToRemove =
                                sysObj
                                |> Seq.filter (fun kv ->
                                    kv.Key <> "system" && kv.Key <> "kind" && kv.Key <> "device")
                                |> Seq.map (fun kv -> kv.Key)
                                |> Seq.toList
                            if not keysToRemove.IsEmpty then truncated := true
                            for k in keysToRemove do
                                sysObj.Remove(k) |> ignore
                        elif maxAbsDepth < 3 then
                            // level 2 — flow(active) / apidef(passive) 까지 유지. work(레벨3) + work 간 arrows 제거.
                            if sysObj.ContainsKey("works") then
                                truncated := true
                                sysObj.Remove("works") |> ignore
                            if sysObj.ContainsKey("arrows") then
                                truncated := true
                                sysObj.Remove("arrows") |> ignore
                        elif maxAbsDepth < 4 then
                            // level 3 — work skeleton (flow:/tokenRole 등) 유지. 각 work 의 calls + call-arrows(레벨4) 제거.
                            match sysObj.TryGetPropertyValue("works") with
                            | true, (:? JsonObject as worksObj) ->
                                for kv in (worksObj |> Seq.toArray) do
                                    match kv.Value with
                                    | :? JsonObject as workObj ->
                                        if workObj.ContainsKey("calls") then
                                            truncated := true
                                            workObj.Remove("calls") |> ignore
                                        if workObj.ContainsKey("arrows") then
                                            truncated := true
                                            workObj.Remove("arrows") |> ignore
                                    | _ -> ()
                            | _ -> ()
                        // maxAbsDepth >= 4 — calls 까지 모두 유지 (추가 절단 없음)
                    | _ -> ()
            | _ -> ()

    /// systems[] / flow / work / call / apidef 수 합계. budget 측정 + summary.totalEntities 의 단위.
    /// **카운트 단위** (SSOT §2.8 후속 본문 명시 예정): EntityKind 가 `find_by_name` 에서 노출되는 5종
    /// (System / Flow / Work / Call / ApiDef). Arrow 는 entity 가 아닌 관계, device / kind / workDuration /
    /// opposing 은 attribute — 카운트 미포함. Project 는 envelope root 라 카운트 미포함 (단일 project export).
    let private countEntities (systemsArr: JsonArray) : int =
        let mutable c = 0
        for n in systemsArr do
            match n with
            | :? JsonObject as sysObj ->
                c <- c + 1
                // passive system 의 ApiDef 카운트 (apis[] 의 string 각 항목)
                match sysObj.TryGetPropertyValue("apis") with
                | true, (:? JsonArray as apisArr) -> c <- c + apisArr.Count
                | _ -> ()
                // active Flow 카운트 (flows: mapping 의 key 수)
                match sysObj.TryGetPropertyValue("flows") with
                | true, (:? JsonObject as flowsObj) -> c <- c + flowsObj.Count
                | _ -> ()
                // active Work + Call 카운트 (works: system 직속 mapping)
                match sysObj.TryGetPropertyValue("works") with
                | true, (:? JsonObject as worksObj) ->
                    for wkv in worksObj do
                        c <- c + 1   // Work
                        match wkv.Value with
                        | :? JsonObject as workObj ->
                            match workObj.TryGetPropertyValue("calls") with
                            | true, (:? JsonArray as callsArr) -> c <- c + callsArr.Count   // Call
                            | _ -> ()
                        | _ -> ()
                | _ -> ()
            | _ -> ()
        c

    /// partial entry 의 entity budget 상한. SSOT `yaml-protocol-v0.md §2.8` (후속 SSOT commit 에서 본문 명시 예정).
    /// 현 PoC scale (3 zone × N cylinder + Pusher Punch) 에선 절단 거의 도달 안 함 — 사실상 무제한 +
    /// 안전 catch-all. v4 round 의 50 한도가 path 명시 scope 를 통째 삭제하던 회귀 (Major-1) 해소.
    [<Literal>]
    let private PartialBudget = 500

    /// partial entry budget — limit 초과 시 후미 systems 부터 제거 + truncated set.
    /// systems 는 항상 array 유지 (type 단일성). 진단 정보 (totalEntities / emitted / budget) 는
    /// `exportToJsonScoped` 의 `summary` metadata 키로 별도 emit — LLM 이 "513 이면 늘려서 재호출,
    /// 50000 이면 포기" 류의 후속 호출 전략 결정 가능.
    /// SSOT `done-read-surface-guid-cleanup.md` §4.3 ("빈 결과 의미 구분") 정합 — `[]` (실제 0건) 와
    /// `view: partial` + `summary` 동반 (절단으로 0건) 구분은 view/summary 조합으로.
    let private applyEntityBudget (root: JsonObject) (limit: int) (truncated: bool ref) : unit =
        match root.TryGetPropertyValue("systems") with
        | true, (:? JsonArray as systemsArr) ->
            if countEntities systemsArr > limit then
                truncated := true
                while systemsArr.Count > 0 && countEntities systemsArr > limit do
                    systemsArr.RemoveAt(systemsArr.Count - 1)
        | _ -> ()

    /// `exportToJsonScopedWithLevel` — Phase 7 §10.2 #31 — level 인자 entry. 기존 `exportToJsonScoped`
    /// 는 `Full` delegate (후방 호환). Modeling level 시 wire 의 `level: modeling` 키는
    /// `exportToJsonWithLevel` 안에서 emit — partial post-process 는 level 무관 (path/depth 절단은
    /// 카테고리 마스킹과 직교, S1.5 표 정합).
    let exportToJsonScopedWithLevel
            (store: DsStore) (pathOpt: string option) (depthOpt: int option)
            (level: ExportLevel) : JsonDocument =
        match pathOpt, depthOpt with
        | None, None -> exportToJsonWithLevel store level
        | _ ->
            // path 미존재 사전 거부
            let scopeOpt =
                match pathOpt with
                | None -> None
                | Some path ->
                    match tryFindEntity store path with
                    | Some hit -> Some hit
                    | None ->
                        invalidOp (sprintf "VALIDATION_ERROR: path \"%s\" 가 store 에 존재하지 않습니다 (fail-fast). 근사 후보는 `find_by_name` 도구로 확인하세요." path)

            use fullDoc = exportToJsonWithLevel store level
            let root =
                match JsonNode.Parse(fullDoc.RootElement.GetRawText()) with
                | :? JsonObject as o -> o
                | _ -> invalidOp "INTERNAL_ERROR: exportToJson 결과 root 가 object 가 아닙니다."

            let truncated = ref false

            // 절단 전 entity 합 — summary metadata 의 totalEntities 필드용.
            // **의미**: `exportToJson` 이 emit 한 단일 project 의 entity 합. multi-project store 의 경우
            // 첫 project 만 cover (exportToJson:1192 의 단일 project emit 제약 — todo §7.1 후속 cycle).
            // path scope 가 다른 project 를 가리키는 mismatch 분기 (현 PoC N=1 가정상 사실상 미도달)
            // 에서는 totalEntities 가 의도 외 project 합을 표시할 수 있음 — multi-project 도입 시 재정의.
            let totalEntitiesBefore =
                match root.TryGetPropertyValue("systems") with
                | true, (:? JsonArray as sa) -> countEntities sa
                | _ -> 0

            // path scope
            match pathOpt with
            | Some raw ->
                let segs = pathSegments raw
                applyPathScope root segs truncated
            | None -> ()

            // depth cap (scope baseDepth + d)
            match depthOpt with
            | Some d when d >= 0 ->
                let baseDepth =
                    match scopeOpt with
                    | None | Some (EntityKind.Project, _) -> 0
                    | Some (EntityKind.System, _) -> 1
                    | Some (EntityKind.Flow, _) | Some (EntityKind.ApiDef, _) -> 2
                    | Some (EntityKind.Work, _) -> 3
                    | Some (EntityKind.Call, _) -> 4
                    | _ -> 0
                applyDepthCap root (baseDepth + d) truncated
            | _ -> ()

            // partial budget — partial entry only (PartialBudget = 500)
            applyEntityBudget root PartialBudget truncated

            // view 재스탬프 (실제 truncation 0건이면 full 유지, 1건+ 면 partial)
            setView root (if !truncated then "partial" else "full")

            // summary metadata — 절단 발생 시에만 emit. LLM 이 totalEntities / budget 비교로 후속 전략
            // 결정 (좁혀서 재호출 / 포기). SSOT §2.8 / todo §4.3 정합. 정상 (view: full) 결과에는 부재.
            if !truncated then
                let emittedAfter =
                    match root.TryGetPropertyValue("systems") with
                    | true, (:? JsonArray as sa) -> countEntities sa
                    | _ -> 0
                let summary = JsonObject()
                summary.["totalEntities"] <- JsonValue.Create(totalEntitiesBefore)
                summary.["emitted"] <- JsonValue.Create(emittedAfter)
                summary.["budget"] <- JsonValue.Create(PartialBudget)
                root.["summary"] <- summary

            JsonDocument.Parse(root.ToJsonString())

    /// Phase 7 §10.2 #31 — 후방 호환 wrapper. 기존 호출처 (테스트 / ModelTools 등) 시그니처
    /// 변경 없이 `Full` delegate. 새 호출처는 `exportToJsonScopedWithLevel` 직접 호출.
    let exportToJsonScoped (store: DsStore) (pathOpt: string option) (depthOpt: int option) : JsonDocument =
        exportToJsonScopedWithLevel store pathOpt depthOpt Full
