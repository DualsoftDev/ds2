namespace Ds2.Mermaid

open System
open System.Collections.Generic

/// Mermaid 파서 모듈
module MermaidParser =

    /// 서브그래프 스택 프레임 (중첩 지원)
    type private SubgraphFrame = {
        Id: string
        DisplayName: string option
        Nodes: MermaidNode list
        Edges: MermaidEdge list
        Children: MermaidSubgraph list
        IsPassive: bool
    }

    /// 파싱 상태
    type private ParserState = {
        Direction: MermaidDirection option
        SubgraphStack: SubgraphFrame list
        Subgraphs: MermaidSubgraph list
        GlobalNodes: MermaidNode list
        GlobalEdges: MermaidEdge list
        AutoPreEdges: MermaidEdge list
        AllNodeIds: HashSet<string>
        Errors: ParseError list
        IsPassiveSection: bool
    }

    let private createInitialState () = {
        Direction = None
        SubgraphStack = []
        Subgraphs = []
        GlobalNodes = []
        GlobalEdges = []
        AutoPreEdges = []
        AllNodeIds = HashSet<string>()
        Errors = []
        IsPassiveSection = false
    }

    /// 현재 가장 안쪽 서브그래프에 노드 추가 (없으면 글로벌)
    let private addNodeToSubgraph (state: ParserState) (nodeId: string) (label: string) : ParserState =
        let conditions, afterLegacy = MermaidLexer.extractCommonConditions label
        let actualLabel, autoAuxRefs, comAuxRefs, skipUnmatchRefs = MermaidLexer.extractConditionRefs afterLegacy
        let node = {
            Id = nodeId
            Label = actualLabel
            CommonConditions = conditions
            AutoAuxConditionRefs = autoAuxRefs
            ComAuxConditionRefs = comAuxRefs
            SkipUnmatchConditionRefs = skipUnmatchRefs
        }
        state.AllNodeIds.Add(nodeId) |> ignore
        match state.SubgraphStack with
        | frame :: rest ->
            let updated = { frame with Nodes = node :: frame.Nodes }
            { state with SubgraphStack = updated :: rest }
        | [] ->
            { state with GlobalNodes = node :: state.GlobalNodes }

    /// Arrow 소스/타겟에서 인라인 노드 정의 처리: A["Label"] → ID="A", 노드 자동 등록
    let private resolveInlineNode (state: ParserState) (raw: string) : ParserState * string =
        let trimmed = raw.Trim()
        let m = Patterns.NodeWithLabel.Match(trimmed)
        if not m.Success then
            state, trimmed
        else
            let nodeId = m.Groups.[1].Value
            let label = m.Groups.[2].Value
            let newState =
                if state.AllNodeIds.Contains(nodeId) then state
                else addNodeToSubgraph state nodeId label
            newState, nodeId

    /// 엣지를 추가 (현재 서브그래프 내부 또는 글로벌)
    let private addEdge (state: ParserState) (source: string) (target: string) (style: MermaidArrowStyle) (label: ArrowLabel) : ParserState =
        let edge = {
            SourceId = source
            TargetId = target
            Style = style
            Label = label
        }

        if label = AutoPre then
            { state with AutoPreEdges = edge :: state.AutoPreEdges }
        else
            match state.SubgraphStack with
            | frame :: rest ->
                let isInternal =
                    frame.Nodes |> List.exists (fun n -> n.Id = source) &&
                    frame.Nodes |> List.exists (fun n -> n.Id = target)
                if isInternal then
                    let updated = { frame with Edges = edge :: frame.Edges }
                    { state with SubgraphStack = updated :: rest }
                else
                    { state with GlobalEdges = edge :: state.GlobalEdges }
            | [] ->
                { state with GlobalEdges = edge :: state.GlobalEdges }

    /// 서브그래프 시작 (스택에 push)
    let private startSubgraph (state: ParserState) (id: string) (displayName: string option) : ParserState =
        let isPassive =
            // top-level subgraph만 IsPassiveSection 적용 (중첩은 부모 따름)
            match state.SubgraphStack with
            | [] -> state.IsPassiveSection
            | parent :: _ -> parent.IsPassive
        let frame = {
            Id = id
            DisplayName = displayName
            Nodes = []
            Edges = []
            Children = []
            IsPassive = isPassive
        }
        { state with SubgraphStack = frame :: state.SubgraphStack }

    /// 서브그래프 종료 (스택에서 pop → 부모의 Children 또는 top-level Subgraphs)
    let private endSubgraph (state: ParserState) : ParserState =
        match state.SubgraphStack with
        | frame :: rest ->
            let subgraph = {
                Id = frame.Id
                DisplayName = frame.DisplayName
                Nodes = List.rev frame.Nodes
                InternalEdges = List.rev frame.Edges
                Children = List.rev frame.Children
                IsPassive = frame.IsPassive
            }
            match rest with
            | parent :: grandparents ->
                let updatedParent = { parent with Children = subgraph :: parent.Children }
                { state with SubgraphStack = updatedParent :: grandparents }
            | [] ->
                { state with
                    SubgraphStack = []
                    Subgraphs = subgraph :: state.Subgraphs }
        | [] ->
            state

    /// 단일 토큰 처리
    let private processToken (state: ParserState) (tokenLine: TokenizedLine) : ParserState =
        match tokenLine.Token with
        | GraphDir dir ->
            { state with Direction = Some dir }

        | SubgraphStart (id, displayName) ->
            startSubgraph state id displayName

        | SubgraphEnd ->
            endSubgraph state

        | NodeDef (id, label) ->
            addNodeToSubgraph state id label

        | SolidArrowToken (source, target, labelOpt) ->
            let state, src = resolveInlineNode state source
            let state, tgt = resolveInlineNode state target
            let label = parseArrowLabel labelOpt
            addEdge state src tgt Solid label

        | DashedArrowToken (source, target, labelOpt) ->
            let state, src = resolveInlineNode state source
            let state, tgt = resolveInlineNode state target
            let label =
                match labelOpt with
                | Some "autoPre" | Some "AutoPre" -> AutoPre
                | _ -> parseArrowLabel labelOpt
            addEdge state src tgt Dashed label

        | CircleArrowToken (source, target, labelOpt) ->
            let state, src = resolveInlineNode state source
            let state, tgt = resolveInlineNode state target
            let label =
                match labelOpt with
                | None -> Group
                | Some _ -> parseArrowLabel labelOpt
            addEdge state src tgt Solid label

        | PassiveMarker ->
            { state with IsPassiveSection = true }

        | CommentToken _ | EmptyLineToken ->
            state

        | UnknownToken _ ->
            state

    /// 재귀적으로 모든 노드→서브그래프 매핑 수집
    let rec private collectNodeMappings (sg: MermaidSubgraph) : (string * string) list =
        let direct = sg.Nodes |> List.map (fun n -> n.Id, sg.Id)
        let fromChildren = sg.Children |> List.collect collectNodeMappings
        direct @ fromChildren

    /// 재귀적으로 서브그래프 트리에서 특정 ID의 서브그래프를 업데이트
    let rec private updateSubgraphInTree (targetId: string) (updater: MermaidSubgraph -> MermaidSubgraph) (sgs: MermaidSubgraph list) : MermaidSubgraph list =
        sgs |> List.map (fun sg ->
            if sg.Id = targetId then updater sg
            else { sg with Children = updateSubgraphInTree targetId updater sg.Children })

    /// 글로벌 엣지를 서브그래프 내부/외부로 재분류
    let private reclassifyEdges (graph: MermaidGraph) : MermaidGraph =
        let nodeToSubgraph =
            graph.Subgraphs
            |> List.collect collectNodeMappings
            |> dict

        let mutable updatedSubgraphs = graph.Subgraphs
        let mutable remainingGlobalEdges = []

        for edge in graph.GlobalEdges do
            let srcSg = nodeToSubgraph.TryGetValue(edge.SourceId) |> snd
            let tgtSg = nodeToSubgraph.TryGetValue(edge.TargetId) |> snd

            if not (String.IsNullOrEmpty(srcSg)) && srcSg = tgtSg then
                updatedSubgraphs <-
                    updateSubgraphInTree srcSg
                        (fun sg -> { sg with InternalEdges = edge :: sg.InternalEdges })
                        updatedSubgraphs
            else
                remainingGlobalEdges <- edge :: remainingGlobalEdges

        { graph with
            Subgraphs = updatedSubgraphs
            GlobalEdges = List.rev remainingGlobalEdges }

    /// Mermaid 내용을 파싱하여 그래프 생성
    let parse (content: string) : Result<MermaidGraph, ParseError list> =
        let tokens = MermaidLexer.tokenize content
        let significantTokens = MermaidLexer.filterSignificantTokens tokens

        let finalState =
            significantTokens
            |> List.fold processToken (createInitialState ())

        // 닫히지 않은 서브그래프 모두 종료
        let finalState =
            let mutable s = finalState
            while not s.SubgraphStack.IsEmpty do
                s <- endSubgraph s
            s

        if not finalState.Errors.IsEmpty then
            Error (List.rev finalState.Errors)
        else
            let subgraphs = List.rev finalState.Subgraphs
            let globalNodes = List.rev finalState.GlobalNodes
            let globalEdges = List.rev finalState.GlobalEdges

            if subgraphs.IsEmpty && globalNodes.IsEmpty then
                Error [EmptyGraph]
            else
                let graph = {
                    Direction = finalState.Direction |> Option.defaultValue TD
                    Subgraphs = subgraphs
                    GlobalNodes = globalNodes
                    GlobalEdges = globalEdges
                    AutoPreEdges = List.rev finalState.AutoPreEdges
                }

                let graph = reclassifyEdges graph

                Ok graph

    /// 파싱 결과 통계
    let getStats (graph: MermaidGraph) =
        let rec countNodes (sg: MermaidSubgraph) =
            sg.Nodes.Length + (sg.Children |> List.sumBy countNodes)
        let rec countEdges (sg: MermaidSubgraph) =
            sg.InternalEdges.Length + (sg.Children |> List.sumBy countEdges)
        let subgraphNodes = graph.Subgraphs |> List.sumBy countNodes
        let totalInternalEdges = graph.Subgraphs |> List.sumBy countEdges
        {|
            SubgraphCount = graph.Subgraphs.Length
            TotalNodes = subgraphNodes + graph.GlobalNodes.Length
            GlobalNodes = graph.GlobalNodes.Length
            InternalEdges = totalInternalEdges
            GlobalEdges = graph.GlobalEdges.Length
            AutoPreEdges = graph.AutoPreEdges.Length
        |}
