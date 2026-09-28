namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core

/// ds2-csv-for-ai/v1 파서 + fail-fast 검증기.
///
/// 오류는 전량 집계 후 한 번에 Error 로 반환한다(부분 import 금지) — BasicCsvParser 와 같은 규약.
/// 오류 코드: AI001~AI0xx. 계약 문서: docs/CSV_FOR_AI_SCHEMA.md
///
/// 전처리는 반드시 CsvParser 의 것을 그대로 쓴다. 판별(CsvFormatDetector)과 파싱이 서로 다른
/// 전처리를 쓰면 «배지는 초록인데 불러오기는 실패» 하는 상태가 생긴다.
module AiCsvParser =

    let internal expectedHeaderFields =
        [ "kind"; "name"; "type"; "detail"; "time"; "intag"; "outtag" ]

    /// 이름에 쓸 수 없는 문자 — 전부 문법 기호다. `.` 는 경로 구분자라 특히 중요하다.
    let private forbiddenNameChars = set [ '.'; '>'; ';'; '='; ','; '"'; '&'; '|'; '!'; '('; ')'; '/'; '+'; '@'; '#' ]

    let private trim (s: string) = if isNull s then "" else s.Trim()

    let private checkName (what: string) (name: string) =
        if name = "" then Error $"AI002: {what} 이름이 비어 있습니다."
        elif name |> Seq.exists forbiddenNameChars.Contains then
            Error $"AI002: {what} 이름 '{name}' 에 금지 문자(. > ; = , \" & | ! ( ) / + @ #)가 있습니다."
        else Ok name

    // ---------- 시간 ----------

    /// `100MS` / `1.5S` / `?` / 공란. 단위가 없으면 오류 — 숫자만 적고 단위를 잊는 실수가 잦다.
    let internal parseTime (raw: string) : Result<TimeSpan option, string> =
        let t = (trim raw).ToUpperInvariant()
        if t = "" || t = "?" then Ok None
        else
            let num, unit =
                if t.EndsWith "MS" then t.Substring(0, t.Length - 2), "MS"
                elif t.EndsWith "S" then t.Substring(0, t.Length - 1), "S"
                else t, ""
            if unit = "" then Error $"AI020: 시간 '{raw}' 에 단위가 없습니다(MS 또는 S)."
            else
                match Double.TryParse(num, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                | false, _ -> Error $"AI020: 시간 '{raw}' 를 숫자로 읽을 수 없습니다."
                | true, v when v < 0.0 -> Error $"AI020: 시간 '{raw}' 가 음수입니다."
                | true, v ->
                    let ms = if unit = "MS" then v else v * 1000.0
                    if ms > 86_400_000.0 then Error $"AI020: 시간 '{raw}' 가 24시간을 넘습니다."
                    else Ok (Some (TimeSpan.FromMilliseconds ms))

    // ---------- 경로 ----------

    /// `A.B` → 2마디, `A.B.C.D` → 4마디. 마디 수가 곧 Work/Call 판정이라 별도 열이 없다.
    let private splitPath (path: string) = (trim path).Split('.') |> Array.map trim |> List.ofArray

    // ---------- ActionType / SensingType ----------

    /// `Normal` · `Normal(100)` · `Pulse` · `Latch` · `Virtual`
    let private parseAction (raw: string) : Result<ActionType, string> =
        let t = trim raw
        let name, arg =
            match t.IndexOf '(' with
            | -1 -> t, None
            | i ->
                if not (t.EndsWith ")") then t, None
                else t.Substring(0, i), Some (t.Substring(i + 1, t.Length - i - 2))
        let argMs () =
            match arg with
            | None -> Ok None
            | Some a ->
                match Int32.TryParse(trim a) with
                | true, v when v >= 0 -> Ok (Some v)
                | _ -> Error $"AI030: Action 시간 '{a}' 를 읽을 수 없습니다(정수 ms)."
        match name.ToUpperInvariant() with
        | "NORMAL" -> argMs () |> Result.map ActionType.Normal
        | "PULSE" -> argMs () |> Result.map ActionType.Pulse
        | "LATCH" -> if arg.IsSome then Error "AI030: Latch 는 시간 인자를 받지 않습니다." else Ok ActionType.Latch
        | "VIRTUAL" -> if arg.IsSome then Error "AI030: Action 의 Virtual 은 시간 인자를 받지 않습니다." else Ok ActionType.Virtual
        | "" -> Error "AI030: Action 이 비어 있습니다."
        | other -> Error $"AI030: 알 수 없는 Action '{other}' (Normal|Pulse|Latch|Virtual)."

    /// `Normal` · `Normal(100)` · `Latch(50)` · `Virtual(200)` — Latch/Virtual 은 시간 필수.
    let private parseSensing (raw: string) : Result<SensingType, string> =
        let t = trim raw
        let name, arg =
            match t.IndexOf '(' with
            | -1 -> t, None
            | i ->
                if not (t.EndsWith ")") then t, None
                else t.Substring(0, i), Some (t.Substring(i + 1, t.Length - i - 2))
        let reqMs () =
            match arg with
            | None -> Error "AI031: Latch/Virtual 감지는 시간이 필수입니다 — 예: Latch(50)."
            | Some a ->
                match Int32.TryParse(trim a) with
                | true, v when v >= 0 -> Ok v
                | _ -> Error $"AI031: Sensing 시간 '{a}' 를 읽을 수 없습니다(정수 ms)."
        match name.ToUpperInvariant() with
        | "NORMAL" ->
            match arg with
            | None -> Ok (SensingType.Normal None)
            | Some a ->
                match Int32.TryParse(trim a) with
                | true, v when v >= 0 -> Ok (SensingType.Normal (Some v))
                | _ -> Error $"AI031: Sensing 시간 '{a}' 를 읽을 수 없습니다(정수 ms)."
        | "LATCH" -> reqMs () |> Result.map SensingType.Latch
        | "VIRTUAL" -> reqMs () |> Result.map SensingType.Virtual
        | "" -> Error "AI031: Sensing 이 비어 있습니다."
        | other -> Error $"AI031: 알 수 없는 Sensing '{other}' (Normal|Latch|Virtual)."

    // ---------- IO 태그 ----------

    /// `[심벌@]주소[:DataType][=기대값]`
    let private parseTag (raw: string) : Result<AiTagSpec option, string> =
        let t = trim raw
        if t = "" || t = "?" then Ok None
        else
            let symbol, rest =
                match t.IndexOf '@' with
                | -1 -> None, t
                | i -> Some (trim (t.Substring(0, i))), trim (t.Substring(i + 1))
            let body, expected =
                match rest.IndexOf '=' with
                | -1 -> rest, None
                | i -> trim (rest.Substring(0, i)), Some (trim (rest.Substring(i + 1)))
            let addr, dataType =
                match body.IndexOf ':' with
                | -1 -> body, None
                | i -> trim (body.Substring(0, i)), Some ((trim (body.Substring(i + 1))).ToUpperInvariant())
            if addr = "" then Error $"AI040: 태그 '{raw}' 에 주소가 없습니다."
            else Ok (Some { Symbol = symbol; Address = addr; DataType = dataType; Expected = expected })

    // ---------- 조건식 ----------

    /// 토큰: `(` `)` `&` `|` `!` 그리고 leaf 텍스트.
    let private tokenizeCond (s: string) =
        let tokens = ResizeArray<string>()
        let buf = Text.StringBuilder()
        let flush () =
            let v = trim (buf.ToString())
            if v <> "" then tokens.Add v
            buf.Clear() |> ignore
        // `A(R)` · `A(F)` 의 괄호는 **leaf 의 일부**다. 묶음 괄호와 같게 다루면
        // `기동버튼.PB(R)` 이 네 토큰으로 쪼개져 조건식이 깨진다.
        let isEdgeSuffix (i: int) =
            i + 2 < s.Length
            && (s.[i + 1] = 'R' || s.[i + 1] = 'r' || s.[i + 1] = 'F' || s.[i + 1] = 'f')
            && s.[i + 2] = ')'
        let mutable inQuote = false
        let mutable i = 0
        while i < s.Length do
            let ch = s.[i]
            if inQuote then
                buf.Append ch |> ignore
                if ch = '"' then inQuote <- false
            else
                match ch with
                | '"' -> inQuote <- true; buf.Append ch |> ignore
                // 앞에 읽던 leaf 가 있고 뒤가 R)/F) 면 접미사다 — 통째로 붙인다.
                | '(' when buf.Length > 0 && isEdgeSuffix i ->
                    buf.Append(s.Substring(i, 3)) |> ignore
                    i <- i + 2
                | '(' | ')' | '&' | '|' | '!' -> flush (); tokens.Add (string ch)
                | _ -> buf.Append ch |> ignore
            i <- i + 1
        flush ()
        List.ofSeq tokens

    /// leaf 텍스트 → AiCondExpr. `/A`(Nc접점) · `A(R)`/`A(F)` 접미 · `A=기대값`.
    let private parseLeaf (raw: string) : Result<AiCondExpr, string> =
        let mutable t = trim raw
        let contactNc = t.StartsWith "/"
        if contactNc then t <- trim (t.Substring 1)
        let mutable contact = if contactNc then ContactKind.NcContact else ContactKind.NoContact
        if t.EndsWith "(R)" then
            contact <- ContactKind.RisingPulse
            t <- trim (t.Substring(0, t.Length - 3))
        elif t.EndsWith "(F)" then
            contact <- ContactKind.FallingPulse
            t <- trim (t.Substring(0, t.Length - 3))
        let body, spec =
            match t.IndexOf '=' with
            | -1 -> t, None
            | i -> trim (t.Substring(0, i)), Some (trim (t.Substring(i + 1)))
        if body = "" then Error "AI050: 조건 leaf 가 비어 있습니다."
        elif body.StartsWith "_" then Ok (AiRawLeaf(body, contact))
        else
            match splitPath body with
            | [ dev; api ] when dev <> "" && api <> "" -> Ok (AiLeaf(dev, api, contact, spec))
            | _ -> Error $"AI050: 조건 leaf '{raw}' 는 '디바이스.액션' 이어야 합니다."

    /// 재귀 하강 파서. 같은 괄호 안에서 `&` 와 `|` 를 섞으면 거부한다 —
    /// 우선순위를 몰래 적용하면 사용자가 쓴 식과 저장된 트리가 달라진다.
    /// 재귀 하강 파서. 같은 괄호 안에서 `&` 와 `|` 를 섞으면 거부한다 —
    /// 우선순위를 몰래 적용하면 사용자가 쓴 식과 저장된 트리가 달라진다.
    ///
    /// 피연산자(parsePrimary)와 묶음(parseGroup)을 나눈다. 한 함수로 합치면 피연산자를 읽는
    /// 재귀가 뒤따르는 연산자까지 먹어 `A & B | C` 의 혼용을 잡지 못한다.
    let internal parseCondExpr (text: string) : Result<AiCondExpr, string> =
        let tokens = tokenizeCond text
        if tokens.IsEmpty then Error "AI050: 조건식이 비어 있습니다." else

        let mutable rest = tokens
        let peek () = match rest with [] -> None | h :: _ -> Some h
        let pop () = match rest with [] -> () | _ :: t -> rest <- t

        let rec parsePrimary () : Result<AiCondExpr, string> =
            let negated = (peek () = Some "!")
            if negated then pop ()
            let inner =
                match peek () with
                | Some "(" ->
                    pop ()
                    match parseGroup () with
                    | Error e -> Error e
                    | Ok g ->
                        if peek () = Some ")" then
                            pop ()
                            Ok g
                        else Error "AI051: 괄호가 닫히지 않았습니다."
                | Some tok when tok <> ")" && tok <> "&" && tok <> "|" ->
                    pop ()
                    parseLeaf tok
                | _ -> Error "AI050: 조건식 구문이 잘못되었습니다."
            match inner with
            | Error e -> Error e
            | Ok expr -> if negated then Ok (AiGroup(false, true, [ expr ])) else Ok expr

        and parseGroup () : Result<AiCondExpr, string> =
            match parsePrimary () with
            | Error e -> Error e
            | Ok head ->
                let items = ResizeArray<AiCondExpr>()
                items.Add head
                let mutable op : string option = None
                let mutable failure : string option = None
                let mutable go = true
                while go && failure.IsNone do
                    match peek () with
                    | Some "&" | Some "|" ->
                        let o = (peek ()).Value
                        if op.IsSome && op.Value <> o then
                            failure <- Some "AI052: 같은 괄호 안에서 '&' 와 '|' 를 섞을 수 없습니다. 괄호로 묶어 주세요."
                        else
                            op <- Some o
                            pop ()
                            match parsePrimary () with
                            | Error e -> failure <- Some e
                            | Ok next -> items.Add next
                    | _ -> go <- false
                match failure with
                | Some e -> Error e
                | None ->
                    if items.Count = 1 then Ok items.[0]
                    else Ok (AiGroup((op = Some "|"), false, List.ofSeq items))

        match parseGroup () with
        | Error e -> Error e
        | Ok expr -> if rest.IsEmpty then Ok expr else Error "AI051: 조건식에 남는 토큰이 있습니다."

    // ---------- Call DAG (WORK.Detail) ----------

    /// `Dev.Api>Dev.Api;Dev.Api` — `;` 는 서로 독립인 경로, `>` 는 그 다음.
    let private parseCallDag (raw: string) =
        let nodeOrder = ResizeArray<string>()
        let nodeInfo = Dictionary<string, string * string>()
        let edgeOrder = ResizeArray<string * string>()
        let edgeSeen = HashSet<string * string>()
        let mutable failure : string option = None
        let addNode (token: string) =
            match splitPath token with
            | [ dev; api ] when dev <> "" && api <> "" ->
                let key = $"{dev}.{api}"
                if not (nodeInfo.ContainsKey key) then
                    nodeInfo.[key] <- (dev, api)
                    nodeOrder.Add key
                Some key
            | _ ->
                failure <- Some $"AI010: Call '{token}' 은 '디바이스.액션' 이어야 합니다."
                None
        for path in (trim raw).Split(';') do
            let path = trim path
            if path <> "" && failure.IsNone then
                let mutable prev : string option = None
                for token in path.Split('>') do
                    let token = trim token
                    if token <> "" && failure.IsNone then
                        match addNode token with
                        | None -> ()
                        | Some key ->
                            match prev with
                            | Some p when p <> key && edgeSeen.Add((p, key)) -> edgeOrder.Add((p, key))
                            | _ -> ()
                            prev <- Some key
        match failure with
        | Some e -> Error e
        | None ->
            let nodes = [ for key in nodeOrder -> let dev, api = nodeInfo.[key] in key, dev, api ]
            Ok (nodes, List.ofSeq edgeOrder)

    // ---------- 문서 파싱 ----------

    let parse (content: string) : Result<AiCsvDocument, ParseError list> =
        let normalized = CsvParser.normalize content
        let lines =
            normalized.Replace("\r\n", "\n").Split('\n')
            |> Array.mapi (fun i line -> i + 1, line)
            |> Array.filter (fun (_, line) -> line.Trim() <> "")

        if lines.Length = 0 then
            Error [ { LineNumber = 0; Message = "AI001: 내용이 비어 있습니다." } ]
        else

        let errors = ResizeArray<ParseError>()
        let warnings = ResizeArray<string>()
        let err line (msg: string) = errors.Add { LineNumber = line; Message = msg }

        let headerLine, headerText = lines.[0]
        let separator = CsvParser.detectSeparator headerText
        let headerFields =
            headerText.Split(separator) |> Array.map CsvParser.normalizeHeaderField |> List.ofArray
        if headerFields <> expectedHeaderFields then
            err headerLine "AI001: 헤더가 'Kind,Name,Type,Detail,Time,InTag,OutTag' 가 아닙니다(쉼표 또는 탭 구분)."

        let systems = ResizeArray<AiSystemRow>()
        let flows = ResizeArray<AiFlowRow>()
        let works = ResizeArray<AiWorkRow>()
        let arrows = ResizeArray<AiArrowRow>()
        let apis = ResizeArray<AiApiRow>()
        let conds = ResizeArray<AiCondRow>()

        if errors.Count = 0 then
            for lineNumber, line in Array.skip 1 lines do
                // `#` 주석 행은 열 개수 검사에서 면제한다. Excel 이 채운 빈 칸도, 쉼표가 든 메모도 통과.
                if (trim line).StartsWith "#" then () else

                let cells = line.Split(separator) |> Array.map trim
                let cell i = if i < cells.Length then cells.[i] else ""
                let kindRaw = (cell 0).ToUpperInvariant()
                let name = cell 1
                let typ = cell 2
                let detail = cell 3
                let timeRaw = cell 4
                let inTagRaw = cell 5
                let outTagRaw = cell 6

                let bind r f = match r with Ok v -> f v | Error e -> err lineNumber e

                match kindRaw with
                | "" -> ()
                | "SYS" ->
                    bind (checkName "System" name) (fun n ->
                        let isActive = typ.Equals("Active", StringComparison.OrdinalIgnoreCase)
                        if not isActive && not (typ.Equals("Passive", StringComparison.OrdinalIgnoreCase)) then
                            err lineNumber $"AI003: SYS 의 Type 은 Active 또는 Passive 여야 합니다(현재 '{typ}')."
                        else
                            systems.Add { Name = n; IsActive = isActive
                                          SystemType = (if detail = "" then None else Some detail)
                                          LineNumber = lineNumber })
                | "FLOW" ->
                    bind (checkName "Flow" name) (fun n ->
                        flows.Add { Name = n; LineNumber = lineNumber })
                | "WORK" ->
                    match splitPath name with
                    | [ f; w ] when f <> "" && w <> "" ->
                        let roleTokens =
                            typ.Split('+') |> Array.map trim |> Array.filter (fun s -> s <> "")
                        let mutable roles = TokenRole.None
                        let mutable initFinish = false
                        for tok in roleTokens do
                            match tok.ToUpperInvariant() with
                            | "SOURCE" -> roles <- roles ||| TokenRole.Source
                            | "SINK" -> roles <- roles ||| TokenRole.Sink
                            | "IGNORE" -> roles <- roles ||| TokenRole.Ignore
                            | "FINISH" -> initFinish <- true
                            | other -> err lineNumber $"AI004: 알 수 없는 WORK Type '{other}' (Source|Sink|Ignore|Finish)."
                        let dag = if detail = "" then Ok ([], []) else parseCallDag detail
                        bind dag (fun (nodes, edges) ->
                            bind (parseTime timeRaw) (fun dur ->
                                // 계약: 동작 시간은 Call 없는 Work 에만. 구조로 막는다.
                                if dur.IsSome && not nodes.IsEmpty then
                                    err lineNumber "AI021: Call 을 가진 WORK 에는 Time 을 적을 수 없습니다(동작 시간은 API 행에)."
                                else
                                    works.Add { FlowName = f; WorkName = w; Roles = roles; InitFinish = initFinish
                                                Nodes = nodes; Edges = edges; Duration = dur; LineNumber = lineNumber }))
                    | _ -> err lineNumber $"AI005: WORK 의 Name 은 'Flow.Work' 여야 합니다(현재 '{name}')."
                | "ARROW" ->
                    let srcParts = splitPath name
                    let isCall = (List.length srcParts = 4)
                    if List.length srcParts <> 2 && not isCall then
                        err lineNumber $"AI006: ARROW 의 출발점은 'Flow.Work' 또는 'Flow.Work.Device.Api' 여야 합니다(현재 '{name}')."
                    else
                        let at =
                            match typ.ToUpperInvariant() with
                            | "START" -> Ok ArrowType.Start
                            | "RESET" -> Ok ArrowType.Reset
                            | "STARTRESET" -> Ok ArrowType.StartReset
                            | "RESETRESET" -> Ok ArrowType.ResetReset
                            | "GROUP" -> Ok ArrowType.Group
                            | other -> Error $"AI007: 알 수 없는 ARROW Type '{other}'."
                        bind at (fun arrowType ->
                            // Call 레벨은 Start/Group 만 — 에디터가 금지한 상태를 CSV 가 합법화하면 안 된다.
                            if isCall && arrowType <> ArrowType.Start && arrowType <> ArrowType.Group then
                                err lineNumber "AI008: Call 레벨 ARROW 는 Start 또는 Group 만 쓸 수 있습니다."
                            else
                                let targets =
                                    detail.Split(';') |> Array.map trim |> Array.filter (fun s -> s <> "") |> List.ofArray
                                if targets.IsEmpty then
                                    err lineNumber "AI009: ARROW 의 Detail(도착점)이 비어 있습니다."
                                elif targets |> List.exists (fun t -> List.length (splitPath t) <> List.length srcParts) then
                                    err lineNumber "AI009: ARROW 의 도착점은 출발점과 같은 마디 수여야 합니다."
                                else
                                    arrows.Add { IsCallLevel = isCall; Source = name; ArrowType = arrowType
                                                 Targets = targets; LineNumber = lineNumber })
                | "API" ->
                    match splitPath name with
                    | [ dev; api ] when dev <> "" && api <> "" ->
                        // Type 은 `Action/Sensing`. 둘은 서로 다른 값이라 한 칸으로 뭉개면 안 된다.
                        let actionRaw, sensingRaw =
                            match typ.IndexOf '/' with
                            | -1 -> typ, ""
                            | i -> typ.Substring(0, i), typ.Substring(i + 1)
                        if sensingRaw = "" then
                            err lineNumber $"AI032: API 의 Type 은 'Action/Sensing' 이어야 합니다(현재 '{typ}')."
                        else
                            // Detail 은 v1 예약. 센서 선언만 허용한다.
                            let isSensor = detail.Equals("sensor", StringComparison.OrdinalIgnoreCase)
                            if detail <> "" && not isSensor then
                                err lineNumber "AI033: API 의 Detail 은 공란이거나 'sensor' 여야 합니다(v1 예약)."
                            else
                                bind (parseAction actionRaw) (fun action ->
                                    bind (parseSensing sensingRaw) (fun sensing ->
                                        bind (parseTime timeRaw) (fun dur ->
                                            bind (parseTag inTagRaw) (fun inTag ->
                                                bind (parseTag outTagRaw) (fun outTag ->
                                                    let unknownTag (raw: string) = raw.Trim() = "?"
                                                    apis.Add { Device = dev; Api = api; Action = action; Sensing = sensing
                                                               Duration = dur; IsSensor = isSensor
                                                               TagsUnknown = unknownTag inTagRaw || unknownTag outTagRaw
                                                               InTag = inTag; OutTag = outTag
                                                               LineNumber = lineNumber })))))
                    | _ -> err lineNumber $"AI011: API 의 Name 은 '디바이스.액션' 이어야 합니다(현재 '{name}')."
                | "COND" ->
                    let parts = splitPath name
                    let isCall = (List.length parts = 4)
                    if List.length parts <> 2 && not isCall then
                        err lineNumber $"AI012: COND 의 Name 은 'Flow.Work' 또는 'Flow.Work.Device.Api' 여야 합니다(현재 '{name}')."
                    else
                        let ct =
                            match typ.ToUpperInvariant() with
                            | "AUTOAUX" -> Ok ConditionType.AutoAux
                            | "COMAUX" -> Ok ConditionType.ComAux
                            | "SKIPACTION" -> Ok ConditionType.SkipAction
                            | other -> Error $"AI013: 알 수 없는 COND Type '{other}' (AutoAux|ComAux|SkipAction)."
                        bind ct (fun condType ->
                            // Work 조건은 SkipAction 만 의미가 있다. 나머지는 저장돼도 런타임이 무시한다.
                            if not isCall && condType <> ConditionType.SkipAction then
                                err lineNumber "AI014: Work 조건은 SkipAction 만 쓸 수 있습니다(AutoAux/ComAux 는 Call 조건)."
                            elif detail = "" then
                                err lineNumber "AI015: COND 의 Detail(조건식)이 비어 있습니다."
                            else
                                bind (parseCondExpr detail) (fun expr ->
                                    conds.Add { IsCallLevel = isCall; OwnerPath = name; CondType = condType
                                                Expr = expr; LineNumber = lineNumber }))
                | other -> err lineNumber $"AI002: 알 수 없는 Kind '{other}' (SYS|FLOW|WORK|ARROW|API|COND)."

        // ---------- 문서 수준 검증 ----------
        if errors.Count = 0 then
            let activeSystems = systems |> Seq.filter (fun s -> s.IsActive) |> Seq.toList
            match activeSystems with
            | [] -> err 0 "AI060: Active System 행(SYS,<이름>,Active)이 없습니다."
            | [ _ ] -> ()
            | _ -> err 0 $"AI060: Active System 은 1개여야 합니다(현재 {activeSystems.Length}개). 여러 Active System 은 v1 범위 밖입니다."

            if flows.Count = 0 then err 0 "AI061: FLOW 행이 없습니다. Flow 개수가 곧 Capa 입니다."
            if works.Count = 0 then err 0 "AI062: WORK 행이 없습니다."

            let flowNames = flows |> Seq.map (fun f -> f.Name) |> HashSet
            for w in works do
                if not (flowNames.Contains w.FlowName) then
                    err w.LineNumber $"AI063: WORK '{w.FlowName}.{w.WorkName}' 의 Flow '{w.FlowName}' 에 해당하는 FLOW 행이 없습니다."

            let workKeys = works |> Seq.map (fun w -> $"{w.FlowName}.{w.WorkName}") |> HashSet
            let checkWorkRef line (what: string) (path: string) =
                let parts = splitPath path
                let key = if List.length parts >= 2 then String.Join(".", parts |> List.truncate 2) else path
                if not (workKeys.Contains key) then
                    err line $"AI064: {what} 이 가리키는 Work '{key}' 에 해당하는 WORK 행이 없습니다."
            for a in arrows do
                checkWorkRef a.LineNumber "ARROW 출발점" a.Source
                for t in a.Targets do checkWorkRef a.LineNumber "ARROW 도착점" t
            for c in conds do
                checkWorkRef c.LineNumber "COND 소유자" c.OwnerPath

            // API 정의 유무 — Call 이 가리키는 API 가 없으면 디바이스 캐스케이드가 만들 수 없다.
            let apiKeys = apis |> Seq.map (fun a -> $"{a.Device}.{a.Api}") |> HashSet
            for w in works do
                for (key, _, _) in w.Nodes do
                    if not (apiKeys.Contains key) then
                        err w.LineNumber $"AI065: Call '{key}' 에 해당하는 API 행이 없습니다."

            // 역방향 — API 행을 적었는데 어느 Call 도 부르지 않으면 ApiDef 가 만들어지지 않는다.
            // 디바이스 캐스케이드는 «Call 이 있는 API» 만 만들기 때문이다. 오류로 막지는 않는다
            // (의도적으로 미리 적어 둘 수 있다) — 다만 조용히 사라지면 IO 배선을 적어 놓고도
            // 모델에 없는 상태가 되므로 반드시 알린다.
            let usedApiKeys =
                works |> Seq.collect (fun w -> w.Nodes |> Seq.map (fun (key, _, _) -> key)) |> HashSet
            for a in apis do
                let key = $"{a.Device}.{a.Api}"
                if not (usedApiKeys.Contains key) then
                    warnings.Add $"AI-W9: API '{key}' 를 어느 WORK 도 호출하지 않습니다 — ApiDef 가 만들어지지 않습니다."
                // v1 에서 `sensor` 는 읽히기만 하고 모델을 바꾸지 않는다. 단일 API 디바이스의
                // DONE 더미는 센서에도 필요하다 — 반복 감지하려면 재무장할 상대가 있어야 하고,
                // ApiDef 의 Tx/Rx 를 갈라 «감지 송신 ~ 완료 수신» 을 만든다.
                // 적어 두면 효과가 있다고 믿게 되므로 사실대로 알린다.
                if a.IsSensor then
                    warnings.Add $"AI-W10: '{key}' 의 sensor 표기는 v1 에서 모델을 바꾸지 않습니다(문서용)."

            // 중복
            let dupWork = works |> Seq.countBy (fun w -> $"{w.FlowName}.{w.WorkName}") |> Seq.filter (snd >> (<) 1)
            for (k, n) in dupWork do err 0 $"AI066: WORK '{k}' 가 {n}번 중복 정의되었습니다."
            let dupApi = apis |> Seq.countBy (fun a -> $"{a.Device}.{a.Api}") |> Seq.filter (snd >> (<) 1)
            for (k, n) in dupApi do err 0 $"AI067: API '{k}' 가 {n}번 중복 정의되었습니다."

            // 경고 — 불러오기는 되지만 알아야 하는 것.
            if not (works |> Seq.exists (fun w -> w.Roles.HasFlag TokenRole.Source)) then
                warnings.Add "AI-W1: Source Work 가 없습니다. 자동 시작되지 않고 그래프 검증이 «Source 후보» 로 경고합니다."
            let unspecified = apis |> Seq.filter (fun a -> a.Duration.IsNone) |> Seq.length
            if unspecified > 0 then
                warnings.Add $"AI-W2: Time 미기입 {unspecified}건 — 기본값 500ms 가 적용됩니다."
            for a in apis do
                // `?` 로 «모른다» 고 적은 것은 빠뜨린 것이 아니다 — 아래에서 따로 센다.
                if a.Action <> ActionType.Virtual && a.OutTag.IsNone && not a.IsSensor && not a.TagsUnknown then
                    warnings.Add $"AI-W3: '{a.Device}.{a.Api}' 는 Action 이 Virtual 이 아닌데 OutTag 가 없습니다."
            let unknownTags = apis |> Seq.filter (fun a -> a.TagsUnknown) |> Seq.length
            if unknownTags > 0 then
                warnings.Add $"AI-W11: IO 미상 {unknownTags}건 — `?` 로 표시했습니다. 배선 전에 채워야 합니다."

            // AI-W12 — 같은 디바이스를 Flow 마다 다르게 다루는 비대칭.
            //
            // 차종 A 는 로봇을 원위치시키고 차종 B 는 그냥 두는 모델이 실제로 만들어졌다.
            // 경고도 오류도 나지 않지만 «A 는 돌아오고 B 는 안 돌아오는» 설비가 된다.
            // 기준: 어떤 API 를 여러 Flow 가 부르는데, 같은 디바이스의 다른 API 는
            // 그중 일부만 부르면 비대칭이다. (Flow 하나만 쓰는 API 는 전용 동작이므로 뺀다.)
            // AI-W14 — 출력 자리에 입력 영역 주소를 적었다.
            //
            // `%I…` 는 PLC 가 «읽는» 비트다. 거기에 코일을 걸 수 없다.
            // 영역 문자(I/Q/M)가 주소의 뜻을 정한다 — 숫자만 맞다고 되는 것이 아니다.
            for a in apis do
                match a.OutTag with
                | Some t when t.Address.TrimStart().StartsWith("%I") ->
                    warnings.Add $"AI-W14: '{a.Device}.{a.Api}' 의 OutTag 가 입력 영역입니다({t.Address}) — 출력은 %%Q 여야 합니다."
                | _ -> ()

            // AI-W13 — 같은 IO 주소를 두 API 가 쓴다.
            //
            // 입력 비트 하나가 여러 센서일 수는 없고, 출력 하나가 여러 장치를 따로 움직일 수도 없다.
            // 실제로 지그 API 16개가 주소 2개를 공유하는 모델이 만들어졌다 — 복사해 붙이면 이렇게 된다.
            // 문법도 연결도 멀쩡해서 그래프 검증까지 조용히 통과한다.
            let addressUsers =
                apis
                |> Seq.collect (fun a ->
                    [ a.InTag; a.OutTag ]
                    |> List.choose id
                    |> List.map (fun t -> t.Address.Trim(), $"{a.Device}.{a.Api}"))
                |> Seq.filter (fun (addr, _) -> addr <> "")
                |> Seq.groupBy fst
                |> Seq.map (fun (addr, xs) -> addr, xs |> Seq.map snd |> Seq.distinct |> Seq.toList)
                |> Seq.filter (fun (_, users) -> List.length users > 1)
            for addr, users in addressUsers do
                let names = String.concat ", " users
                warnings.Add $"AI-W13: 주소 '{addr}' 를 {List.length users}개 API 가 함께 씁니다 — {names}"

            let flowsByNode =
                works
                |> Seq.collect (fun w -> w.Nodes |> Seq.map (fun (key, _, _) -> key, w.FlowName))
                |> Seq.groupBy fst
                |> Seq.map (fun (key, xs) -> key, xs |> Seq.map snd |> Set.ofSeq)
                |> Map.ofSeq
            let nodesByDevice =
                works
                |> Seq.collect (fun w -> w.Nodes |> Seq.map (fun (key, dev, _) -> dev, key))
                |> Seq.groupBy fst
                |> Seq.map (fun (dev, xs) -> dev, xs |> Seq.map snd |> Set.ofSeq)
            for dev, nodeKeys in nodesByDevice do
                let shared =
                    nodeKeys
                    |> Seq.choose (fun k -> flowsByNode |> Map.tryFind k |> Option.map (fun f -> k, f))
                    |> Seq.filter (fun (_, flows) -> flows.Count > 1)
                    |> Seq.toList
                for sharedKey, sharedFlows in shared do
                    for otherKey in nodeKeys do
                        if otherKey <> sharedKey then
                            let otherFlows = flowsByNode |> Map.tryFind otherKey |> Option.defaultValue Set.empty
                            let missing = Set.difference sharedFlows otherFlows
                            if not missing.IsEmpty && not (Set.isEmpty otherFlows) then
                                let names = missing |> Set.toList |> String.concat ", "
                                warnings.Add $"AI-W12: '{dev}' 를 Flow 마다 다르게 다룹니다 — '{sharedKey}' 는 부르면서 '{otherKey}' 를 {names} 에서 부르지 않습니다."

        if errors.Count > 0 then Error (List.ofSeq errors)
        else
            Ok { Systems = List.ofSeq systems
                 Flows = List.ofSeq flows
                 Works = List.ofSeq works
                 Arrows = List.ofSeq arrows
                 Apis = List.ofSeq apis
                 Conds = List.ofSeq conds
                 Warnings = List.ofSeq warnings }

    /// 불러오기 전 미리보기. Capa 를 반드시 보여 준다.
    let preview (doc: AiCsvDocument) : AiCsvPreview =
        { ActiveSystemName = doc.Systems |> List.tryFind (fun s -> s.IsActive) |> Option.map (fun s -> s.Name) |> Option.defaultValue ""
          PassiveSystemNames = doc.Systems |> List.filter (fun s -> not s.IsActive) |> List.map (fun s -> s.Name)
          Capa = List.length doc.Flows
          WorkCount = List.length doc.Works
          ArrowCount = doc.Arrows |> List.sumBy (fun a -> List.length a.Targets)
          ApiCount = List.length doc.Apis
          CondCount = List.length doc.Conds
          UnspecifiedTimes = doc.Apis |> List.filter (fun a -> a.Duration.IsNone) |> List.length
          Warnings = doc.Warnings }
