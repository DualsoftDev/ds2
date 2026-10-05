namespace Ds2.Text

open System
open System.Collections.Generic
open System.Text
open Ds2.Core
open Ds2.Core.Store

/// Export policy; ordinary human-readable output remains unchanged by default.
type Ds2TextOptions =
    { WriteIdentity: bool }
    static member Default = { WriteIdentity = false }

/// `DsStore` → 정규 DS2 Text v4 원문.
///
/// 참조 구현은 두 곳이며, 이 모듈은 그 둘의 **역방향**이다.
///   · 배치·생략 규칙 : `ds2_text/ds2text/text.py` 의 `format_text`
///   · 저장 대응 규칙 : `ds2_text/native/Program.fs` (IR → DsStore 어댑터)
///
/// native 어댑터가 정한 대응을 그대로 되짚는다 — 여기서 어긋나면 왕복이 깨진다.
///   · Text 의 call        ↔ `Call.Name` ("장비.기능" — 손잡이는 없다)
///   · Call 의 API 대상    ↔ `ApiCall.Name` ("System.Api" 문자열 그대로)
///   · Work 의 initial     ↔ `SimulationWorkProperties.IsFinished`
///   · Call 의 completion  ↔ `SimulationCallProperties.CallType`
///   · Call 의 timeout     ↔ `SimulationCallProperties.Timeout`
///
/// 이 모듈은 그래프를 추론하지 않는다. 저장된 화살표·선언을 순서 그대로 적고,
/// **인접한** 같은 종류의 화살표만 연쇄(`A > B > C`)로 압축한다.
[<RequireQualifiedAccess>]
module Ds2TextWriter =

    let private positionText (p: Xywh) = sprintf "at %d, %d, %d, %d;" p.X p.Y p.W p.H

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

    // ─────────────────────────────────────────────────────────── 관계 압축
    //  text.py `_relation_lines` 의 이식.
    //  · Group은 저장된 edge마다 이름 있는 두 멤버 묶음으로 적는다.
    //    Studio의 named group A,B,C는 chain A–B,B–C로 낮춰지므로 star A–B,A–C를
    //    하나의 목록으로 압축하면 edge가 바뀐다. 원래 묶음 이름은 store에 없다.
    //  · 그 외 연산자는 **source 가 직전 target 과 같은 동안만** 이어 붙인다.
    //  · `<|>` 는 이름 하나씩만 받으므로 연쇄하지 않는다.
    //  어느 쪽도 저장 순서를 바꾸지 않는다 — 압축은 표기일 뿐 의미를 만들지 않는다.

    let private operatorOf (t: ArrowType) =
        match t with
        | ArrowType.Start      -> ">"
        | ArrowType.Reset      -> "|>"
        | ArrowType.StartReset -> "=>"
        | ArrowType.ResetReset -> "<|>"
        | other -> failwithf "DS2 Text 로 적을 수 없는 화살표입니다: %A" other

    /// 묶음 이름은 **저장소에 없다** — 글로 적을 때 지어낸다.
    ///
    /// 이름을 저장하려고 `DsGroup` 엔티티를 두었었는데, 재 보니 얻는 것이 이름뿐이었다:
    /// 런타임은 `buildGroupSets` 가 **Union-Find** 로 묶으므로 사슬(A–B, B–C)과
    /// 별(A–B, A–C)이 같은 집합 `{A,B,C}` 이다 — 모양 차이가 뜻을 바꾸지 않는다.
    /// 이름 하나를 위해 엔티티·사전·이행 경로·검증 60줄을 지고 갈 이유가 없어 들어냈다.
    let private groupNameGenerator (reserved: string seq) =
        let taken = HashSet<string>(reserved, StringComparer.Ordinal)
        let mutable serial = 0
        fun () ->
            let mutable candidate = ""
            let mutable available = false
            while not available do
                serial <- serial + 1
                candidate <- "group" + string serial
                available <- taken.Add candidate
            candidate

    /// 묶음 간선 표시. 이름은 아직 안 붙인 채로 멤버만 실어 둔다.
    let private GROUP_MARK = "\u0000group\u0000"

    /// 흩어진 묶음 간선을 **연결 성분**으로 합쳐 한 줄씩 만든다.
    ///
    /// 저장소에는 `A–B`, `B–C` 처럼 쌍으로만 남는다. 그대로 적으면
    /// `group group1 = A, B;` `group group2 = B, C;` 두 줄이 되어 **셋이 한 묶음**이라는
    /// 사실이 글에서 사라진다. 런타임이 Union-Find 로 보는 것과 같게 합친다 —
    /// 즉 **인과를 해석해 묶음을 되살린다.** 이름은 그때 `group1`·`group2` 로 짓는다.
    let private mergeGroupRuns (lines: string list) (newGroupName: unit -> string) : string list =
        if lines |> List.forall (fun l -> not (l.StartsWith GROUP_MARK)) then lines
        else
            let parent = Dictionary<string, string>()
            let rec find x = match parent.TryGetValue x with
                             | true, p when p <> x -> let r = find p in parent.[x] <- r; r
                             | _ -> x
            let add x = if not (parent.ContainsKey x) then parent.[x] <- x
            let order = ResizeArray<string>()                 // 처음 나온 차례를 지킨다
            let groupLineIndexes = ResizeArray<int>()
            lines |> List.iteri (fun i l ->
                if l.StartsWith GROUP_MARK then
                    groupLineIndexes.Add i
                    let members = l.Substring(GROUP_MARK.Length).Split('\u0001')
                    for m in members do
                        add m
                        if not (order.Contains m) then order.Add m
                    for k in 1 .. members.Length - 1 do
                        parent.[find members.[0]] <- find members.[k])
            // 성분별 멤버를 **처음 나온 차례**로 모은다
            let byRoot = Dictionary<string, ResizeArray<string>>()
            for m in order do
                let r = find m
                match byRoot.TryGetValue r with
                | true, xs -> xs.Add m
                | _ -> byRoot.[r] <- ResizeArray([m])
            let nameOfRoot = Dictionary<string, string>()
            let emitted = HashSet<string>()
            let first = groupLineIndexes.[0]
            let rendered =
                order
                |> Seq.map find
                |> Seq.distinct
                |> Seq.filter (fun r -> byRoot.[r].Count >= 2)
                |> Seq.map (fun r ->
                    let name = newGroupName()
                    nameOfRoot.[r] <- name
                    emitted.Add r |> ignore
                    "group " + q name + " = " + String.concat ", " (byRoot.[r]) + ";")
                |> List.ofSeq
            // 묶음 줄은 **첫 묶음 간선이 있던 자리**에 한꺼번에 놓는다. 나머지는 지운다.
            lines
            |> List.mapi (fun i l -> i, l)
            |> List.collect (fun (i, l) ->
                if i = first then rendered
                elif l.StartsWith GROUP_MARK then []
                else [ l ])

    let private relationLines (arrows: DsArrow list) (nameOf: Guid -> string) (newGroupName: unit -> string) : string list =
        let items = List.toArray arrows
        let result = ResizeArray<string>()
        let mutable i = 0
        while i < items.Length do
            let head = items.[i]
            let kind = head.ArrowType
            let names = ResizeArray<Guid>([ head.SourceId; head.TargetId ])
            i <- i + 1
            if kind = ArrowType.Group then
                // 여기서는 **한 간선만** 적는다. 같은 묶음의 나머지 간선은 아래
                // `mergeGroupRuns` 가 연결 성분으로 합쳐 한 줄로 만든다.
                result.Add(GROUP_MARK + (names |> Seq.map nameOf |> String.concat "\u0001"))
            else
                if kind <> ArrowType.ResetReset then
                    while i < items.Length && items.[i].ArrowType = kind && items.[i].SourceId = names.[names.Count - 1] do
                        names.Add items.[i].TargetId
                        i <- i + 1
                result.Add((names |> Seq.map nameOf |> String.concat (" " + operatorOf kind + " ")) + ";")
        mergeGroupRuns (List.ofSeq result) newGroupName

    // ─────────────────────────────────────────────────────────── 조건식
    //  Condition 트리 → `start when` / `permit when` / `skip when` 의 오른쪽.

    let private contactPrefix (kind: ContactKind) =
        match kind with
        | ContactKind.NoContact    -> ""
        | ContactKind.NcContact    -> "nc "
        | ContactKind.RisingPulse  -> "rise "
        | ContactKind.FallingPulse -> "fall "
        | other -> raise (NotSupportedException(sprintf "DS2TEXT_CONDITION_CONTACT: DS2 Text로 보존할 수 없는 조건 접점입니다: %A" other))

    /// leafReference는 실제 ApiDef 대상과 공유 입력(from)의 일치를 검증한다.
    let rec private expressionText (leafReference: ApiCall -> string * string option) (c: Condition) : string =
        // 런타임은 직접 ApiCall과 자식을 함께 평가하지만 native Text의 정규 트리는
        // 단일 leaf 또는 자식 그룹이다. 누락하거나 상수로 바꾸지 않고 지원 경계를 알린다.
        let unsupported reason =
            raise (NotSupportedException(sprintf "DS2TEXT_CONDITION_SHAPE: 조건 %O을 DS2 Text로 보존할 수 없습니다: %s" c.Id reason))
        if c.ApiCalls.Count > 0 && c.Children.Count > 0 then
            unsupported "직접 ApiCall과 자식 조건이 혼합되어 있습니다."
        if c.ApiCalls.Count > 1 then
            unsupported "하나의 조건 노드에 여러 직접 ApiCall이 있습니다."
        if c.ApiCalls.Count = 0 && c.Children.Count = 0 then
            unsupported "빈 조건은 표현 가능한 비교식이 없으며 true로 대체할 수 없습니다."
        if c.Children.Count > 0 && c.IsInverted && c.ApiCalls.Count = 0 && c.Children.Count = 1 then
            "not " + expressionText leafReference c.Children.[0]
        elif c.Children.Count > 0 then
            let joiner = if c.IsOR then " or " else " and "
            let body =
                c.Children
                |> Seq.map (expressionText leafReference)
                |> String.concat joiner
            let wrapped = "(" + body + ")"
            if c.IsInverted then "not " + wrapped else wrapped
        elif c.ApiCalls.Count = 1 then
            let a = c.ApiCalls.[0]
            let target, owner = leafReference a
            let source = owner |> Option.map (fun path -> " from " + path) |> Option.defaultValue ""
            let leaf =
                match source, Ds2TextSpec.tryMatchLiteral a.InputSpec with
                // 기존 match 조건은 타입 없이 `Api == 값` 으로 남는다. from 이 붙으면 typed 조건이다.
                | "", Some literal -> target + " == " + literal
                | _ -> target + source + " as " + Ds2TextSpec.format "==" a.InputSpec
            let text = contactPrefix a.ContactKind + leaf
            if c.IsInverted then "not " + text else text
        else
            unsupported "지원하지 않는 조건 구조입니다."

    let private conditionsOf (conditions: ResizeArray<Condition>) (kind: ConditionType) =
        conditions |> Seq.filter (fun c -> c.Type = Some kind) |> List.ofSeq

    // ─────────────────────────────────────────────────────────── 본문

    /// `projectId` 프로젝트를 정규 DS2 Text v4 원문으로 적는다.
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

        lines.Add "ds2 4;"
        lines.Add ("project " + q project.Name + identity project.Id + " {")
        if project.Author <> "" then emit 2 ("author " + jl project.Author + ";")
        if project.Version <> "1.0.0" then emit 2 ("version " + jl project.Version + ";")
        if project.DateTime <> DateTimeOffset.UnixEpoch || project.DateTime.Offset <> TimeSpan.Zero then
            emit 2 ("datetime " + jl (project.DateTime.ToString("O", Globalization.CultureInfo.InvariantCulture)) + ";")

        // ── product (TokenSpec) ──
        for spec in project.TokenSpecs do
            let source =
                spec.WorkId
                |> Option.bind (fun id ->
                    match workOfId.TryGetValue id with
                    | true, w -> workParts w |> Option.map (fun (s, f, n) -> Ds2TextLexeme.path [ s; f; n ])
                    | _ -> None)
                |> Option.map (fun p -> " from " + p)
                |> Option.defaultValue ""
            if spec.Fields.IsEmpty then
                emit 2 (sprintf "product %d %s%s {" spec.Id (jl spec.Label) source)
                emit 2 "}"
            else
                emit 2 (sprintf "product %d %s%s {" spec.Id (jl spec.Label) source)
                for KeyValue (name, value) in spec.Fields do
                    emit 4 (q name + " = " + jl value + ";")
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

            let flows = store.Flows.Values |> Seq.filter (fun f -> f.ParentId = sys.Id) |> declarationOrder

            // Work 화살표: 같은 Flow 안이면 그 Flow 에, 다르면 System 레벨에 적는다.
            let sysArrows =
                store.ArrowWorks.Values
                |> Seq.filter (fun a -> a.ParentId = sys.Id)
                |> Seq.filter (fun a -> workOfId.ContainsKey a.SourceId && workOfId.ContainsKey a.TargetId)
                |> List.ofSeq
            let flowIdOf (id: Guid) = workOfId.[id].ParentId
            let crossFlow = sysArrows |> List.filter (fun a -> flowIdOf a.SourceId <> flowIdOf a.TargetId)
            let workNameAbs (id: Guid) =
                let w = workOfId.[id]
                match flowOfWork w with
                | Some f -> workPath None f.Name w.LocalName
                | None -> q w.LocalName
            let systemGroupName = groupNameGenerator (flows |> Seq.map (fun f -> f.Name))
            emitAll 4 (relationLines (crossFlow |> List.map (fun a -> a :> DsArrow)) workNameAbs systemGroupName)

            let writeFlow (flow: Flow) =
                emit 4 ("flow " + q flow.Name + identity flow.Id + " {")
                if flow.IsDisabled then emit 6 "enabled false;"
                if not flow.IsAuto then emit 6 "auto false;"

                // Work 차례는 **인과가 정한다**. 같은 Flow 안의 Start 계열 간선이 선행이다.
                let workPreds (id: Guid) =
                    sysArrows
                    |> Seq.filter (fun a ->
                        a.TargetId = id
                        && (a.ArrowType = ArrowType.Start || a.ArrowType = ArrowType.StartReset)
                        && flowIdOf a.SourceId = flow.Id)
                    |> Seq.map (fun a -> a.SourceId)
                let works = store.Works.Values |> Seq.filter (fun w -> w.ParentId = flow.Id) |> topoOrder workPreds

                // 제품 역할 — entry(Source) · token ignore(Ignore) · exit(Sink) 순서 고정.
                for (role, keyword) in [ TokenRole.Source, "entry"; TokenRole.Ignore, "token ignore"; TokenRole.Sink, "exit" ] do
                    let named = works |> List.filter (fun w -> w.TokenRole.HasFlag role) |> List.map (fun w -> q w.LocalName)
                    if not named.IsEmpty then emit 6 (keyword + " " + String.concat ", " named + ";")

                let inFlow = sysArrows |> List.filter (fun a -> flowIdOf a.SourceId = flow.Id && flowIdOf a.TargetId = flow.Id)
                let workNameRel (id: Guid) =
                    let w = workOfId.[id]
                    match flowOfWork w with
                    | Some f -> workPath (Some flow.Name) f.Name w.LocalName
                    | None -> q w.LocalName
                let flowGroupName = groupNameGenerator (works |> Seq.map (fun w -> w.LocalName))
                emitAll 6 (relationLines (inFlow |> List.map (fun a -> a :> DsArrow)) workNameRel flowGroupName)

                // ── Work 선언 ──
                let pendingEmpty = ResizeArray<string>()
                let flushEmpty () =
                    if pendingEmpty.Count > 0 then
                        emit 6 ("work " + String.concat ", " pendingEmpty + ";")
                        pendingEmpty.Clear()

                for w in works do
                    let head = "work " + q w.LocalName + identity w.Id
                    match w.ReferenceOf with
                    | Some originalId when workOfId.ContainsKey originalId ->
                        flushEmpty ()
                        let ow = workOfId.[originalId]
                        let target =
                            match flowOfWork ow with
                            | Some f -> workPath (Some flow.Name) f.Name ow.LocalName
                            | None -> q ow.LocalName
                        let initial = w.GetSimulationProperties() |> Option.map (fun p -> if p.IsFinished then "initial finish;" else "initial ready;")
                        let ownProperties = ResizeArray<string>()
                        initial |> Option.iter ownProperties.Add
                        w.Position |> Option.iter (positionText >> ownProperties.Add)
                        if ownProperties.Count > 0 then
                            emit 6 (head + " = ref " + target + " {")
                            for p in ownProperties do emit 8 p
                            emit 6 "}"
                        else
                            emit 6 (head + " = ref " + target + ";")
                    | _ ->
                        let body = ResizeArray<string>()
                        w.Position |> Option.iter (positionText >> body.Add)
                        w.Duration |> Option.iter (fun d -> body.Add("duration " + Ds2TextLexeme.ms d + ";"))
                        if w.MinDuration.IsSome || w.MaxDuration.IsSome then
                            let bound (v: TimeSpan option) = match v with Some t -> Ds2TextLexeme.ms t | None -> "none"
                            body.Add("limits " + bound w.MinDuration + ", " + bound w.MaxDuration + ";")
                        w.GetSimulationProperties()
                        |> Option.iter (fun p -> body.Add(if p.IsFinished then "initial finish;" else "initial ready;"))
                        for c in conditionsOf w.Conditions ConditionType.SkipAction do
                            body.Add("skip when " + expressionText conditionReference c + ";")

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
                        let callGroupName = groupNameGenerator (calls |> Seq.map textNameOf)
                        body.AddRange(relationLines callArrows callName callGroupName)

                        for c in calls do
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
                                c.Position |> Option.iter (positionText >> inner.Add)
                                let simProps = c.GetSimulationProperties()
                                match simProps with
                                | Some p ->
                                    if p.CallType = CallType.SkipIfCompleted then inner.Add "completion existing;"
                                    p.Timeout |> Option.iter (fun t -> inner.Add("timeout " + Ds2TextLexeme.ms t + ";"))
                                | None -> ()
                                match c.SequenceLabel with
                                | SequenceLabel.Head -> inner.Add "label head;"
                                | SequenceLabel.Tail -> inner.Add "label tail;"
                                | _ -> ()
                                if c.Interlocked then inner.Add "interlocked true;"

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

                                for (kind, keyword) in [ ConditionType.AutoAux, "start"; ConditionType.ComAux, "permit"; ConditionType.SkipAction, "skip" ] do
                                    for cond in conditionsOf c.Conditions kind do
                                        inner.Add(keyword + " when " + expressionText conditionReference cond + ";")

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

                flushEmpty ()
                emit 4 "}"

            // ── api ──
            let writeApi (api: ApiDef) =
                emit 4 ("api " + q api.Name + identity api.Id + " {")
                api.Description |> Option.iter (fun value -> emit 6 ("description " + jl value + ";"))
                let target (id: Guid option) =
                    match id |> Option.bind (fun g -> match workOfId.TryGetValue g with | true, w -> Some w | _ -> None) with
                    | Some w ->
                        match flowOfWork w with
                        | Some f -> workPath None f.Name w.LocalName
                        | None -> q w.LocalName
                    | None -> "none"
                emit 6 ("command " + target api.TxGuid + ";")
                emit 6 ("observe " + target api.RxGuid + ";")
                // Text 기본값은 action=virtual, sensing=normal(시간 없음) — 그 값이면 생략한다.
                match api.ActionType with
                | ActionType.Virtual -> ()
                | ActionType.Normal None   -> emit 6 "action normal;"
                | ActionType.Normal (Some t) -> emit 6 ("action normal " + Ds2TextLexeme.msInt t + ";")
                | ActionType.Pulse None    -> emit 6 "action pulse;"
                | ActionType.Pulse (Some t)  -> emit 6 ("action pulse " + Ds2TextLexeme.msInt t + ";")
                | ActionType.Latch         -> emit 6 "action latch;"
                match api.SensingType with
                | SensingType.Normal None -> ()
                | SensingType.Normal (Some t) -> emit 6 ("sensing normal " + Ds2TextLexeme.msInt t + ";")
                | SensingType.Latch t   -> emit 6 ("sensing latch " + Ds2TextLexeme.msInt t + ";")
                | SensingType.Virtual t -> emit 6 ("sensing virtual " + Ds2TextLexeme.msInt t + ";")
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
