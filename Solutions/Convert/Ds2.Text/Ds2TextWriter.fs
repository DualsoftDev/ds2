namespace Ds2.Text

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Ds2.Core
open Ds2.Core.Store

/// Export policy; ordinary human-readable output remains unchanged by default.
type Ds2TextOptions =
    { WriteIdentity: bool }
    static member Default = { WriteIdentity = false }

/// `DsStore` → 정규 DS2 Text 5 원문.
///
/// 참조 구현은 두 곳이며, 이 모듈은 그 둘의 **역방향**이다.
///   · 배치·생략 규칙 : `ds2_text/ds2text/text.py` 의 `format_text`
///   · 저장 대응 규칙 : `ds2_text/native/Program.fs` (IR → DsStore 어댑터)
///
/// native 어댑터가 정한 대응을 그대로 되짚는다 — 여기서 어긋나면 왕복이 깨진다.
///   · Text 의 call        ↔ `Call.Name` ("장비.기능" — 손잡이는 없다)
///   · Call 의 API 대상    ↔ `ApiCall.Name` ("System.Api" 문자열 그대로)
///   · Work 의 finished    ↔ `SimulationWorkProperties.IsFinished = true` (저장된 완료 비트)
///   · Call 의 `skip when completed;` ↔ `SimulationCallProperties.CallType = SkipIfCompleted`(#8963 — 옛 `completion existing`)
///   · Call 의 timeout·Work 의 limits·저장 Source/Sink 역할은 ds2 5 Text 에 **없다**(2026-10-20 #8962 — 문법에서 뺐다).
///     SDF 에만 남고 Text 투영에서는 빠진다 — 빠진 값은 Studio 의 투영 감사(auditTextExport)가 손실로 알린다.
///   · 기존 Call.SequenceLabel 은 SDF/보존 번들에만 남는다. DS 인과 Text는 KPI 경계를 수동 선언하지 않는다.
///     기본값이 아닌 과거 라벨에는 투영 진단 주석을 붙이며, Text 단독으로 원본 전체가 보존된다고 보장하지 않는다.
///   · 기존 Call.Interlocked도 SDF/보존 번들에만 남는다. 실제 운전 사유별 감시 자격으로 이관하는 것은 별도다.
///
/// 이 모듈은 그래프를 추론하지 않는다. 저장된 화살표·선언을 순서 그대로 적되, ds2 5 의 표기로 적는다:
///   · 같은 과녁으로 드는 Start 간선은 **한 문장**(괄호 공동 원인) — 내부 진입 Work(ReferenceOf·AutoEntry)의
///     간선은 canonical 이름으로 적어 **별도 문장 = 독립 진입**을 되살린다
///   · Reset 은 source 별 `S |> (A, B);`, `<|>` 는 쌍마다, 1:1 Start 사슬은 `A > B > C;`
///   · `ref`·`group`·좌표(`at`)·`bind`·`command none`·`action virtual` 은 적지 않는다 — Group 간선·action Virtual 은 거절한다
[<RequireQualifiedAccess>]
module Ds2TextWriter =

    let private q  = Ds2TextLexeme.quote
    let private jl = Ds2TextLexeme.jsonString

    /// 이름 차례 — 인과가 없을 때의 바닥. **결정적이어야** diff 가 뜻을 가진다.
    let private nameOrder (items: seq<'T>) : 'T list when 'T :> DsChild =
        items
        |> Seq.sortWith (fun a b ->
            let name = StringComparer.Ordinal.Compare(a.Name, b.Name)
            if name <> 0 then name else compare a.Id b.Id)
        |> List.ofSeq

    /// 선언 차례 — **인과가 있으면 위상 차례**, 없으면 이름 차례.
    ///
    /// 전에는 `DsChild.Ordinal`(저장된 「적은 차례」)로 정렬했다. 지웠다 —
    /// 그 정보를 **간선이 이미 들고 있기** 때문이다. 사슬 공정이면 위상 차례가
    /// 원문 차례를 그대로 되살린다. 갈래가 지거나 인과가 아예 없으면 못 되살리지만,
    /// 그때는 **원문 차례에 뜻이 없다** — 나란히 가는 것들이라 순서가 자의적이다.
    ///
    /// 고리가 있으면 위상이 성립하지 않는다. 남은 것은 이름 차례로 떨어뜨린다(결정적).
    let private topoOrder (predsOf: Guid -> Guid seq) (items: seq<'T>) : 'T list when 'T :> DsChild =
        let sorted = nameOrder items
        let ids = HashSet<Guid>(sorted |> Seq.map (fun x -> x.Id))
        let remaining = Dictionary<Guid, HashSet<Guid>>()
        for x in sorted do
            remaining.[x.Id] <- HashSet<Guid>(predsOf x.Id |> Seq.filter ids.Contains)
        let out = ResizeArray<'T>()
        let pending = ResizeArray<'T>(sorted)
        let mutable moving = true
        while pending.Count > 0 && moving do
            // pending 은 이미 이름 차례다 — 「준비된 것 중 첫째」를 꺼내면 결정적이다
            match pending |> Seq.tryFindIndex (fun x -> remaining.[x.Id].Count = 0) with
            | Some i ->
                let x = pending.[i]
                pending.RemoveAt i
                out.Add x
                for set in remaining.Values do set.Remove x.Id |> ignore
            | None -> moving <- false
        out.AddRange pending                     // 고리에 걸린 것들 — 이름 차례 그대로
        List.ofSeq out

    /// 인과가 없는 것(Flow·ApiDef)은 이름 차례뿐이다.
    let private declarationOrder (items: seq<'T>) : 'T list when 'T :> DsChild =
        nameOrder items

    // ─────────────────────────────────────────────────────────── 경로
    //  Work 경로는 Flow 안에서는 `이름`, 밖에서는 `Flow.이름`.
    //  Call 은 `장비.기능` 으로 적는다. 인과는 같은 Work 안에서만 그으므로 경로가 없다.

    let private workPath (currentFlow: string option) (flowName: string) (localName: string) =
        match currentFlow with
        | Some f when f = flowName -> q localName
        | _ -> q flowName + "." + q localName

    // ─────────────────────────────────────────────────────────── 관계 (ds2 5)
    //  · Start/StartReset: **과녁(host)별 한 문장** `(A, B) op T;` — host 가 내부 진입 Work 면 canonical 이름으로
    //    적어 별도 문장(독립 진입)이 된다. 1:1 사슬은 `A > B > C;` 로 이어 붙인다(컴파일러가 다시 쌍으로 가른다).
    //  · Reset: source 별 `S |> (A, B);`   · ResetReset: 쌍마다 `A <|> B;`
    //  · Group 간선(type 5)은 ds2 5 에 없다 — 거절한다. 어느 쪽도 뜻을 만들지 않는다.

    let private operatorOf (t: ArrowType) =
        match t with
        | ArrowType.Start      -> ">"
        | ArrowType.Reset      -> "|>"
        | ArrowType.StartReset -> "=>"
        | ArrowType.ResetReset -> "<|>"
        | ArrowType.Group      ->
            failwith "DS2TEXT_GROUP: ds2 5 에는 group 이 없습니다 — Group 간선(type 5)은 적을 수 없습니다. 공동 원인은 같은 과녁의 Start 간선(괄호)으로, 독립 진입은 별도 문장으로 적습니다."
        | other -> failwithf "DS2 Text 로 적을 수 없는 화살표입니다: %A" other

    let private joinNames (names: string list) =
        match names with
        | [ one ] -> one
        | many -> "(" + String.concat ", " many + ")"

    /// `rankOf` 는 canonical Work 의 위상 차례(선언 차례)다 — 문장을 입력 순서가 아니라 이 차례로 적어야 같은 모델이 늘 같은 글이 된다(고정점).
    let private relationLines (arrows: DsArrow list) (nameOf: Guid -> string) (canonicalOf: Guid -> Guid) (rankOf: Guid -> int) : string list =
        let starts = Dictionary<struct (ArrowType * Guid), ResizeArray<string>>()
        let startOrder = ResizeArray<struct (ArrowType * Guid)>()
        let resets = Dictionary<Guid, ResizeArray<string>>()
        let resetOrder = ResizeArray<Guid>()
        let mutuals = ResizeArray<string>()
        for a in arrows do
            match a.ArrowType with
            | ArrowType.Start | ArrowType.StartReset ->
                let key = struct (a.ArrowType, a.TargetId)
                if not (starts.ContainsKey key) then
                    starts.[key] <- ResizeArray<string>()
                    startOrder.Add key
                let source = nameOf (canonicalOf a.SourceId)
                if not (starts.[key].Contains source) then starts.[key].Add source
            | ArrowType.Reset ->
                if not (resets.ContainsKey a.SourceId) then
                    resets.[a.SourceId] <- ResizeArray<string>()
                    resetOrder.Add a.SourceId
                let target = nameOf (canonicalOf a.TargetId)
                if not (resets.[a.SourceId].Contains target) then resets.[a.SourceId].Add target
            | ArrowType.ResetReset ->
                mutuals.Add(nameOf (canonicalOf a.SourceId) + " <|> " + nameOf (canonicalOf a.TargetId) + ";")
            | other -> operatorOf other |> ignore
        let statements =
            [| for key in startOrder do
                 let struct (kind, host) = key
                 let sources = starts.[key] |> Seq.sortBy (fun n -> n) |> List.ofSeq
                 yield kind, sources, nameOf (canonicalOf host), rankOf (canonicalOf host) |]
            |> Array.sortBy (fun (kind, _, target, rank) -> rank, (if kind = ArrowType.Start then 0 else 1), target)
            |> Array.map (fun (kind, sources, target, _) -> kind, sources, target)
        let out = ResizeArray<string>()
        let mutable i = 0
        while i < statements.Length do
            let (kind, sources, target) = statements.[i]
            let names = ResizeArray<string>([ joinNames sources; target ])
            i <- i + 1
            if sources.Length = 1 then
                let mutable chaining = true
                while chaining && i < statements.Length do
                    let (k2, s2, t2) = statements.[i]
                    if k2 = kind && s2 = [ names.[names.Count - 1] ] then
                        names.Add t2
                        i <- i + 1
                    else chaining <- false
            out.Add((names |> String.concat (" " + operatorOf kind + " ")) + ";")
        for source in resetOrder |> Seq.sortBy (fun s -> rankOf (canonicalOf s), nameOf (canonicalOf s)) do
            out.Add(nameOf (canonicalOf source) + " |> " + joinNames (resets.[source] |> Seq.sortBy id |> List.ofSeq) + ";")
        out.AddRange(mutuals |> Seq.sortBy id)
        List.ofSeq out

    // ─────────────────────────────────────────────────────────── 조건식
    //  Condition 트리 → `when` / `while` / `skip when` 의 오른쪽.
    //  AutoAux/ComAux의 구별과 전체 Boolean 트리를 보존한다. leaf 접점으로 바꾸지 않는다.

    let private contactPrefix (kind: ContactKind) =
        match kind with
        | ContactKind.NoContact    -> ""
        | ContactKind.NcContact    -> "nc "
        | ContactKind.RisingPulse  -> "rise "
        | ContactKind.FallingPulse -> "fall "
        | other -> raise (NotSupportedException(sprintf "DS2TEXT_CONDITION_CONTACT: DS2 Text로 보존할 수 없는 조건 접점입니다: %A" other))

    /// JSON 리터럴 글 그대로 — 제품 사양·변수 값은 글에 적힌 모양(true · 25 · "B")으로 저장된다.
    let private rawLiteral (e: JsonElement) = e.GetRawText()

    /// leafReference는 실제 ApiDef 대상과 공유 입력(from)의 일치를 검증한다.
    /// `tokenOnly` 는 `skip when` 자리다 — ds2 5 의 skip 은 제품 사양만 읽으므로 api 잎을 거절한다.
    /// `where` 는 사람이 고칠 자리다(`work F.검사 · skip when`) — 거절 메시지가 GUID 만 적으면 어느 줄인지 알 수 없다(#8938).
    let rec private expressionText (where: string) (leafReference: ApiCall -> string * string option) (tokenOnly: bool) (c: Condition) : string =
        // 런타임은 직접 ApiCall과 자식을 함께 평가하지만 native Text의 정규 트리는
        // 단일 leaf 또는 자식 그룹이다. 누락하거나 상수로 바꾸지 않고 지원 경계를 알린다.
        let unsupported reason =
            raise (NotSupportedException(sprintf "DS2TEXT_CONDITION_SHAPE: %s 의 조건(%O)을 DS2 Text로 보존할 수 없습니다: %s" where c.Id reason))
        let leafCount = c.ApiCalls.Count + (if c.Token.IsSome then 1 else 0) + (if c.Variable.IsSome then 1 else 0)
        if leafCount > 0 && c.Children.Count > 0 then
            unsupported "직접 잎(ApiCall·token·variable)과 자식 조건이 혼합되어 있습니다."
        if leafCount > 1 then
            unsupported "하나의 조건 노드에 여러 직접 잎이 있습니다."
        if leafCount = 0 && c.Children.Count = 0 then
            unsupported "빈 조건은 표현 가능한 비교식이 없으며 true로 대체할 수 없습니다."
        if c.Children.Count > 0 && c.IsInverted && leafCount = 0 && c.Children.Count = 1 then
            "not " + expressionText where leafReference tokenOnly c.Children.[0]
        elif c.Children.Count > 0 then
            let joiner = if c.IsOR then " or " else " and "
            let body =
                c.Children
                |> Seq.map (expressionText where leafReference tokenOnly)
                |> String.concat joiner
            let wrapped = "(" + body + ")"
            if c.IsInverted then "not " + wrapped else wrapped
        elif c.Token.IsSome then
            // 제품 사양 잎 — `token.필드 == 값` · `!=` · `in {…}`
            let t = c.Token.Value
            let head = "token." + q t.Field
            let body =
                match t.Op, t.Values with
                | "in", values -> head + " in {" + (values |> List.map rawLiteral |> String.concat ", ") + "}"
                | "ne", value :: _ -> head + " != " + rawLiteral value
                | _, value :: _ -> head + " == " + rawLiteral value
                | _ -> unsupported "token 조건에 비교값이 없습니다."
            if c.IsInverted then "not " + body else body
        elif c.Variable.IsSome then
            if tokenOnly then unsupported "ds2 5 의 skip 은 제품 사양(token)만 읽습니다 — 변수 잎은 skip 에 올 수 없습니다."
            let v = c.Variable.Value
            let head = "variable." + q v.Name
            match Ds2TextSpec.tryMatchLiteral v.InputSpec with
            | Some literal -> head + (if c.IsInverted then " != " else " == ") + literal
            | None -> (if c.IsInverted then "not " else "") + head + " as " + Ds2TextSpec.format "==" v.InputSpec
        elif c.ApiCalls.Count = 1 then
            if tokenOnly then unsupported "ds2 5 의 skip 은 제품 사양(token)만 읽습니다 — 센서(api) 잎은 skip 에 올 수 없습니다. when 으로 기다리거나 product 필드로 적으십시오."
            let a = c.ApiCalls.[0]
            let target, owner = leafReference a
            let source = owner |> Option.map (fun path -> " from " + path) |> Option.defaultValue ""
            // 뒤집힌 단일값 잎은 `!=` 로 적는다 — `not X == v` 와 같은 저장 결과이고, 접점(nc)과 겹치는 `not` 경고를 피한다.
            let op = if c.IsInverted then "!=" else "=="
            let leaf =
                match source, Ds2TextSpec.tryMatchLiteral a.InputSpec with
                // 기존 match 조건은 타입 없이 `Api == 값` 으로 남는다. from 이 붙으면 typed 조건이다.
                | "", Some literal -> target + " " + op + " " + literal
                | _ ->
                    match a.InputSpec with
                    | BoolValue (Single _) | Int8Value (Single _) | Int16Value (Single _) | Int32Value (Single _) | Int64Value (Single _)
                    | UInt8Value (Single _) | UInt16Value (Single _) | UInt32Value (Single _) | UInt64Value (Single _)
                    | Float32Value (Single _) | Float64Value (Single _) | StringValue (Single _) ->
                        target + source + " as " + Ds2TextSpec.format op a.InputSpec
                    | _ ->
                        (if c.IsInverted then "not " else "") + target + source + " as " + Ds2TextSpec.format "==" a.InputSpec
            contactPrefix a.ContactKind + leaf
        else
            unsupported "지원하지 않는 조건 구조입니다."

    let private conditionsOf (conditions: ResizeArray<Condition>) (kind: ConditionType) =
        conditions |> Seq.filter (fun c -> c.Type = Some kind) |> List.ofSeq

    /// 2026-10-20 Q47(#8963) → 2026-10-21 FS1 70 — `skip when` 은 **한 절**이다. OR 노드(글의 `or`)는 `or` 한 절로 되쓰고,
    /// 저장 목록의 여러 SkipAction(옛 SDF 의 **AND**)은 조용히 OR 로 읽히지 않게 `and` 로 이은 **한 절**로 명시 이관한다.
    let private skipClauses (render: Condition -> string) (conditions: Condition list) : string list =
        let isOrGroup (c: Condition) =
            c.IsOR && not c.IsInverted && c.Children.Count >= 2
            && c.ApiCalls.Count = 0 && c.Token.IsNone && c.Variable.IsNone
        // 2026-10-21 FS1 70(CAT-70-R07·R08) — 같은 종류의 조건은 **한 절**로만 적는다: OR 루트는 `or` 한 절(절로 펼치지 않는다),
        //   저장 목록(옛 AND)은 괄호로 감싼 `and` 한 절. isOrGroup 은 판별 기록용으로 남긴다.
        match conditions with
        | [] -> []
        | [ single ] -> [ render single ]
        | many -> [ many |> List.map (fun c -> "(" + render c + ")") |> String.concat " and " ]

    /// product 필드 값 — ds2 5 컴파일러는 JSON 리터럴 글(`true`·`25`·`"B"`)로 저장한다. 그 꼴이면 그대로 적고,
    /// 리터럴이 아닌 옛 문자열은 문자열 리터럴로 감싼다(그러면 종류가 string 이 된다 — 그것이 그 값의 사실이다).
    let private fieldLiteral (value: string) =
        let v = if isNull value then "" else value.Trim()
        if v = "true" || v = "false" then v
        elif v.Length >= 2 && v.StartsWith "\"" && v.EndsWith "\"" then v
        elif Text.RegularExpressions.Regex.IsMatch(v, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$") then v
        else jl value

    // ─────────────────────────────────────────────────────────── 본문

    /// `projectId` 프로젝트를 정규 DS2 Text v5 원문으로 적는다.
    let writeWithOptions (store: DsStore) (projectId: Guid) (options: Ds2TextOptions) : string =
        let emittedIds = HashSet<Guid>()
        let identity (id: Guid) =
            if not options.WriteIdentity then ""
            elif id = Guid.Empty || not (emittedIds.Add id) then
                raise (NotSupportedException(sprintf "DS2TEXT_IDENTITY: Empty or duplicate declaration ID %O" id))
            else " #" + jl (id.ToString("D"))
        let project =
            match store.Projects.TryGetValue projectId with
            | true, p -> p
            | _ -> failwithf "프로젝트를 찾을 수 없습니다: %O" projectId

        // ── 색인 ──
        let systemOfId = Dictionary<Guid, DsSystem>()
        for s in store.Systems.Values do systemOfId.[s.Id] <- s
        let flowOfId = Dictionary<Guid, Flow>()
        for f in store.Flows.Values do flowOfId.[f.Id] <- f
        let workOfId = Dictionary<Guid, Work>()
        for w in store.Works.Values do workOfId.[w.Id] <- w
        let callOfId = Dictionary<Guid, Call>()
        for c in store.Calls.Values do callOfId.[c.Id] <- c

        /// 내부 진입 Work(ReferenceOf) → 원본. 사슬은 끝까지 따라간다 — 글에는 원본 이름만 적는다.
        let canonicalOf (id: Guid) =
            let seen = HashSet<Guid>()
            let mutable current = id
            let mutable go = true
            while go do
                match workOfId.TryGetValue current with
                | true, w when w.ReferenceOf.IsSome && seen.Add current -> current <- w.ReferenceOf.Value
                | _ -> go <- false
            current

        let flowOfWork (w: Work) =
            match flowOfId.TryGetValue w.ParentId with
            | true, f -> Some f
            | _ -> None

        let systemOfFlow (f: Flow) =
            match systemOfId.TryGetValue f.ParentId with
            | true, s -> Some s
            | _ -> None

        /// Work 의 "System/Flow/Work" 3단 이름 (경로 인용 전 원본 문자열).
        let workParts (w: Work) =
            match flowOfWork w with
            | Some f ->
                let sysName = systemOfFlow f |> Option.map (fun s -> s.Name) |> Option.defaultValue ""
                Some (sysName, f.Name, w.LocalName)
            | None -> None

        // 손잡이가 없다 — **`장비.기능` 이 곧 이름**이다.
        //
        // 전에는 `Call.TextName`(사용자가 쓴 손잡이)을 보존하고, 없으면 별칭에서
        // 합성했다(`로봇` · 겹치면 `로봇_집기` · 또 겹치면 `_2`). 그 「또 겹치면」이
        // 한 Work 안에 같은 `장비.기능` 이 둘인 경우였고, 그것은 Call 레퍼런스로만
        // 만들어졌다. 레퍼런스를 글에서 없애면서 겹칠 일 자체가 사라졌다.
        let textNameOf (c: Call) = c.Name
        /// 글에 적는 꼴 — `장비.기능` 두 토막을 각각 따옴표 규칙에 맞춘다.
        let callText (c: Call) = Ds2TextLexeme.path (c.Name.Split '.')

        // 묶음 이름은 저장소에 없다 — 글로 적을 때 `group1`·`group2` 로 짓는다.
        // 전에는 `DsGroup` 엔티티로 이름을 보존하고, 그것이 저장된 Group 간선과
        // 어긋나지 않는지 60줄로 검증했다. 얻는 것이 이름뿐이라 통째로 들어냈다.

        let projectSystemIds = HashSet<Guid>(Seq.append project.ActiveSystemIds project.PassiveSystemIds)
        // 같은 id를 여러 Call이 소유하면 last-wins로 from을 고르지 않는다.
        // 경로 없는 소유자도 남겨 독립 leaf로 오인하지 않게 한다.
        let apiCallOwners = Dictionary<Guid, ResizeArray<ApiCall * string option>>()
        for c in store.Calls.Values do
            let path =
                match workOfId.TryGetValue c.ParentId with
                | true, w ->
                    match workParts w, (flowOfWork w |> Option.bind systemOfFlow) with
                    | Some (sysName, flowName, workName), Some sys when projectSystemIds.Contains sys.Id ->
                        Some (Ds2TextLexeme.path [ sysName; flowName; workName; c.DevicesAlias; c.ApiName ])
                    | _ -> None
                | _ -> None
            for a in c.ApiCalls do
                match apiCallOwners.TryGetValue a.Id with
                | true, owners -> owners.Add(a, path)
                | _ -> apiCallOwners.[a.Id] <- ResizeArray([ a, path ])

        let conditionReference (leaf: ApiCall) =
            let unsupported reason =
                raise (NotSupportedException(sprintf "DS2TEXT_CONDITION_TARGET: 조건 ApiCall %O을 DS2 Text로 보존할 수 없습니다: %s" leaf.Id reason))
            let resolveTarget (a: ApiCall) =
                let parts = if isNull a.Name then [||] else a.Name.Split '.'
                if parts.Length <> 2 || (parts |> Array.exists String.IsNullOrWhiteSpace) then
                    unsupported "대상은 명시적인 System.Api 이름이어야 합니다. raw 상수 또는 잘못된 경로는 지원하지 않습니다."
                let api =
                    match a.ApiDefId with
                    | Some id ->
                        match store.ApiDefs.TryGetValue id with
                        | true, value when value.Id = id -> value
                        | _ -> unsupported "ApiDefId가 실제 API 선언을 가리키지 않습니다."
                    | None -> unsupported "ApiDefId가 없는 조건 leaf는 지원하지 않습니다."
                let sys =
                    match systemOfId.TryGetValue api.ParentId with
                    | true, value when projectSystemIds.Contains value.Id -> value
                    | _ -> unsupported "API의 System이 출력 프로젝트에 존재하지 않습니다."
                if parts.[0] <> sys.Name || parts.[1] <> api.Name then
                    unsupported "Name과 ApiDefId가 가리키는 실제 System/API 이름이 다릅니다."
                api.Id, Ds2TextLexeme.path [ sys.Name; api.Name ]
            let targetId, target = resolveTarget leaf
            let source =
                match apiCallOwners.TryGetValue leaf.Id with
                | false, _ -> None
                | true, owners when owners.Count = 1 ->
                    let original, path = owners.[0]
                    let originalId, _ = resolveTarget original
                    if originalId <> targetId then
                        unsupported "from 입력을 공유하는 원본 ApiCall과 조건 leaf의 API 대상이 다릅니다."
                    if path.IsNone then unsupported "from 입력 소유자의 경로를 출력 프로젝트에서 찾을 수 없습니다."
                    path
                | _ -> unsupported "여러 Call이 같은 ApiCall id를 소유하여 from 경로가 모호합니다."
            target, source

        // ── 출력 버퍼 ──
        let lines = ResizeArray<string>()
        let emit (indent: int) (text: string) = lines.Add(String.replicate indent " " + text)
        let emitAll (indent: int) (texts: string seq) = for t in texts do emit indent t

        lines.Add "ds2 5;"
        lines.Add ("project " + q project.Name + identity project.Id + " {")
        if project.Author <> "" then emit 2 ("author " + jl project.Author + ";")
        if project.Version <> "1.0.0" then emit 2 ("version " + jl project.Version + ";")
        if project.DateTime <> DateTimeOffset.UnixEpoch || project.DateTime.Offset <> TimeSpan.Zero then
            emit 2 ("datetime " + jl (project.DateTime.ToString("O", Globalization.CultureInfo.InvariantCulture)) + ";")

        // ── product (TokenSpec) ──
        // 2026-10-20 Q46(#8958): `product … from 시스템.흐름.일` 은 ds2 5 에서 거절된다 — 제품 선택은 지령 api 의
        //   `product N;` 한 곳이다. 그래서 옛 SDF 의 TokenSpec.WorkId 는 Text 로 내지 않는다(SDF 에만 남고,
        //   Studio 의 투영 감사가 그 경로를 손실로 알린다).
        for spec in project.TokenSpecs do
            if spec.Fields.IsEmpty then
                emit 2 (sprintf "product %d %s {" spec.Id (jl spec.Label))
                emit 2 "}"
            else
                emit 2 (sprintf "product %d %s {" spec.Id (jl spec.Label))
                for KeyValue (name, value) in spec.Fields do
                    emit 4 (q name + " = " + fieldLiteral value + ";")
                emit 2 "}"

        // ── system (active 먼저, 그다음 passive) ──
        let orderedSystems =
            [ for id in project.ActiveSystemIds do
                match systemOfId.TryGetValue id with
                | true, s -> yield s, true
                | _ -> ()
              for id in project.PassiveSystemIds do
                match systemOfId.TryGetValue id with
                | true, s -> yield s, false
                | _ -> () ]

        for (sys, isActive) in orderedSystems do
            emit 2 ("system " + q sys.Name + identity sys.Id + (if isActive then " active" else "") + " {")
            sys.IRI |> Option.iter (fun value -> emit 4 ("iri " + jl value + ";"))
            sys.SystemType |> Option.iter (fun t -> emit 4 ("type " + jl t + ";"))

            // ── variable — active System 의 기억 (ds2 5). 옵션은 기본값(false·없음)이 아닌 것만 적는다.
            let rawOpt (e: JsonElement option) = e |> Option.map rawLiteral
            for v in store.Variables.Values |> Seq.filter (fun v -> v.ParentId = sys.Id) |> declarationOrder do
                let initial =
                    match rawOpt v.InitialValue with
                    | Some text -> text
                    | None -> failwithf "DS2TEXT_VARIABLE: 변수 %s (%O) 에 초기값이 없습니다 — ds2 5 의 variable 은 초기값이 필수입니다." v.Name v.Id
                let options = ResizeArray<string>()
                if v.Readable then options.Add "readable true;"
                if v.Writable then options.Add "writable true;"
                if v.Retain then options.Add "retain true;"
                rawOpt v.Min |> Option.iter (fun m -> options.Add("min " + m + ";"))
                rawOpt v.Max |> Option.iter (fun m -> options.Add("max " + m + ";"))
                let head = "variable " + v.ValueType + " " + q v.Name + identity v.Id + " = " + initial
                if options.Count = 0 then emit 4 (head + ";")
                else
                    emit 4 (head + " {")
                    for o in options do emit 6 o
                    emit 4 "}"

            let flows = store.Flows.Values |> Seq.filter (fun f -> f.ParentId = sys.Id) |> declarationOrder

            // Work 화살표: 같은 Flow 안이면 그 Flow 에, 다르면 System 레벨에 적는다.
            let sysArrows =
                store.ArrowWorks.Values
                |> Seq.filter (fun a -> a.ParentId = sys.Id)
                |> Seq.filter (fun a -> workOfId.ContainsKey a.SourceId && workOfId.ContainsKey a.TargetId)
                |> List.ofSeq
            let flowIdOf (id: Guid) = workOfId.[canonicalOf id].ParentId
            // FS1 S-17 — 한 진입(같은 과녁·같은 Start 계열 = relationLines 의 한 문장)은 한 자리에 적는다.
            // 원인 하나라도 다른 Flow 면 그 진입 **전체**를 System 레벨에 적는다. 화살표마다 나누면
            // `(F.A, H.C) > H.X;` 가 System 의 `F.A > H.X;` 와 Flow 안 `C > X;` 로 갈라지고,
            // 다시 읽으면 독립 진입 둘(OR)이 되어 AND 가 바뀐다. Reset·ResetReset 은 과녁별로 독립이라 그대로 둔다.
            let isStartLike (a: ArrowBetweenWorks) = a.ArrowType = ArrowType.Start || a.ArrowType = ArrowType.StartReset
            let mixedEntries =
                sysArrows
                |> List.filter (fun a -> isStartLike a && flowIdOf a.SourceId <> flowIdOf a.TargetId)
                |> List.map (fun a -> a.ArrowType, a.TargetId)
                |> Set.ofList
            let atSystem (a: ArrowBetweenWorks) =
                flowIdOf a.SourceId <> flowIdOf a.TargetId
                || (isStartLike a && mixedEntries.Contains((a.ArrowType, a.TargetId)))
            let crossFlow = sysArrows |> List.filter atSystem
            let workNameAbs (id: Guid) =
                let w = workOfId.[id]
                match flowOfWork w with
                | Some f -> workPath None f.Name w.LocalName
                | None -> q w.LocalName
            // Work 차례는 **인과가 정한다**(flow 안 Start 계열 선행의 위상 차례). 관계문도 이 차례로 적는다.
            let flowWorkPreds (flow: Flow) (id: Guid) =
                sysArrows
                |> Seq.filter (fun a ->
                    canonicalOf a.TargetId = id
                    && (a.ArrowType = ArrowType.Start || a.ArrowType = ArrowType.StartReset)
                    && flowIdOf a.SourceId = flow.Id)
                |> Seq.map (fun a -> canonicalOf a.SourceId)
            let orderedWorks (flow: Flow) =
                store.Works.Values
                |> Seq.filter (fun w -> w.ParentId = flow.Id && w.ReferenceOf.IsNone)
                |> topoOrder (flowWorkPreds flow)
            let rank = Dictionary<Guid, int>()
            for flow in flows do
                for w in orderedWorks flow do rank.[w.Id] <- rank.Count
            let rankOf (id: Guid) = match rank.TryGetValue id with | true, r -> r | _ -> Int32.MaxValue
            emitAll 4 (relationLines (crossFlow |> List.map (fun a -> a :> DsArrow)) workNameAbs canonicalOf rankOf)

            let writeFlow (flow: Flow) =
                emit 4 ("flow " + q flow.Name + identity flow.Id + " {")
                if flow.IsDisabled then emit 6 "enabled false;"
                if not flow.IsAuto then emit 6 "auto false;"

                // Work 차례는 **인과가 정한다**. 같은 Flow 안의 Start 계열 간선이 선행이다.
                // 내부 진입 Work(ReferenceOf)는 글에 없다 — 그 간선은 canonical 의 별도 문장(독립 진입)으로 적힌다.
                // 제 속성을 가진 참조 Work(옛 `work X = ref Y { … }`)는 그렇게 옮길 수 없으므로 거절한다.
                for alias in store.Works.Values |> Seq.filter (fun w -> w.ParentId = flow.Id && w.ReferenceOf.IsSome) do
                    let own =
                        alias.Duration.IsSome || alias.MinDuration.IsSome || alias.MaxDuration.IsSome
                        || alias.Conditions.Count > 0 || alias.GetSimulationProperties().IsSome || alias.TokenRole <> TokenRole.None
                    if own then
                        failwithf "DS2TEXT_REF: ds2 5 에는 `ref` 가 없습니다 — 참조 Work %s (%O) 가 제 속성(시간·조건·finished·역할)을 가져 독립 진입 문장으로 옮길 수 없습니다." alias.Name alias.Id
                let works = orderedWorks flow

                // 제품 역할 — Text 에 적는 것은 `token ignore` 하나다. Source·Sink 는 유도한다(소스 = active api 의 command 대상,
                // 끝 = 경로 끝, Q35·Q39). `entry`/`exit` 는 2026-10-20 #8962 로 문법에서 빠졌다 — 저장 비트는 SDF 에만 남는다.
                let ignored = works |> List.filter (fun w -> w.TokenRole.HasFlag TokenRole.Ignore) |> List.map (fun w -> q w.LocalName)
                if not ignored.IsEmpty then emit 6 ("token ignore " + String.concat ", " ignored + ";")

                let inFlow = sysArrows |> List.filter (fun a -> not (atSystem a) && flowIdOf a.SourceId = flow.Id && flowIdOf a.TargetId = flow.Id)
                let workNameRel (id: Guid) =
                    let w = workOfId.[id]
                    match flowOfWork w with
                    | Some f -> workPath (Some flow.Name) f.Name w.LocalName
                    | None -> q w.LocalName
                emitAll 6 (relationLines (inFlow |> List.map (fun a -> a :> DsArrow)) workNameRel canonicalOf rankOf)

                // ── Work 선언 ──
                let pendingEmpty = ResizeArray<string>()
                let flushEmpty () =
                    if pendingEmpty.Count > 0 then
                        emit 6 ("work " + String.concat ", " pendingEmpty + ";")
                        pendingEmpty.Clear()

                for w in works do
                    let head = "work " + q w.LocalName + identity w.Id
                    begin
                        // 좌표(Position)는 ds2 5 의 글에 없다 — 편집기 배치는 글 밖의 일이다(Q18).
                        let body = ResizeArray<string>()
                        w.Duration |> Option.iter (fun d -> body.Add("duration " + Ds2TextLexeme.ms d + ";"))
                        // `limits` 는 #8962 로 문법에서 빠졌다 — Work 시간 감시는 SDF 실행 구성 몫(Q37), Text 에 적지 않는다.
                        w.GetSimulationProperties()
                        |> Option.iter (fun p -> if p.IsFinished then body.Add("finished;"))
                        for clause in skipClauses (expressionText ("work " + w.Name + " · skip when") conditionReference true) (conditionsOf w.Conditions ConditionType.SkipAction) do
                            body.Add("skip when " + clause + ";")

                        let callPreds (id: Guid) =
                            store.ArrowCalls.Values
                            |> Seq.filter (fun a ->
                                a.ParentId = w.Id && a.TargetId = id
                                && (a.ArrowType = ArrowType.Start || a.ArrowType = ArrowType.StartReset))
                            |> Seq.map (fun a -> a.SourceId)
                        let calls = store.Calls.Values |> Seq.filter (fun c -> c.ParentId = w.Id) |> topoOrder callPreds
                        let callArrows =
                            store.ArrowCalls.Values
                            |> Seq.filter (fun a -> a.ParentId = w.Id && callOfId.ContainsKey a.SourceId && callOfId.ContainsKey a.TargetId)
                            |> Seq.map (fun a -> a :> DsArrow)
                            |> List.ofSeq
                        let callName (id: Guid) = callText callOfId.[id]
                        let callRank = Dictionary<Guid, int>()
                        for c in calls do callRank.[c.Id] <- callRank.Count
                        body.AddRange(relationLines callArrows callName id (fun c -> match callRank.TryGetValue c with | true, r -> r | _ -> Int32.MaxValue))

                        for c in calls do
                            // 보존 번들도 이 작성기를 사용하므로 내보내기를 거절하지 않는다.
                            // 원본 SDF의 경계는 그대로 두고, 순수 Text에 없는 정보임을 명시한다.
                            if c.SequenceLabel <> SequenceLabel.Body then
                                body.Add "// DS2TEXT_SEQUENCE_LABEL_PROJECTION: legacy KPI boundary is omitted; preserve the source SDF or its preservation bundle."
                            if c.Interlocked then
                                body.Add "// DS2TEXT_INTERLOCKED_PROJECTION: legacy abnormal-monitoring exclusion is omitted; preserve the source SDF or its preservation bundle."
                            // 선언문이 없다. 부르는 것을 **그대로 적는다**: `로봇.집기;`
                            let chead = callText c + identity c.Id
                            match c.ReferenceOf with
                            | Some _ ->
                                // Call 레퍼런스는 글에서 폐지했다. 조용히 흘리지 않는다 —
                                // 흘리면 「적히지 않은 Call」이 생겨 왕복이 거짓이 된다.
                                failwithf "DS2 Text 로 적을 수 없습니다: Call 레퍼런스 %s (%O). ref 는 Work 전용입니다." c.Name c.Id
                            | _ ->
                                // 복수 대상 Call 도 글에서 폐지했다 — 함께 부르는 것은 **묶음**이다.
                                if c.ApiCalls.Count > 1 then
                                    failwithf "DS2 Text 로 적을 수 없습니다: 대상이 %d 개인 Call %s (%O). 함께 부르는 것은 묶음으로 적습니다."
                                        c.ApiCalls.Count c.Name c.Id
                                let inner = ResizeArray<string>()
                                // 2026-10-20 Q47(#8963) — `completion existing` 은 없다. CallType SkipIfCompleted 는
                                // `skip when completed;`(제품 Skip 절 뒤)로 적는다. `timeout` 은 #8962 로 빠졌다(Q37).
                                let skipIfCompleted =
                                    match c.GetSimulationProperties() with
                                    | Some p -> p.CallType = CallType.SkipIfCompleted
                                    | None -> false
                                let bindingLines (a: ApiCall) =
                                    let acc = ResizeArray<string>()
                                    let inAddr  = a.InTag  |> Option.map (fun t -> t.Address)
                                    let outAddr = a.OutTag |> Option.map (fun t -> t.Address)
                                    // 생략된 Studio binding은 UndefinedValue다. bool true도
                                    // 명시해야 원래 수신/지시 사양을 미정 값으로 바꾸지 않는다.
                                    acc.Add("input " + Ds2TextSpec.formatWithTag "==" inAddr a.InputSpec + ";")
                                    acc.Add("output " + Ds2TextSpec.formatWithTag "=" outAddr a.OutputSpec + ";")
                                    if a.ContactKind <> ContactKind.NoContact then
                                        let name =
                                            match a.ContactKind with
                                            | ContactKind.NcContact    -> "nc"
                                            | ContactKind.RisingPulse  -> "rise"
                                            | ContactKind.FallingPulse -> "fall"
                                            | other -> failwithf "DS2 Text 로 적을 수 없는 접점입니다: %A" other
                                        acc.Add("contact " + name + ";")
                                    List.ofSeq acc

                                if c.ApiCalls.Count = 1 then inner.AddRange(bindingLines c.ApiCalls.[0])

                                for (kind, keyword) in [ ConditionType.AutoAux, "when"; ConditionType.ComAux, "while" ] do
                                    // 2026-10-21 FS1 70(CAT-70-R07): 저장 목록의 여러 루트(AND)는 괄호로 감싼 `and` **한 절**로 — 두 절로 재출력하지 않는다.
                                    let render = expressionText ("call " + c.Name + " · " + keyword) conditionReference false
                                    match conditionsOf c.Conditions kind with
                                    | [] -> ()
                                    | [ single ] -> inner.Add(keyword + " " + render single + ";")
                                    | many -> inner.Add(keyword + " " + (many |> List.map (fun cond -> "(" + render cond + ")") |> String.concat " and ") + ";")
                                for clause in skipClauses (expressionText ("call " + c.Name + " · skip when") conditionReference true) (conditionsOf c.Conditions ConditionType.SkipAction) do
                                    inner.Add("skip when " + clause + ";")
                                if skipIfCompleted then inner.Add "skip when completed;"

                                // ── 왜 늘 블록으로 적는가 ────────────────────────
                                // 관계문이 이름을 이미 적었으니 `로봇.집기;` 한 줄은 군더더기다.
                                // 그런데 **binding 은 관계문이 대신 말해 줄 수 없다** — 생략된 사양은
                                // UndefinedValue 이고, 그것은 기본값(주소 없는 bool true)과 **다른 값**이다.
                                // 그래서 `input`·`output` 이 늘 붙고, 줄은 늘 블록이 된다.
                                // 군더더기를 줄이는 것은 **생성기**의 몫이다(거기선 binding 이 없다).
                                if inner.Count = 0 then body.Add(chead + ";")
                                else
                                    body.Add(chead + " {")
                                    for t in inner do body.Add("  " + t)
                                    body.Add "}"

                        if body.Count = 0 then
                            if options.WriteIdentity then
                                flushEmpty ()
                                emit 6 (head + ";")
                            else pendingEmpty.Add(q w.LocalName)
                        else
                            flushEmpty ()
                            emit 6 (head + " {")
                            emitAll 8 body
                            emit 6 "}"
                    end

                flushEmpty ()
                emit 4 "}"

            // ── api / sensor (ds2 5) ──
            //  · 명령(command)이 없는 ApiDef 는 `sensor` 다 — `command none` 은 없다.
            //  · api 는 command·observe·action·sensing 넷을 **모두** 적는다 — 숨은 기본값이 없다.
            //  · 변수를 가리키는 api/sensor 에는 action·sensing 을 적지 않는다(물리 정책이 없다).
            //  · `action virtual` 은 ds2 5 에 없다 — 명령이 있는데 Virtual 이면 거절한다.
            let writeApi (api: ApiDef) =
                let workTarget (id: Guid option) =
                    id |> Option.bind (fun g -> match workOfId.TryGetValue g with | true, w -> Some w | _ -> None)
                       |> Option.map (fun w -> match flowOfWork w with | Some f -> workPath None f.Name w.LocalName | None -> q w.LocalName)
                let varTarget (id: Guid option) =
                    id |> Option.bind (fun g -> match store.Variables.TryGetValue g with | true, v -> Some ("variable." + q v.Name) | _ -> None)
                let tx = match workTarget api.TxGuid with | Some w -> Some w | None -> varTarget api.TxVariableId
                let rx = match workTarget api.RxGuid with | Some w -> Some w | None -> varTarget api.RxVariableId
                let valueAccess = api.TxVariableId.IsSome || api.RxVariableId.IsSome
                let actionLine () =
                    match api.ActionType with
                    | ActionType.Normal None     -> "action normal;"
                    | ActionType.Normal (Some t) -> "action normal " + Ds2TextLexeme.msInt t + ";"
                    | ActionType.Pulse None      -> "action pulse;"
                    | ActionType.Pulse (Some t)  -> "action pulse " + Ds2TextLexeme.msInt t + ";"
                    | ActionType.Latch           -> "action latch;"
                    | ActionType.Virtual         ->
                        failwithf "DS2TEXT_ACTION_VIRTUAL: ds2 5 에는 `action virtual` 이 없습니다 — api %s (%O). 출력이 없는 장치는 sensor 로 적습니다." api.Name api.Id
                let sensingLine () =
                    match api.SensingType with
                    | SensingType.Normal None     -> "sensing normal;"
                    | SensingType.Normal (Some t) -> "sensing normal " + Ds2TextLexeme.msInt t + ";"
                    | SensingType.Latch t         -> "sensing latch " + Ds2TextLexeme.msInt t + ";"
                    | SensingType.Virtual t       -> "sensing virtual " + Ds2TextLexeme.msInt t + ";"
                // Q43 — 제품은 Work 를 지령하는 api 에만 있다. 다른 자리의 번호를 말없이 버리지 않는다.
                if api.TokenSpecId.IsSome && (tx.IsNone || valueAccess) then
                    failwithf "DS2TEXT_API_PRODUCT: api %s (%O) 는 Work 를 지령하지 않아 `product %d;` 를 적을 수 없습니다(2026-10-20 Q43)." api.Name api.Id api.TokenSpecId.Value
                match tx with
                | None ->
                    emit 4 ("sensor " + q api.Name + identity api.Id + " {")
                    api.Description |> Option.iter (fun value -> emit 6 ("description " + jl value + ";"))
                    match rx with
                    | Some target -> emit 6 ("observe " + target + ";")
                    | None -> failwithf "DS2TEXT_SENSOR: ApiDef %s (%O) 에 command 도 observe 도 없습니다 — ds2 5 로 적을 수 없습니다." api.Name api.Id
                    if not valueAccess then emit 6 (sensingLine ())
                    emit 4 "}"
                | Some command ->
                    emit 4 ("api " + q api.Name + identity api.Id + " {")
                    api.Description |> Option.iter (fun value -> emit 6 ("description " + jl value + ";"))
                    emit 6 ("command " + command + ";")
                    emit 6 ("observe " + (rx |> Option.defaultValue "none") + ";")
                    if not valueAccess then
                        emit 6 (actionLine ())
                        emit 6 (sensingLine ())
                    api.TokenSpecId |> Option.iter (fun id -> emit 6 ("product " + string id + ";"))
                    emit 4 "}"

            let children : DsChild list =
                [ yield! flows |> Seq.map (fun f -> f :> DsChild)
                  yield! store.ApiDefs.Values |> Seq.filter (fun a -> a.ParentId = sys.Id) |> Seq.map (fun a -> a :> DsChild) ]
            for child in declarationOrder children do
                match child with
                | :? Flow as flow -> writeFlow flow
                | :? ApiDef as api -> writeApi api
                | _ -> failwith "Unexpected System child."
            emit 2 "}"

        lines.Add "}"

        let sb = StringBuilder()
        for l in lines do sb.Append(l).Append('\n') |> ignore
        sb.ToString()

    /// Default readable output deliberately omits declaration identities.
    let write (store: DsStore) (projectId: Guid) : string =
        writeWithOptions store projectId Ds2TextOptions.Default

    /// 저장소의 첫 프로젝트를 적는다 (프로젝트가 없으면 빈 문자열).
    let writeStore (store: DsStore) : string =
        match store.Projects.Keys |> Seq.tryHead with
        | Some id -> write store id
        | None -> ""
