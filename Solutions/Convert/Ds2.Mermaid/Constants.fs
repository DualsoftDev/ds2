namespace Ds2.Mermaid

open System

/// 상수 및 기본값 정의
[<AutoOpen>]
module Constants =

    /// 정규식 패턴
    module Patterns =
        open System.Text.RegularExpressions

        /// 그래프 방향 패턴
        let GraphDirection = Regex(@"^\s*graph\s+(TD|LR|RL|BT)\s*$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

        /// flowchart 방향 패턴 (graph 대신 flowchart 사용하는 경우)
        let FlowchartDirection = Regex(@"^\s*flowchart\s+(TD|LR|RL|BT)\s*$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

        /// 서브그래프 시작 패턴
        let SubgraphStart = Regex(@"^\s*subgraph\s+([^\s\[]+)(?:\s*\[""([^""]+)""\])?\s*$", RegexOptions.Compiled)

        /// 서브그래프 끝 패턴
        let SubgraphEnd = Regex(@"^\s*end\s*$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

        /// 노드 정의 패턴: NodeId["Label"]
        let NodeWithLabel = Regex(@"^\s*(\S+)\[""([^""]+)""\]\s*$", RegexOptions.Compiled)

        /// 주석 패턴
        let Comment = Regex(@"^\s*%%\s*(.*?)$", RegexOptions.Compiled)

        /// commonPre 추출 패턴: [commonPre(CONDITION)]LABEL
        let CommonPre = Regex(@"^\[commonPre\(([^)]+)\)\](.+)$", RegexOptions.Compiled)

        /// 빈 줄 또는 공백만 있는 줄
        let EmptyLine = Regex(@"^\s*$", RegexOptions.Compiled)

    /// Arrow 라벨 문자열 파싱
    let parseArrowLabel (labelStr: string option) : ArrowLabel =
        match labelStr with
        | None -> NoLabel
        | Some s ->
            match s.ToLowerInvariant().Trim() with
            | "reset" -> Interlock
            | "interlock" -> Interlock
            | "selfreset" -> SelfReset
            | "startreset" -> StartReset
            | "startedge" -> StartEdge
            | "resetedge" -> ResetEdge
            | "autopre" -> AutoPre
            | "resetreset" -> ResetReset
            | "group" -> Group
            | other -> Custom other

    /// 그래프 방향 문자열 파싱
    let parseDirection (dirStr: string) : MermaidDirection option =
        match dirStr.ToUpperInvariant() with
        | "TD" -> Some TD
        | "LR" -> Some LR
        | "RL" -> Some RL
        | "BT" -> Some BT
        | _ -> None
