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
    // CallConditionType / ContactKind / CallType / ApiDefActionType — emit/apply 양쪽 호출
    // 예정 (C-3 ~ C-5). 본 phase 는 helper 만 추가 — 기존 동작 영향 0건.
    // 형식은 parseArrowType 패턴 답습 (Result<_, string>) — error 메시지에 허용 라벨 enumerate.

    let parseCallConditionType (raw: string) : Result<CallConditionType, string> =
        match raw.Trim() with
        | "AutoAux" -> Ok CallConditionType.AutoAux
        | "ComAux" -> Ok CallConditionType.ComAux
        | "SkipUnmatch" -> Ok CallConditionType.SkipUnmatch
        | other -> Error (sprintf "callCondition type '%s' 미지원. 허용: AutoAux|ComAux|SkipUnmatch." other)

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

    // ApiDefActionType — DU 변형. 표기 grammar (device DU literal §2.3 패턴 답습):
    //   - 인자 없음: "Normal" / "Push" / "Pulse"
    //   - 1 인자  : "TimeTotal(500)" / "TimeAppend(200)"  (ms)
    //   - 2 인자  : "MultiAction(3, 100)"                  (count, ms)
    let private apiDefActionTypeRegex =
        System.Text.RegularExpressions.Regex(
            @"^([A-Za-z][A-Za-z0-9]*)(?:\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    let parseApiDefActionType (raw: string) : Result<ApiDefActionType, string> =
        let trimmed = raw.Trim()
        let m = apiDefActionTypeRegex.Match(trimmed)
        if not m.Success then
            Error (sprintf "apiDefActionType '%s' 인식 불가. 형식: Normal|Push|Pulse|TimeTotal(<ms>)|TimeAppend(<ms>)|MultiAction(<count>, <ms>)." raw)
        else
            let caseName = m.Groups.[1].Value
            let arg1Ok = m.Groups.[2].Success
            let arg2Ok = m.Groups.[3].Success
            match caseName, arg1Ok, arg2Ok with
            | "Normal",      false, _    -> Ok ApiDefActionType.Normal
            | "Push",        false, _    -> Ok ApiDefActionType.Push
            | "Pulse",       false, _    -> Ok ApiDefActionType.Pulse
            | "TimeTotal",   true,  false -> Ok (ApiDefActionType.TimeTotal  (int m.Groups.[2].Value))
            | "TimeAppend",  true,  false -> Ok (ApiDefActionType.TimeAppend (int m.Groups.[2].Value))
            | "MultiAction", true,  true  -> Ok (ApiDefActionType.MultiAction (int m.Groups.[2].Value, int m.Groups.[3].Value))
            | _ ->
                Error (sprintf "apiDefActionType '%s' — case 이름과 인자 개수 불일치. Normal/Push/Pulse 는 인자 없음, TimeTotal/TimeAppend 는 (ms), MultiAction 은 (count, ms)." raw)

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
        WorkIds: Dictionary<string, Dictionary<string, Guid>>  // flowName → workLocalName → Guid
    }

    let private newSystemEntry name kind = {
        Name = name
        Kind = kind
        SystemId = ref None
        ApiDefIds = Dictionary<string, Guid>(StringComparer.Ordinal)
        FlowIds = Dictionary<string, Guid>(StringComparer.Ordinal)
        WorkIds = Dictionary<string, Dictionary<string, Guid>>(StringComparer.Ordinal)
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
    }

    let private newContext (plan: ImportPlanBuilder) (store: DsStore) : ApplyContext = {
        Plan = plan
        Store = store
        Diagnostics = Diagnostics()
        Systems = Dictionary<string, SystemEntry>(StringComparer.Ordinal)
    }

    /// `flow Run` 같은 prefix 키 매칭 (SSOT §2.5).
    /// grammar: `flow` WS+ identifier (ASCII).
    let private flowKeyRegex =
        System.Text.RegularExpressions.Regex(@"^flow[ \t]+([A-Za-z0-9_\-]+)$", System.Text.RegularExpressions.RegexOptions.Compiled)

    let tryParseFlowKey (key: string) : string option =
        let m = flowKeyRegex.Match(key)
        if m.Success then Some m.Groups.[1].Value else None

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
                    | None -> ctx.Diagnostics.Add(path + ".system", "string 기대.")
                    | Some name ->
                        let kindRaw =
                            tryProp sysEl "kind"
                            |> Option.bind tryString
                        match kindRaw with
                        | None ->
                            ctx.Diagnostics.Add(path, "kind 누락. 'active' 또는 'passive' 명시 필요.")
                        | Some kind when kind <> "active" && kind <> "passive" ->
                            ctx.Diagnostics.Add(path + ".kind", sprintf "'%s' 미지원. 'active' 또는 'passive' 만 허용." kind)
                        | Some kind ->
                            // kind 와 키 정합성 체크 (SSOT §2.7 룰 6)
                            let hasFlowKey =
                                if sysEl.ValueKind = JsonValueKind.Object then
                                    sysEl.EnumerateObject()
                                    |> Seq.exists (fun p -> tryParseFlowKey p.Name |> Option.isSome)
                                else false
                            let hasDeviceKey = tryProp sysEl "device" |> Option.isSome
                            if kind = "passive" && hasFlowKey then
                                ctx.Diagnostics.Add(path, "kind=passive 인데 flow 키 존재. 어느 한쪽 수정.")
                            if kind = "active" && hasDeviceKey then
                                ctx.Diagnostics.Add(path, "kind=active 인데 device 키 존재. 어느 한쪽 수정.")
                            if ctx.Systems.ContainsKey name then
                                ctx.Diagnostics.Add(path + ".system", sprintf "'%s' 시스템 이름 중복." name)
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
            ctx.Diagnostics.Add(path + ".duration", "키 폐기됨. 'workDuration' 으로 변경하세요.")

        let workDuration =
            match workDurRaw with
            | None -> None
            | Some s ->
                match parseDuration s with
                | Ok ts -> Some ts
                | Error msg ->
                    ctx.Diagnostics.Add(path + ".workDuration", msg)
                    None

        match deviceRaw with
        | None ->
            // device 키 부재 — 단순 Passive 만 생성 (SSOT §5 매핑 표 잠정 허용).
            try
                let id = ToolOperations.queueAddPassiveSystem ctx.Plan ctx.Store entry.Name "Unit"
                entry.SystemId := Some id
            with ex -> ctx.Diagnostics.Add(path, ex.Message)
        | Some raw ->
            match parseDevice raw with
            | Error msg -> ctx.Diagnostics.Add(path + ".device", msg)
            | Ok lit ->
                match lit with
                | UnknownSugar bare ->
                    ctx.Diagnostics.Add(
                        path + ".device",
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

    let private dispatchActiveSystem
        (ctx: ApplyContext)
        (entry: SystemEntry)
        (_sysEl: JsonElement)
        (path: string) : unit =

        // M1 fix: active system 이름 sanitize 가드.
        if not (tryValidateName ctx (path + ".system") "System name" entry.Name) then () else

        try
            let id = ToolOperations.queueAddActiveSystem ctx.Plan ctx.Store entry.Name
            entry.SystemId := Some id
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

    // ─── CallCondition / ContactKind apply helpers (Phase 7 §4.2 C-3) ────────
    //
    // SSOT yaml-protocol-v0.md §2.2.1 dual format — object 형태 call 의 보강 property.
    // ToolOperations.queueAddCall 후 plan 에 추가된 Call instance 를 추출하여 직접 mutation.
    // ToolOperations 의 동일 패턴 (`tryFindCallInPlan`) 답습 — ModelProtocol 내부 private 재선언.

    let private tryFindCallInPlan (plan: ImportPlanBuilder) (callId: Guid) : Call option =
        plan.Operations
        |> Seq.tryPick (function
            | AddCall c when c.Id = callId -> Some c
            | _ -> None)

    /// CallCondition tree (Type / IsOR / IsInverted / Conditions / Children) recursive parse.
    /// conditions leaf 는 ApiCall — dual format (string scalar 또는 object{ref, contactKind?}).
    /// PoC scope 가정: leaf 의 ApiCall 은 ApiDefId + ContactKind 만 set (IO tag binding 은 C-4 phase).
    let rec private parseCallCondition (ctx: ApplyContext) (condEl: JsonElement) (path: string) : CallCondition option =
        if condEl.ValueKind <> JsonValueKind.Object then
            ctx.Diagnostics.Add(path, sprintf "callCondition object 기대 (실제 %A)." condEl.ValueKind)
            None
        else
            let cond = CallCondition()
            // type — AutoAux default 면 키 부재
            tryProp condEl "type"
            |> Option.bind tryString
            |> Option.iter (fun s ->
                match parseCallConditionType s with
                | Ok t -> cond.Type <- Some t
                | Error msg -> ctx.Diagnostics.Add(path + ".type", msg))
            // isOR / isInverted — bool false default
            let parseBoolKey key target =
                tryProp condEl key
                |> Option.iter (fun el ->
                    match el.ValueKind with
                    | JsonValueKind.True -> target true
                    | JsonValueKind.False -> target false
                    | _ -> ctx.Diagnostics.Add(path + "." + key, sprintf "bool 기대 (실제 %A)." el.ValueKind))
            parseBoolKey "isOR" (fun b -> cond.IsOR <- b)
            parseBoolKey "isInverted" (fun b -> cond.IsInverted <- b)
            // conditions — ApiCall leaf list (dual format)
            tryProp condEl "conditions"
            |> Option.iter (fun condsEl ->
                if condsEl.ValueKind = JsonValueKind.Array then
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
                                | Error msg -> ctx.Diagnostics.Add(leafPath + ".contactKind", msg))
                        | _ ->
                            ctx.Diagnostics.Add(leafPath, sprintf "string 또는 object 기대 (실제 %A)." leafEl.ValueKind)
                        if validLeaf then cond.Conditions.Add(apiCall)
                        idx <- idx + 1)
            // children — nested CallCondition list (recursive)
            tryProp condEl "children"
            |> Option.iter (fun chEl ->
                if chEl.ValueKind = JsonValueKind.Array then
                    let mutable idx = 0
                    for childEl in chEl.EnumerateArray() do
                        let childPath = sprintf "%s.children[%d]" path idx
                        match parseCallCondition ctx childEl childPath with
                        | Some child -> cond.Children.Add(child)
                        | None -> ()
                        idx <- idx + 1)
            Some cond

    let private dispatchWork
        (ctx: ApplyContext)
        (sysEntry: SystemEntry)
        (flowName: string)
        (flowId: Guid)
        (workLocalName: string)
        (workEl: JsonElement)
        (path: string) : unit =

        try
            // M1 fix: work localName sanitize 가드.
            if not (tryValidateName ctx path "Work localName" workLocalName) then () else
            // M3 fix: workDuration 을 queueAddWork 호출 *전* 에 파싱해서 옵션 인자로 전달.
            // 후행 mutation (plan.Operations 재검색 + w.Duration <- ts) 제거 — Operations immutable invariant 보존.
            let durationOpt =
                tryProp workEl "workDuration" |> Option.bind tryString
                |> Option.bind (fun s ->
                    match parseDuration s with
                    | Ok ts -> Some ts
                    | Error msg ->
                        ctx.Diagnostics.Add(path + ".workDuration", msg)
                        None)
            let workId = ToolOperations.queueAddWork ctx.Plan ctx.Store workLocalName flowId durationOpt
            // WorkIds 누적
            if not (sysEntry.WorkIds.ContainsKey flowName) then
                sysEntry.WorkIds.[flowName] <- Dictionary<string, Guid>(StringComparer.Ordinal)
            sysEntry.WorkIds.[flowName].[workLocalName] <- workId

            if tryProp workEl "duration" |> Option.isSome then
                ctx.Diagnostics.Add(path + ".duration", "키 폐기됨. 'workDuration' 으로 변경하세요.")

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
                        let callId =
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
                        // CallCondition: recursive parse 후 call.CallConditions 에 추가.
                        callObjOpt |> Option.iter (fun obj ->
                            match tryFindCallInPlan ctx.Plan callId with
                            | None ->
                                ctx.Diagnostics.Add(callPath, "queueAddCall 후 plan 에서 Call instance 추적 실패 (forensic).")
                            | Some call ->
                                tryProp obj "contactKind"
                                |> Option.bind tryString
                                |> Option.iter (fun s ->
                                    match parseContactKind s with
                                    | Ok k ->
                                        if call.ApiCalls.Count > 0 then
                                            call.ApiCalls.[0].ContactKind <- k
                                    | Error msg -> ctx.Diagnostics.Add(callPath + ".contactKind", msg))
                                tryProp obj "callCondition"
                                |> Option.iter (fun ccEl ->
                                    match parseCallCondition ctx ccEl (callPath + ".callCondition") with
                                    | Some cc -> call.CallConditions.Add(cc)
                                    | None -> ()))
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
            // flow prefix 키 수집
            let flowKeys =
                if sysEl.ValueKind = JsonValueKind.Object then
                    sysEl.EnumerateObject()
                    |> Seq.choose (fun p ->
                        tryParseFlowKey p.Name
                        |> Option.map (fun fname -> fname, p.Value))
                    |> Seq.toList
                else []

            // 중복 prefix 검사 — 첫 등장만 채택, 두 번째 이후는 diagnostic 후 skip (Critical 3 fix).
            // 미수정 시 같은 이름 Flow 가 두 번 queueAddFlow 되어 sysEntry.FlowIds 가 두 번째 ID 로 덮어써짐.
            // single-pass: filter 가 dedup 과 diagnostic 동시 수행.
            let seen = HashSet<string>(StringComparer.Ordinal)
            let dedupedFlowKeys =
                flowKeys
                |> List.filter (fun (fname, _) ->
                    if seen.Add fname then true
                    else
                        ctx.Diagnostics.Add(basePath, sprintf "'flow %s' 키 중복." fname)
                        false)

            for (flowName, flowEl) in dedupedFlowKeys do
                let flowPath = sprintf "%s.flow %s" basePath flowName
                // M1 fix: flow 이름 sanitize 가드.
                if not (tryValidateName ctx flowPath "Flow name" flowName) then () else
                try
                    let flowId = ToolOperations.queueAddFlow ctx.Plan ctx.Store flowName sysId
                    sysEntry.FlowIds.[flowName] <- flowId

                    // works (mapping)
                    let worksEl = tryProp flowEl "works"
                    match worksEl with
                    | None -> ()
                    | Some w when w.ValueKind <> JsonValueKind.Object ->
                        ctx.Diagnostics.Add(flowPath + ".works", "Object 기대.")
                    | Some w ->
                        for prop in w.EnumerateObject() do
                            let workPath = sprintf "%s.works.%s" flowPath prop.Name
                            dispatchWork ctx sysEntry flowName flowId prop.Name prop.Value workPath

                    // arrows (Flow 안 — ArrowBetweenWorks)
                    let arrowsList =
                        tryProp flowEl "arrows"
                        |> Option.bind (fun el ->
                            if el.ValueKind = JsonValueKind.Array then
                                el.EnumerateArray()
                                |> Seq.map extractArrowString
                                |> Seq.toList
                                |> Some
                            else None)
                        |> Option.defaultValue []
                    let resolveWorkId (rawName: string) (subPath: string) : Guid option =
                        let workMap =
                            match sysEntry.WorkIds.TryGetValue flowName with
                            | true, m -> m
                            | _ -> Dictionary<string, Guid>()
                        let normalized = normalizePath rawName
                        match workMap.TryGetValue normalized with
                        | true, id -> Some id
                        | _ ->
                            // Levenshtein 키 통일 (review m3): normalized vs key set 모두 normalized 형식.
                            let candidates = nearestCandidates normalized workMap.Keys 3
                            ctx.Diagnostics.Add(
                                subPath,
                                sprintf "Work '%s' 가 발견되지 않음." rawName,
                                ?suggestion = (if candidates.IsEmpty then None else Some (String.Join(" / ", candidates))))
                            None
                    let processOneFlowArrow (arrowPath: string) (arrowRaw: string) : unit =
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
                                        try
                                            ToolOperations.queueAddArrow ctx.Plan ctx.Store s t aType |> ignore
                                        with ex ->
                                            ctx.Diagnostics.Add(arrowPath, ex.Message)
                                    | _ -> ()
                    let mutable aIdx = 0
                    for arrowResult in arrowsList do
                        let arrowPath = sprintf "%s.arrows[%d]" flowPath aIdx
                        match arrowResult with
                        | Error msg -> ctx.Diagnostics.Add(arrowPath, msg)
                        | Ok arrowRaw -> processOneFlowArrow arrowPath arrowRaw
                        aIdx <- aIdx + 1
                with ex ->
                    ctx.Diagnostics.Add(flowPath, ex.Message)

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

    // ─── Patch DSL — v0 (SSOT §2.6) ─────────────────────────────────────────
    //
    // 본 PoC 는 schema 의 add / arrows.add / rename / remove 4 종 dispatch.
    // 자세한 구현은 후속 cycle — patch path 는 store 가 이미 채워져 있는 경우 주력 시나리오.

    /// 단일 segment system path → store 안의 DsSystem 검색 (active + passive 합집합).
    let private findSystemByName (store: DsStore) (sysName: string) : DsSystem option =
        Queries.allProjects store
        |> List.collect (fun p ->
            (Queries.activeSystemsOf p.Id store)
            @ (Queries.passiveSystemsOf p.Id store))
        |> List.tryFind (fun s -> s.Name = sysName)

    /// `<system>.<flow>` 형식 path → store 안의 Flow 검색.
    let private findFlowByPath (store: DsStore) (rawPath: string) : Flow option =
        match pathSegments rawPath with
        | [ sysName; flowName ] ->
            findSystemByName store sysName
            |> Option.bind (fun s ->
                Queries.flowsOf s.Id store
                |> List.tryFind (fun f -> f.Name = flowName))
        | _ -> None

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
            // patch.add 안의 각 entry — system 키 있으면 systems list 와 동일 처리.
            // review C2 (silent drop): system 키 없는 entry (`in:` + works/calls 등) 는 PoC 미지원 →
            // silent drop 대신 친절 에러로 안내 (patch.arrows.remove 의 line 877 패턴과 동일).
            let entriesWithIdx = addEl.EnumerateArray() |> Seq.toList |> List.indexed
            for (i, entry) in entriesWithIdx do
                if tryProp entry "system" |> Option.isNone then
                    let hint =
                        if tryProp entry "in" |> Option.isSome then
                            "PoC 미지원 — `in:` + works/calls 등 자식 키 추가는 후속 cycle. 새 Work/Call 추가는 `apply_model_doc` 으로 전체 doc 재발행."
                        else
                            "patch.add entry 는 `system:` 키 필수 (Passive/Active system 추가). 다른 형식 미지원."
                    ctx.Diagnostics.Add(sprintf "patch.add[%d]" i, hint)
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
        | _ -> ()

        // patch.arrows.add / patch.arrows.remove — SSOT §2.6 / §3.4 (Critical 1 fix)
        match tryProp patchEl "arrows" with
        | Some arrowsEl when arrowsEl.ValueKind = JsonValueKind.Object ->
            // arrows.add — Flow 단위 entries
            match tryProp arrowsEl "add" with
            | Some addList when addList.ValueKind = JsonValueKind.Array ->
                let mutable aIdx = 0
                for entry in addList.EnumerateArray() do
                    let path = sprintf "patch.arrows.add[%d]" aIdx
                    let inPath = tryProp entry "in" |> Option.bind tryString
                    let entriesEl = tryProp entry "entries"
                    match inPath, entriesEl with
                    | None, _ -> ctx.Diagnostics.Add(path, "'in' 키 누락 (Flow path 필요).")
                    | _, None -> ctx.Diagnostics.Add(path, "'entries' 키 누락 (arrow 표기 list 필요).")
                    | Some flowPath, Some entries when entries.ValueKind = JsonValueKind.Array ->
                        match findFlowByPath ctx.Store flowPath with
                        | None -> ctx.Diagnostics.Add(path, sprintf "Flow '%s' 가 store 에 없습니다." flowPath)
                        | Some flow ->
                            let mutable eIdx = 0
                            for arrEl in entries.EnumerateArray() do
                                let entryPath = sprintf "%s.entries[%d]" path eIdx
                                match extractArrowString arrEl with
                                | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                                | Ok raw ->
                                    match parseArrowSpec raw with
                                    | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                                    | Ok spec ->
                                        match spec.TypeRaw with
                                        | None -> ctx.Diagnostics.Add(entryPath, "type 누락. '<from> -> <to> : <Type>' 형식 사용.")
                                        | Some tRaw ->
                                            match parseArrowType tRaw with
                                            | Error msg -> ctx.Diagnostics.Add(entryPath, msg)
                                            | Ok aType ->
                                                let resolveWork (rawName: string) : Guid option =
                                                    Queries.worksOf flow.Id ctx.Store
                                                    |> List.tryFind (fun w -> w.LocalName = normalizePath rawName)
                                                    |> Option.map (fun w -> w.Id)
                                                match resolveWork spec.FromRaw, resolveWork spec.ToRaw with
                                                | Some s, Some t ->
                                                    try
                                                        ToolOperations.queueAddArrow ctx.Plan ctx.Store s t aType |> ignore
                                                    with ex -> ctx.Diagnostics.Add(entryPath, ex.Message)
                                                | None, _ -> ctx.Diagnostics.Add(entryPath + ".from", sprintf "Work '%s' 가 Flow '%s' 에 없습니다." spec.FromRaw flowPath)
                                                | _, None -> ctx.Diagnostics.Add(entryPath + ".to", sprintf "Work '%s' 가 Flow '%s' 에 없습니다." spec.ToRaw flowPath)
                                eIdx <- eIdx + 1
                    | _ -> ctx.Diagnostics.Add(path, "'entries' 가 array 가 아닙니다.")
                    aIdx <- aIdx + 1
            | _ -> ()
            // arrows.remove — PoC 미지원 (EntityKind 에 ArrowWork case 없음 → queueRemoveEntity 경로 부재)
            // 후속: EntityKind.ArrowWork / ArrowCall 확장 + CascadeRemove 분기 추가 필요. 현재는 친절 에러로 안내.
            match tryProp arrowsEl "remove" with
            | Some _ ->
                ctx.Diagnostics.Add(
                    "patch.arrows.remove",
                    "PoC 미지원 — Arrow 단독 제거는 후속 cycle (EntityKind 확장 필요). 부모 Work 제거로 cascade 우회 가능.")
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
        match tryProp patchEl "remove" with
        | Some removeEl when removeEl.ValueKind = JsonValueKind.Array ->
            let mutable rIdx = 0
            for entry in removeEl.EnumerateArray() do
                let path = sprintf "patch.remove[%d]" rIdx
                match tryString entry with
                | None -> ctx.Diagnostics.Add(path, "remove 항목은 path string 이어야 합니다.")
                | Some rawPath ->
                    let segs = pathSegments rawPath
                    match segs with
                    | [ sysName ] ->
                        let sysOpt =
                            Queries.allProjects ctx.Store
                            |> List.collect (fun p ->
                                (Queries.activeSystemsOf p.Id ctx.Store)
                                @ (Queries.passiveSystemsOf p.Id ctx.Store))
                            |> List.tryFind (fun s -> s.Name = sysName)
                        match sysOpt with
                        | None -> ctx.Diagnostics.Add(path, sprintf "System '%s' 가 발견되지 않음." sysName)
                        | Some s ->
                            try
                                ToolOperations.queueRemoveEntity ctx.Plan ctx.Store s.Id |> ignore
                            with ex -> ctx.Diagnostics.Add(path, ex.Message)
                    | _ ->
                        ctx.Diagnostics.Add(path, "PoC 는 single-segment system remove 만 지원.")
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

        if ctx.Diagnostics.HasErrors then
            // protocol 거부 시점 — 본 path 는 plan 미변경이라 truncate no-op. 일관성 위해 호출.
            plan.TruncateTo(snapshotCount)
            ctx.Diagnostics, Map.empty
        else
            // project 키 처리
            let _projectId = resolveProjectKey ctx root

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

    let formatCallConditionType (t: CallConditionType) : string =
        match t with
        | CallConditionType.AutoAux -> "AutoAux"
        | CallConditionType.ComAux -> "ComAux"
        | CallConditionType.SkipUnmatch -> "SkipUnmatch"
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

    let formatApiDefActionType (a: ApiDefActionType) : string =
        match a with
        | ApiDefActionType.Normal              -> "Normal"
        | ApiDefActionType.Push                -> "Push"
        | ApiDefActionType.Pulse               -> "Pulse"
        | ApiDefActionType.TimeTotal  ms       -> sprintf "TimeTotal(%d)" ms
        | ApiDefActionType.TimeAppend ms       -> sprintf "TimeAppend(%d)" ms
        | ApiDefActionType.MultiAction(c, ms)  -> sprintf "MultiAction(%d, %d)" c ms

    // ─── CallCondition / ContactKind emit helpers (Phase 7 §4.2 C-3) ─────────
    //
    // dual format (§2.2.1) 의 emit 측 — store 값 inspection 후 default 인지 판단.
    // PoC 가정: Call.CallConditions 는 multiple root 가능하나 *첫 root 만 emit*. 후속 phase 가 multiple root 정책 결정.

    let private callHasEnhancement (c: Call) : bool =
        let hasCallCondition = c.CallConditions.Count > 0
        let hasNonDefaultContactKind =
            c.ApiCalls.Count > 0 && c.ApiCalls.[0].ContactKind <> ContactKind.NoContact
        hasCallCondition || hasNonDefaultContactKind

    /// CallCondition tree recursive emit. `apiCallRef` 람다: ApiCall → "<System>.<ApiDef>" path 도출
    /// (caller 가 store 컨텍스트 제공). conditions leaf 는 ContactKind default 면 string scalar, 아니면 object.
    let rec private emitCallCondition
        (w: Utf8JsonWriter)
        (apiCallRef: ApiCall -> string)
        (cond: CallCondition) : unit =
        w.WriteStartObject()
        // type — AutoAux default 면 생략
        (match cond.Type with
         | Some t when t <> CallConditionType.AutoAux ->
             w.WriteString("type", formatCallConditionType t)
         | _ -> ())
        if cond.IsOR then w.WriteBoolean("isOR", true)
        if cond.IsInverted then w.WriteBoolean("isInverted", true)
        if cond.Conditions.Count > 0 then
            w.WritePropertyName "conditions"
            w.WriteStartArray()
            for ac in cond.Conditions do
                let leafRef = apiCallRef ac
                if ac.ContactKind <> ContactKind.NoContact then
                    w.WriteStartObject()
                    w.WriteString("ref", leafRef)
                    w.WriteString("contactKind", formatContactKind ac.ContactKind)
                    w.WriteEndObject()
                else
                    w.WriteStringValue(leafRef)
            w.WriteEndArray()
        if cond.Children.Count > 0 then
            w.WritePropertyName "children"
            w.WriteStartArray()
            for child in cond.Children do
                emitCallCondition w apiCallRef child
            w.WriteEndArray()
        w.WriteEndObject()

    /// Passive system 의 internal Flow 의 ResetReset arrow 갯수 → opposing 추정.
    /// chain: N-1 / all-pairs: N*(N-1)/2 / none: 0
    let private inferOpposing (apiCount: int) (resetResetCount: int) : string =
        if apiCount <= 1 || resetResetCount = 0 then "none"
        elif resetResetCount = apiCount - 1 then "chain"
        elif resetResetCount = apiCount * (apiCount - 1) / 2 then "all-pairs"
        else "none"  // unknown shape — conservative

    let exportToJson (store: DsStore) : JsonDocument =
        let projects = Queries.allProjects store
        let ms = new MemoryStream()
        do
            use w = new Utf8JsonWriter(ms)
            w.WriteStartObject()
            w.WriteString("protocol", "promaker/v0")
            // SSOT §2.8 — 전체 export 는 항상 view: full. partial 변형은 별도 함수 (Phase 6 후속 commit).
            w.WriteString("view", "full")
            match projects with
            | [] -> ()
            | p :: _ ->
                w.WriteString("project", p.Name)
                w.WriteStartArray("systems")

                let actives = Queries.activeSystemsOf p.Id store
                let passives = Queries.passiveSystemsOf p.Id store

                for s in actives do
                    w.WriteStartObject()
                    w.WriteString("system", s.Name)
                    w.WriteString("kind", "active")
                    // flows (object)
                    let flows = Queries.flowsOf s.Id store
                    for f in flows do
                        w.WritePropertyName(sprintf "flow %s" f.Name)
                        w.WriteStartObject()
                        // works
                        let works = Queries.worksOf f.Id store
                        if not works.IsEmpty then
                            w.WritePropertyName "works"
                            w.WriteStartObject()
                            for wk in works do
                                w.WritePropertyName wk.LocalName
                                w.WriteStartObject()
                                let calls = Queries.callsOf wk.Id store
                                if not calls.IsEmpty then
                                    w.WritePropertyName "calls"
                                    w.WriteStartArray()
                                    // CallCondition.Conditions leaf (ApiCall) → path 도출 람다. ApiDefId/getApiDef/getSystem
                                    // 어느 단계든 실패 시 ApiCall.Name fallback (데이터 무결성 깨진 케이스).
                                    let apiCallRef (ac: ApiCall) : string =
                                        match ac.ApiDefId with
                                        | Some apiDefId ->
                                            match Queries.getApiDef apiDefId store with
                                            | Some apiDef ->
                                                match Queries.getSystem apiDef.ParentId store with
                                                | Some sys -> sprintf "%s.%s" sys.Name apiDef.Name
                                                | None -> ac.Name
                                            | None -> ac.Name
                                        | None -> ac.Name
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
                                        // 있으면 object 승격 + 보강 property (현 phase: contactKind / callCondition).
                                        // CallType / SkipInputSensor / InTag/OutTag/etc 는 C-4/C-5 phase.
                                        if callHasEnhancement c then
                                            w.WriteStartObject()
                                            w.WriteString("ref", callRef)
                                            if c.ApiCalls.Count > 0 then
                                                let ck = c.ApiCalls.[0].ContactKind
                                                if ck <> ContactKind.NoContact then
                                                    w.WriteString("contactKind", formatContactKind ck)
                                            if c.CallConditions.Count > 0 then
                                                w.WritePropertyName "callCondition"
                                                emitCallCondition w apiCallRef c.CallConditions.[0]
                                            w.WriteEndObject()
                                        else
                                            w.WriteStringValue(callRef)
                                    w.WriteEndArray()
                                // arrows (Work 안 — ArrowBetweenCalls). round-trip 정합: apply 측 (line 617~) 의
                                // callIdMap 키 (`sysName.apiName`) 와 동일한 normalized 표현 사용 → load 시 resolveCallId
                                // 매칭 보장. 미 emit 시 work-level call 간 분기 (병렬/순차) 정보가 round-trip 에서 소실.
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
                                match wk.Duration with
                                | Some d when d <> TimeSpan.FromMilliseconds 500. ->
                                    w.WriteString("workDuration", formatDuration d)
                                | _ -> ()
                                w.WriteEndObject()
                            w.WriteEndObject()
                        // arrows (Flow 안 — ArrowBetweenWorks)
                        let workArrows = Queries.arrowWorksOf s.Id store
                        let workArrowsForFlow =
                            workArrows
                            |> List.filter (fun a ->
                                match Queries.getWork a.SourceId store with
                                | Some sw -> sw.ParentId = f.Id
                                | None -> false)
                        if not workArrowsForFlow.IsEmpty then
                            w.WritePropertyName "arrows"
                            w.WriteStartArray()
                            for a in workArrowsForFlow do
                                let srcW = Queries.getWork a.SourceId store
                                let tgtW = Queries.getWork a.TargetId store
                                match srcW, tgtW with
                                | Some sw, Some tw ->
                                    w.WriteStringValue(sprintf "%s -> %s : %s" sw.LocalName tw.LocalName (formatArrowType a.ArrowType))
                                | _ -> ()
                            w.WriteEndArray()
                        w.WriteEndObject()
                    w.WriteEndObject()

                for s in passives do
                    w.WriteStartObject()
                    w.WriteString("system", s.Name)
                    w.WriteString("kind", "passive")
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
                    w.WriteEndObject()

                w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
        ms.Position <- 0L
        JsonDocument.Parse(ms.ToArray())

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
                                // segs[2] = flow 이름 — "flow X" 키 외 제거
                                let flowKey = "flow " + segs.[2]
                                let keysToRemove =
                                    sysObj
                                    |> Seq.filter (fun kv ->
                                        kv.Key.StartsWith("flow ") && kv.Key <> flowKey)
                                    |> Seq.map (fun kv -> kv.Key)
                                    |> Seq.toList
                                for k in keysToRemove do
                                    sysObj.Remove(k) |> ignore
                                    truncated := true
                                // 추가로 system 의 arrows (cross-flow arrows 가 있으면) 도 path 외부 → 제거
                                if sysObj.ContainsKey("arrows") then
                                    sysObj.Remove("arrows") |> ignore
                                    truncated := true
                                // segs[3] = work 안 필터
                                if segs.Length >= 4 then
                                    match sysObj.TryGetPropertyValue(flowKey) with
                                    | true, (:? JsonObject as flowObj) ->
                                        match flowObj.TryGetPropertyValue("works") with
                                        | true, (:? JsonObject as worksObj) ->
                                            let workKeys =
                                                worksObj
                                                |> Seq.map (fun kv -> kv.Key)
                                                |> Seq.toList
                                            for wk in workKeys do
                                                if wk <> segs.[3] then
                                                    worksObj.Remove(wk) |> ignore
                                                    truncated := true
                                            // flow level arrows 도 제거 (work 한정 scope)
                                            if flowObj.ContainsKey("arrows") then
                                                flowObj.Remove("arrows") |> ignore
                                                truncated := true
                                            // segs[4] = call 필터 (calls[] 안 string 항목)
                                            if segs.Length >= 5 then
                                                match worksObj.TryGetPropertyValue(segs.[3]) with
                                                | true, (:? JsonObject as workObj) ->
                                                    match workObj.TryGetPropertyValue("calls") with
                                                    | true, (:? JsonArray as callsArr) ->
                                                        let kept = ResizeArray<JsonNode>()
                                                        let mutable removed = false
                                                        let orig = callsArr |> Seq.toArray
                                                        for cn in orig do
                                                            // call 문자열은 "SysName.ApiName" — `.ApiName` suffix 비교
                                                            let s = if cn = null then "" else cn.ToString()
                                                            let lastDot = s.LastIndexOf('.')
                                                            let apiPart = if lastDot >= 0 then s.Substring(lastDot + 1) else s
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
    /// `baseDepth` = scope entity 가 project root 로부터 떨어진 depth (0=project, 1=system, 2=flow/api, 3=work, 4=call).
    /// `maxAbsDepth` = baseDepth + d. JSON tree 의 각 노드 절대 depth 와 비교.
    /// 절대 depth 매핑: project=0, system entity=1, system content(flow X 키/device/apis 등)=2,
    /// flow content(works obj / arrows)=3, work content(calls 배열)=4, call element(string)=5.
    let private applyDepthCap (root: JsonObject) (maxAbsDepth: int) (truncated: bool ref) : unit =
        // depth=0 : systems 배열 제거 (envelope only)
        if maxAbsDepth < 1 then
            match root.TryGetPropertyValue("systems") with
            | true, (:? JsonArray as sa) when sa.Count > 0 ->
                truncated := true
                sa.Clear()
            | _ -> ()
        else
            // depth>=1: systems[] 안 각 system entry 정리
            match root.TryGetPropertyValue("systems") with
            | true, (:? JsonArray as systemsArr) ->
                for node in systemsArr do
                    match node with
                    | :? JsonObject as sysObj ->
                        // 절대 depth 2 = system content (flow X 키 / apis / device / arrows / workDuration / opposing)
                        if maxAbsDepth < 2 then
                            // system identity 만 유지: system, kind, device (passive identity 보존)
                            let keysToRemove =
                                sysObj
                                |> Seq.filter (fun kv ->
                                    kv.Key <> "system" && kv.Key <> "kind" && kv.Key <> "device")
                                |> Seq.map (fun kv -> kv.Key)
                                |> Seq.toList
                            if not keysToRemove.IsEmpty then truncated := true
                            for k in keysToRemove do
                                sysObj.Remove(k) |> ignore
                        else
                            // depth>=2: flow content (works/arrows) 절단
                            if maxAbsDepth < 3 then
                                // active: 각 "flow X" obj 내부 비우기
                                let flowKeys =
                                    sysObj
                                    |> Seq.filter (fun kv -> kv.Key.StartsWith("flow "))
                                    |> Seq.map (fun kv -> kv.Key)
                                    |> Seq.toList
                                for fk in flowKeys do
                                    match sysObj.TryGetPropertyValue(fk) with
                                    | true, (:? JsonObject as flowObj) when flowObj.Count > 0 ->
                                        truncated := true
                                        flowObj.Clear()
                                    | _ -> ()
                                // system level arrows 도 절단 — Phase 6 에선 active 의 cross-flow arrows
                                if sysObj.ContainsKey("arrows") then
                                    truncated := true
                                    sysObj.Remove("arrows") |> ignore
                            elif maxAbsDepth < 4 then
                                // depth=3: flow content 유지 (works obj) — works 안 work entry 비우기
                                let flowKeys =
                                    sysObj
                                    |> Seq.filter (fun kv -> kv.Key.StartsWith("flow "))
                                    |> Seq.map (fun kv -> kv.Key)
                                    |> Seq.toList
                                for fk in flowKeys do
                                    match sysObj.TryGetPropertyValue(fk) with
                                    | true, (:? JsonObject as flowObj) ->
                                        match flowObj.TryGetPropertyValue("works") with
                                        | true, (:? JsonObject as worksObj) ->
                                            // 각 work entry 의 calls 제거 (work identity 만 유지)
                                            for kv in (worksObj |> Seq.toArray) do
                                                match kv.Value with
                                                | :? JsonObject as workObj ->
                                                    if workObj.ContainsKey("calls") then
                                                        truncated := true
                                                        workObj.Remove("calls") |> ignore
                                                | _ -> ()
                                        | _ -> ()
                                    | _ -> ()
                            // depth>=4: calls 까지 모두 유지 — 추가 절단 없음
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
                // passive system 의 apidef 카운트 (apis[] 의 string 각 항목)
                match sysObj.TryGetPropertyValue("apis") with
                | true, (:? JsonArray as apisArr) -> c <- c + apisArr.Count
                | _ -> ()
                for kv in sysObj do
                    if kv.Key.StartsWith("flow ") then
                        c <- c + 1
                        match kv.Value with
                        | :? JsonObject as flowObj ->
                            match flowObj.TryGetPropertyValue("works") with
                            | true, (:? JsonObject as worksObj) ->
                                for wkv in worksObj do
                                    c <- c + 1
                                    match wkv.Value with
                                    | :? JsonObject as workObj ->
                                        match workObj.TryGetPropertyValue("calls") with
                                        | true, (:? JsonArray as callsArr) ->
                                            c <- c + callsArr.Count
                                        | _ -> ()
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

    /// `exportToJsonScoped` — partial export entry (SSOT §2.8).
    /// 두 인자 모두 None → `exportToJson` delegate (`view: full`, budget 0, 무제한).
    /// 그 외 partial entry — full export 받아 path/depth/budget post-process, 실제 truncation
    /// 1건 이상 시 `view: partial`. path Some + 미존재 = fail-fast (VALIDATION_ERROR).
    let exportToJsonScoped (store: DsStore) (pathOpt: string option) (depthOpt: int option) : JsonDocument =
        match pathOpt, depthOpt with
        | None, None -> exportToJson store
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

            use fullDoc = exportToJson store
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
