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
    | CommentToken of string
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

    /// 단일 라인을 토큰으로 변환
    let tokenizeLine (lineNumber: int) (line: string) : TokenizedLine =
        let trimmed = line.Trim()

        let token =
            if Patterns.EmptyLine.IsMatch(trimmed) then
                EmptyLineToken
            elif Patterns.Comment.IsMatch(trimmed) then
                let m = Patterns.Comment.Match(trimmed)
                CommentToken (if m.Groups.Count > 1 then m.Groups.[1].Value else "")
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
            elif Patterns.LabeledSolidArrow.IsMatch(trimmed) then
                let m = Patterns.LabeledSolidArrow.Match(trimmed)
                SolidArrowToken (m.Groups.[1].Value, m.Groups.[3].Value, Some m.Groups.[2].Value)
            elif Patterns.SolidArrow.IsMatch(trimmed) then
                let m = Patterns.SolidArrow.Match(trimmed)
                SolidArrowToken (m.Groups.[1].Value, m.Groups.[2].Value, None)
            elif Patterns.LabeledDashedArrow.IsMatch(trimmed) then
                let m = Patterns.LabeledDashedArrow.Match(trimmed)
                let label = if m.Groups.[2].Success then Some m.Groups.[2].Value else None
                DashedArrowToken (m.Groups.[1].Value, m.Groups.[3].Value, label)
            elif Patterns.DashedArrow.IsMatch(trimmed) then
                let m = Patterns.DashedArrow.Match(trimmed)
                DashedArrowToken (m.Groups.[1].Value, m.Groups.[2].Value, None)
            elif Patterns.NodeWithLabel.IsMatch(trimmed) then
                let m = Patterns.NodeWithLabel.Match(trimmed)
                NodeDef (m.Groups.[1].Value, m.Groups.[2].Value)
            else
                UnknownToken trimmed

        { LineNumber = lineNumber; OriginalText = line; Token = token }

    /// 전체 내용을 토큰 목록으로 변환
    let tokenize (content: string) : TokenizedLine list =
        content.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.mapi (fun i line -> tokenizeLine (i + 1) line)
        |> Array.toList

    /// 노드 라벨에서 commonPre 조건 추출
    let extractCommonConditions (label: string) : string list * string =
        let m = Patterns.CommonPre.Match(label)
        if m.Success then
            let condition = m.Groups.[1].Value
            let actualLabel = m.Groups.[2].Value
            ([condition], actualLabel)
        else
            ([], label)

    /// 유효한 토큰만 필터링 (빈 줄, 주석 제외)
    let filterSignificantTokens (tokens: TokenizedLine list) : TokenizedLine list =
        tokens
        |> List.filter (fun t ->
            match t.Token with
            | EmptyLineToken | CommentToken _ -> false
            | _ -> true)
