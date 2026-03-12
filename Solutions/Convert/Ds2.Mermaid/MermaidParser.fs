namespace Ds2.Mermaid

open System
open System.Collections.Generic

/// Mermaid 파서 모듈
module MermaidParser =

    /// 파싱 상태
    type private ParserState = {
        Direction: MermaidDirection option
        CurrentSubgraph: (string * string option * MermaidNode list * MermaidEdge list) option
        Subgraphs: MermaidSubgraph list
        GlobalNodes: MermaidNode list
        GlobalEdges: MermaidEdge list
        AutoPreEdges: MermaidEdge list
        AllNodeIds: HashSet<string>
        Errors: ParseError list
    }

    let private initialState = {
        Direction = None
        CurrentSubgraph = None
        Subgraphs = []
        GlobalNodes = []
        GlobalEdges = []
        AutoPreEdges = []
        AllNodeIds = HashSet<string>()
        Errors = []
    }

    /// 노드를 서브그래프에 추가 (서브그래프 밖이면 글로벌 노드로 수집)
    let private addNodeToSubgraph (state: ParserState) (nodeId: string) (label: string) : ParserState =
        let conditions, actualLabel = MermaidLexer.extractCommonConditions label
        let node = {
            Id = nodeId
            Label = actualLabel
            CommonConditions = conditions
        }
        state.AllNodeIds.Add(nodeId) |> ignore
        match state.CurrentSubgraph with
        | Some (id, displayName, nodes, edges) ->
            { state with CurrentSubgraph = Some (id, displayName, node :: nodes, edges) }
        | None ->
            { state with GlobalNodes = node :: state.GlobalNodes }

    /// 엣지를 추가 (서브그래프 내부 또는 글로벌)
    let private addEdge (state: ParserState) (source: string) (target: string) (style: MermaidArrowStyle) (label: ArrowLabel) : ParserState =
        let edge = {
            SourceId = source
            TargetId = target
            Style = style
            Label = label
        }

        if label = AutoPre || style = Dashed then
            { state with AutoPreEdges = edge :: state.AutoPreEdges }
        else
            match state.CurrentSubgraph with
            | Some (id, displayName, nodes, edges) ->
                let isInternal =
                    nodes |> List.exists (fun n -> n.Id = source) &&
                    nodes |> List.exists (fun n -> n.Id = target)
                if isInternal then
                    { state with CurrentSubgraph = Some (id, displayName, nodes, edge :: edges) }
                else
                    { state with GlobalEdges = edge :: state.GlobalEdges }
            | None ->
                { state with GlobalEdges = edge :: state.GlobalEdges }

    /// 서브그래프 시작
    let private startSubgraph (state: ParserState) (id: string) (displayName: string option) : ParserState =
        match state.CurrentSubgraph with
        | Some (prevId, _, _, _) ->
            let error = UnclosedSubgraph prevId
            { state with
                CurrentSubgraph = Some (id, displayName, [], [])
                Errors = error :: state.Errors }
        | None ->
            { state with CurrentSubgraph = Some (id, displayName, [], []) }

    /// 서브그래프 종료
    let private endSubgraph (state: ParserState) : ParserState =
        match state.CurrentSubgraph with
        | Some (id, displayName, nodes, edges) ->
            let subgraph = {
                Id = id
                DisplayName = displayName
                Nodes = List.rev nodes
                InternalEdges = List.rev edges
            }
            { state with
                CurrentSubgraph = None
                Subgraphs = subgraph :: state.Subgraphs }
        | None ->
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
            let label = parseArrowLabel labelOpt
            addEdge state source target Solid label

        | DashedArrowToken (source, target, labelOpt) ->
            let label =
                match labelOpt with
                | Some "autoPre" | Some "AutoPre" -> AutoPre
                | _ -> parseArrowLabel labelOpt
            addEdge state source target Dashed label

        | CommentToken _ | EmptyLineToken ->
            state

        | UnknownToken _ ->
            state

    /// 글로벌 엣지를 서브그래프 내부/외부로 재분류
    let private reclassifyEdges (graph: MermaidGraph) : MermaidGraph =
        let nodeToSubgraph =
            graph.Subgraphs
            |> List.collect (fun sg ->
                sg.Nodes |> List.map (fun n -> n.Id, sg.Id))
            |> dict

        let mutable updatedSubgraphs = graph.Subgraphs
        let mutable remainingGlobalEdges = []

        for edge in graph.GlobalEdges do
            let srcSg = nodeToSubgraph.TryGetValue(edge.SourceId) |> snd
            let tgtSg = nodeToSubgraph.TryGetValue(edge.TargetId) |> snd

            if not (String.IsNullOrEmpty(srcSg)) && srcSg = tgtSg then
                updatedSubgraphs <-
                    updatedSubgraphs
                    |> List.map (fun sg ->
                        if sg.Id = srcSg then
                            { sg with InternalEdges = edge :: sg.InternalEdges }
                        else sg)
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
            |> List.fold processToken initialState

        let finalState =
            match finalState.CurrentSubgraph with
            | Some _ -> endSubgraph finalState
            | None -> finalState

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
        let subgraphNodes = graph.Subgraphs |> List.sumBy (fun sg -> sg.Nodes.Length)
        let totalInternalEdges = graph.Subgraphs |> List.sumBy (fun sg -> sg.InternalEdges.Length)
        {|
            SubgraphCount = graph.Subgraphs.Length
            TotalNodes = subgraphNodes + graph.GlobalNodes.Length
            GlobalNodes = graph.GlobalNodes.Length
            InternalEdges = totalInternalEdges
            GlobalEdges = graph.GlobalEdges.Length
            AutoPreEdges = graph.AutoPreEdges.Length
        |}
