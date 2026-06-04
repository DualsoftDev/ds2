namespace Ds2.Mermaid

open System

/// 상수 및 기본값 정의
[<AutoOpen>]
module Constants =

    /// 파일 확장자 · 사이드카 규약
    module FileFormat =
        /// 신규 mermaid 출력 확장자 (권장)
        let [<Literal>] MermaidExt = ".mmd"
        /// 구 mermaid 출력 확장자 (호환 유지)
        let [<Literal>] MermaidExtLegacy = ".md"
        /// IoTag 페어 사이드카 suffix — `<stem>.iotag.json`
        let [<Literal>] IoTagPairSuffix = ".iotag.json"
        /// LLM 응답 안 IoTag 페어 fence 라벨 (` ```iotag-json `)
        let [<Literal>] IoTagJsonFence = "iotag-json"
        /// 구 fence 라벨 (deprecated, backward-compat 만 인식)
        let [<Literal>] PlcBindingsFenceLegacy = "plc-bindings"

    /// 모델 구조 기본값
    module Model =
        /// 단일 implicit Active System 의 기본 이름 (ai-core §0)
        let [<Literal>] DefaultSystemName = "Main"
        /// IoTag callPath 구분자 — escape-aware (`\.` 는 literal)
        let [<Literal>] CallPathSeparator = "."

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
