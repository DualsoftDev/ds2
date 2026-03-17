namespace Ds2.Mermaid

open System

/// Mermaid 토큰 타입
type MermaidToken =
    | GraphDir of MermaidDirection
    | SubgraphStart of id: string * displayName: string option
    | SubgraphEnd
    | NodeDef of id: string * label: string
    | SolidArrowToken of source: string * target: string * label: string option
    | DashedArrowToken of source: string * target: string * label: string option
    | CircleArrowToken of source: string * target: string * label: string option
    | CommentToken of string
    | PassiveMarker
    | EmptyLineToken
    | UnknownToken of string

/// 토큰화된 라인
type TokenizedLine = {
    LineNumber: int
    OriginalText: string
    Token: MermaidToken
}

/// Mermaid 렉서 모듈
module MermaidLexer =

    let private tryParseArrowToken (trimmed: string) : MermaidToken option =
        let parseWithArrow (arrow: string) (makeToken: string * string * string option -> MermaidToken) =
            let arrowIdx = trimmed.IndexOf(arrow, StringComparison.Ordinal)
            if arrowIdx < 0 then
                None
            else
                let source = trimmed[.. arrowIdx - 1].Trim()
                let remainder = trimmed[(arrowIdx + arrow.Length) ..].Trim()
                if String.IsNullOrWhiteSpace(source) || String.IsNullOrWhiteSpace(remainder) then
                    None
                elif remainder.StartsWith("|", StringComparison.Ordinal) then
                    let labelEnd = remainder.IndexOf("|", 1, StringComparison.Ordinal)
                    if labelEnd <= 1 || labelEnd >= remainder.Length - 1 then
                        None
                    else
                        let label = remainder[1 .. labelEnd - 1]
                        let target = remainder[(labelEnd + 1) ..].Trim()
                        if String.IsNullOrWhiteSpace(target) then None
                        else Some (makeToken (source, target, Some label))
                else
                    Some (makeToken (source, remainder, None))

        parseWithArrow "-.->" DashedArrowToken
        |> Option.orElseWith (fun () -> parseWithArrow "o--o" CircleArrowToken)
        |> Option.orElseWith (fun () -> parseWithArrow "-->" SolidArrowToken)

    /// 단일 라인을 토큰으로 변환
    let tokenizeLine (lineNumber: int) (line: string) : TokenizedLine =
        let trimmed = line.Trim()

        let token =
            if Patterns.EmptyLine.IsMatch(trimmed) then
                EmptyLineToken
            elif Patterns.Comment.IsMatch(trimmed) then
                let m = Patterns.Comment.Match(trimmed)
                let commentText = if m.Groups.Count > 1 then m.Groups.[1].Value.Trim() else ""
                if commentText.Equals("[Passive]", StringComparison.OrdinalIgnoreCase) then
                    PassiveMarker
                else
                    CommentToken commentText
            elif Patterns.GraphDirection.IsMatch(trimmed) then
                let m = Patterns.GraphDirection.Match(trimmed)
                match parseDirection m.Groups.[1].Value with
                | Some dir -> GraphDir dir
                | None -> UnknownToken trimmed
            elif Patterns.FlowchartDirection.IsMatch(trimmed) then
                let m = Patterns.FlowchartDirection.Match(trimmed)
                match parseDirection m.Groups.[1].Value with
                | Some dir -> GraphDir dir
                | None -> UnknownToken trimmed
            elif Patterns.SubgraphStart.IsMatch(trimmed) then
                let m = Patterns.SubgraphStart.Match(trimmed)
                let id = m.Groups.[1].Value
                let displayName =
                    if m.Groups.[2].Success && not (String.IsNullOrWhiteSpace(m.Groups.[2].Value))
                    then Some m.Groups.[2].Value
                    else None
                SubgraphStart (id, displayName)
            elif Patterns.SubgraphEnd.IsMatch(trimmed) then
                SubgraphEnd
            else
                match tryParseArrowToken trimmed with
                | Some arrowToken -> arrowToken
                | None when Patterns.NodeWithLabel.IsMatch(trimmed) ->
                    let m = Patterns.NodeWithLabel.Match(trimmed)
                    NodeDef (m.Groups.[1].Value, m.Groups.[2].Value)
                | None ->
                    UnknownToken trimmed

        { LineNumber = lineNumber; OriginalText = line; Token = token }

    /// 전체 내용을 토큰 목록으로 변환
    let tokenize (content: string) : TokenizedLine list =
        content.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.mapi (fun i line -> tokenizeLine (i + 1) line)
        |> Array.toList

    /// 노드 라벨에서 commonPre 조건 추출 (Legacy Ev2 형식: [commonPre(NODE_ID)]LABEL)
    let extractCommonConditions (label: string) : string list * string =
        let m = Patterns.CommonPre.Match(label)
        if m.Success then
            let condition = m.Groups.[1].Value
            let actualLabel = m.Groups.[2].Value
            ([condition], actualLabel)
        else
            ([], label)

    /// 노드 라벨에서 ds2 조건 참조 추출 (형식: CallName<br>AutoAux: src1, src2<br>ComAux: src3)
    /// 반환: (actualLabel, autoAuxRefs, comAuxRefs, skipUnmatchRefs)
    let extractConditionRefs (label: string) : string * string list * string list * string list =
        let parts = label.Split([| "<br>" |], StringSplitOptions.None)
        if parts.Length <= 1 then
            (label, [], [], [])
        else
            let actualLabel = parts.[0].Trim()
            let mutable autoAuxRefs = []
            let mutable comAuxRefs = []
            let mutable skipUnmatchRefs = []

            for i in 1 .. parts.Length - 1 do
                let part = parts.[i].Trim()
                let parseNames (prefix: string) =
                    if part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                        let names =
                            part.[prefix.Length..]
                                .Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.map (fun s -> s.Trim())
                            |> Array.filter (fun s -> s <> "")
                            |> Array.toList
                        Some names
                    else None

                match parseNames "AutoAux: " with
                | Some names -> autoAuxRefs <- names
                | None ->
                    match parseNames "ComAux: " with
                    | Some names -> comAuxRefs <- names
                    | None ->
                        match parseNames "SkipUnmatch: " with
                        | Some names -> skipUnmatchRefs <- names
                        | None -> ()

            (actualLabel, autoAuxRefs, comAuxRefs, skipUnmatchRefs)

    /// 유효한 토큰만 필터링 (빈 줄, 주석 제외)
    let filterSignificantTokens (tokens: TokenizedLine list) : TokenizedLine list =
        tokens
        |> List.filter (fun t ->
            match t.Token with
            | EmptyLineToken | CommentToken _ -> false
            | _ -> true)
