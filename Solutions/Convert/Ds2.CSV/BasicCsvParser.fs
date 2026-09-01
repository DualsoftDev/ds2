namespace Ds2.CSV

open System
open System.Text
open System.Collections.Generic

/// ds2-basic-csv/v1 파서 + fail-fast 검증기.
/// 오류는 전량 집계 후 한 번에 Error 로 반환한다(부분 import 금지).
/// 오류 코드: CSV001~006, CALL001~002, DAG001~002 (계약 문서 §9).
/// 연산자는 '>'와 ';' 2개뿐 — 별칭 문법(ID=디바이스.액션)은 지원하지 않는다.
module BasicCsvParser =

    // ---------- 전처리 ----------

    /// BOM 제거 + NFC 정규화 + 전각→반각 폴딩(U+FF01..U+FF5E, U+3000).
    /// 한국어 LLM 출력의 전각 구분자(＞ ； ， ＝)를 흡수한다.
    let internal normalize (content: string) : string =
        let noBom =
            if not (String.IsNullOrEmpty content) && content.[0] = '\uFEFF'
            then content.Substring(1) else content
        let nfc = noBom.Normalize(NormalizationForm.FormC)
        let sb = StringBuilder(nfc.Length)
        for ch in nfc do
            let code = int ch
            if code >= 0xFF01 && code <= 0xFF5E then sb.Append(char (code - 0xFEE0)) |> ignore
            elif code = 0x3000 then sb.Append(' ') |> ignore
            else sb.Append(ch) |> ignore
        sb.ToString()

    // ---------- 이름 검증 ----------

    let private reservedDevices = HashSet<string>([ "BUFFER"; "CLEAR" ], StringComparer.OrdinalIgnoreCase)
    let private reservedApis = HashSet<string>([ "DO"; "-" ], StringComparer.OrdinalIgnoreCase)

    let private namePartInvalid (part: string) =
        part |> Seq.exists (fun c -> c = '.' || c = '>' || c = ';' || c = '=' || c = ',' || c = '"')

    /// "디바이스.액션" 분해: 정확히 '.' 1개, 양쪽 비공백, 금지문자/예약어 검사.
    let private tryParseCallName (token: string) : Result<string * string, string> =
        let dotCount = token |> Seq.filter ((=) '.') |> Seq.length
        if dotCount = 0 then Error $"CALL001: '{token}' 에 '.'이 없습니다. Call은 '디바이스.액션' 형식이어야 합니다."
        elif dotCount > 1 then Error $"CALL001: '{token}' 에 '.'이 2개 이상입니다."
        else
            let idx = token.IndexOf('.')
            let dev = token.Substring(0, idx).Trim()
            let api = token.Substring(idx + 1).Trim()
            if dev = "" then Error $"CALL001: '{token}' 의 디바이스가 비어 있습니다."
            elif api = "" then Error $"CALL001: '{token}' 의 액션이 비어 있습니다."
            elif namePartInvalid dev || namePartInvalid api then
                Error $"CALL001: '{token}' 에 금지 문자(. > ; = , 따옴표)가 포함되어 있습니다."
            elif dev.StartsWith("@") then Error $"CSV006: 디바이스 이름 '{dev}' 은 예약 접두어 '@'로 시작할 수 없습니다."
            elif reservedDevices.Contains(dev) then Error $"CALL001: 디바이스 이름 '{dev}' 은 예약어입니다."
            elif reservedApis.Contains(api) then Error $"CALL001: 액션 이름 '{api}' 은 예약어입니다."
            else Ok (dev, api)

    // ---------- CALL 셀 DSL ----------

    /// CALL 셀 하나를 노드/엣지 집합으로 파싱.
    /// '>' = Start 엣지, ';' = 경로 구분, 경로 합집합 = DAG, 동일 이름 = 동일 노드.
    /// 별칭 문법은 없다 — 공유 노드는 전체 이름을 반복하면 자동 병합된다.
    let internal parseCallCell (lineNumber: int) (cell: string)
        : Result<(string * string * string) list * (string * string) list, ParseError list> =
        let errors = ResizeArray<ParseError>()
        let err (msg: string) = errors.Add { LineNumber = lineNumber; Message = msg }

        let nodeOrder = ResizeArray<string>()
        let nodeInfo = Dictionary<string, string * string>()
        let edgeSet = HashSet<string * string>()
        let edgeOrder = ResizeArray<string * string>()

        let ensureNode (key: string) (dev: string) (api: string) =
            if not (nodeInfo.ContainsKey key) then
                nodeInfo.[key] <- (dev, api)
                nodeOrder.Add key

        // 토큰 하나 → 노드 키(오류 시 None, errors 에 축적)
        let resolveToken (raw: string) : string option =
            let token = raw.Trim()
            if token = "" then
                err "CALL002: 빈 노드가 있습니다('>>', 선행/후행 '>' 등)."
                None
            elif token.Contains("=") then
                err $"CALL001: '{token}' — 별칭 문법(ID=디바이스.액션)은 지원하지 않습니다. '디바이스.액션' 전체 이름을 반복하면 같은 노드로 병합됩니다."
                None
            else
                match tryParseCallName token with
                | Error msg -> err msg; None
                | Ok (dev, api) ->
                    let key = $"{dev}.{api}"
                    ensureNode key dev api
                    Some key

        let routes = cell.Split(';')
        for route in routes do
            if route.Trim() = "" then
                err "CALL002: 빈 경로가 있습니다(';;' 또는 선행/후행 ';')."
            else
                let tokens = route.Split('>')
                let keys = tokens |> Array.map resolveToken
                for i in 0 .. keys.Length - 2 do
                    match keys.[i], keys.[i + 1] with
                    | Some src, Some dst ->
                        if src = dst then
                            err $"DAG001: 자기 자신으로의 Edge '{src} > {dst}' 는 금지됩니다."
                        elif edgeSet.Add((src, dst)) then
                            edgeOrder.Add((src, dst))
                    | _ -> ()

        // 셀 단위 순환 검출 (Kahn)
        if errors.Count = 0 && edgeOrder.Count > 0 then
            let indegree = Dictionary<string, int>()
            for key in nodeOrder do indegree.[key] <- 0
            for (_, dst) in edgeOrder do indegree.[dst] <- indegree.[dst] + 1
            let queue = Queue<string>()
            for key in nodeOrder do
                if indegree.[key] = 0 then queue.Enqueue key
            let mutable visited = 0
            while queue.Count > 0 do
                let n = queue.Dequeue()
                visited <- visited + 1
                for (src, dst) in edgeOrder do
                    if src = n then
                        indegree.[dst] <- indegree.[dst] - 1
                        if indegree.[dst] = 0 then queue.Enqueue dst
            if visited < nodeOrder.Count then
                let remaining =
                    nodeOrder |> Seq.filter (fun k -> indegree.[k] > 0) |> String.concat ", "
                err $"DAG002: CALL 경로에 순환이 있습니다: {remaining}"

        if errors.Count > 0 then Error (List.ofSeq errors)
        else
            let nodes = [ for key in nodeOrder -> let dev, api = nodeInfo.[key] in key, dev, api ]
            Ok (nodes, List.ofSeq edgeOrder)

    // ---------- 문서 파싱 ----------

    let private expectedHeaderFields = [ "flow"; "work"; "call" ]

    let parse (content: string) : Result<BasicCsvDocument, ParseError list> =
        let normalized = normalize content
        let lines =
            normalized.Replace("\r\n", "\n").Split('\n')
            |> Array.mapi (fun i line -> i + 1, line)
            |> Array.filter (fun (_, line) -> line.Trim() <> "")

        if lines.Length = 0 then
            Error [ { LineNumber = 0; Message = "CSV001: 내용이 비어 있습니다." } ]
        else
            let errors = ResizeArray<ParseError>()
            let warnings = ResizeArray<string>()
            let err line (msg: string) = errors.Add { LineNumber = line; Message = msg }

            // 헤더 검증 (CSV001)
            let headerLine, headerText = lines.[0]
            let headerFields =
                headerText.Split(',') |> Array.map (fun f -> f.Trim().ToLowerInvariant()) |> List.ofArray
            if headerFields <> expectedHeaderFields then
                err headerLine "CSV001: 헤더가 'FLOW,WORK,CALL' 이 아닙니다."

            let works = ResizeArray<BasicCsvWork>()
            let seenWorkKeys = HashSet<string>()
            let seenFlows = HashSet<string>()
            let warnedFlows = HashSet<string>()
            let mutable prevFlow: string option = None

            if errors.Count = 0 then
                for lineNumber, line in Array.skip 1 lines do
                    match CsvParser.splitCsvLine lineNumber line with
                    | Error parseError -> errors.Add parseError
                    | Ok fields ->
                        if List.length fields <> 3 then
                            err lineNumber $"CSV002: 열 개수가 3이 아닙니다({List.length fields}열)."
                        else
                            let flowName = (List.item 0 fields).Trim()
                            let workName = (List.item 1 fields).Trim()
                            let callCell = (List.item 2 fields).Trim()
                            if flowName = "" then err lineNumber "CSV003: FLOW 가 비어 있습니다."
                            if workName = "" then err lineNumber "CSV003: WORK 가 비어 있습니다."
                            if callCell = "" then err lineNumber "CSV003: CALL 이 비어 있습니다."
                            if flowName.StartsWith("@") then
                                err lineNumber $"CSV006: FLOW 이름 '{flowName}' 은 예약 접두어 '@'로 시작할 수 없습니다."
                            if workName.StartsWith("@") then
                                err lineNumber $"CSV006: WORK 이름 '{workName}' 은 예약 접두어 '@'로 시작할 수 없습니다."
                            if flowName <> "" && workName <> "" && callCell <> ""
                               && not (flowName.StartsWith("@")) && not (workName.StartsWith("@")) then
                                let workKey = flowName + " " + workName
                                if not (seenWorkKeys.Add workKey) then
                                    err lineNumber $"CSV004: (FLOW,WORK)=({flowName},{workName}) 조합이 중복됩니다."
                                // CSV005: 같은 FLOW 행 비연속 경고
                                match prevFlow with
                                | Some prev when prev <> flowName && seenFlows.Contains flowName ->
                                    if warnedFlows.Add flowName then
                                        warnings.Add $"CSV005: FLOW '{flowName}' 행이 비연속입니다(행 {lineNumber}). 행 순서가 실행 순서이므로 정렬 사고 여부를 확인하세요."
                                | _ -> ()
                                seenFlows.Add flowName |> ignore
                                prevFlow <- Some flowName

                                match parseCallCell lineNumber callCell with
                                | Error cellErrors -> errors.AddRange cellErrors
                                | Ok (nodes, edges) ->
                                    works.Add {
                                        FlowName = flowName
                                        WorkName = workName
                                        Nodes = nodes
                                        Edges = edges
                                        LineNumber = lineNumber
                                    }

            // DEV001(경고): API가 1개뿐인 디바이스 — 구동류는 상보 동작 쌍(전진-후진, ON-OFF) 권장
            if errors.Count = 0 then
                let apisByDevice = Dictionary<string, HashSet<string>>()
                let deviceOrder = ResizeArray<string>()
                for basicWork in works do
                    for (_, dev, api) in basicWork.Nodes do
                        match apisByDevice.TryGetValue dev with
                        | true, apis -> apis.Add api |> ignore
                        | false, _ ->
                            apisByDevice.[dev] <- HashSet<string>([ api ])
                            deviceOrder.Add dev
                let singles =
                    deviceOrder
                    |> Seq.filter (fun dev -> apisByDevice.[dev].Count = 1)
                    |> Seq.map (fun dev -> $"{dev}({Seq.head apisByDevice.[dev]})")
                    |> List.ofSeq
                if not (List.isEmpty singles) then
                    let joined = String.concat ", " singles
                    warnings.Add(
                        $"DEV001: API가 1개뿐인 디바이스 {List.length singles}개 — {joined}. "
                        + "각 디바이스 Flow 에 DONE 더미 Work 가 자동 추가되어 1회 동작 후 자동 복귀합니다(재기동 가능). "
                        + "실린더·모터류 구동 디바이스라면 반대 동작(후진·하강·OFF)이 누락되지 않았는지 확인하세요"
                        + "(센서·출력 전용 디바이스는 정상입니다).")

            if errors.Count = 0 && works.Count = 0 then
                err 0 "CSV003: 데이터 행이 없습니다."

            if errors.Count > 0 then Error (List.ofSeq errors)
            else Ok { Works = List.ofSeq works; Warnings = List.ofSeq warnings }
