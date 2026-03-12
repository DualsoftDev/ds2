namespace Ds2.Mermaid

open System
open System.IO
open Ds2.Core
open Ds2.UI.Core

/// Mermaid 임포트 공개 진입점
module MermaidImporter =

    /// Mermaid 파일을 읽고 파싱
    let parseFile (filePath: string) : Result<MermaidGraph, string list> =
        try
            let content = File.ReadAllText(filePath)
            match MermaidParser.parse content with
            | Ok graph -> Ok graph
            | Error errors -> Error (errors |> List.map ParseError.toString)
        with ex ->
            Error [$"파일 읽기 실패: {ex.Message}"]

    /// Mermaid 문자열을 파싱
    let parseContent (content: string) : Result<MermaidGraph, string list> =
        match MermaidParser.parse content with
        | Ok graph -> Ok graph
        | Error errors -> Error (errors |> List.map ParseError.toString)

    /// 프리뷰 생성 (store 변경 없이)
    let preview (graph: MermaidGraph) (level: ImportLevel) : ImportPreview =
        MermaidMapper.buildPreview graph level

    /// parentId에서 systemId 조회 (Flow/Work 레벨용)
    let private resolveSystemId (store: DsStore) (level: ImportLevel) (parentId: Guid) : Guid option =
        match level with
        | SystemLevel -> Some parentId
        | FlowLevel ->
            store.FlowsReadOnly.TryGetValue(parentId)
            |> function
                | true, flow -> Some flow.ParentId
                | _ -> None
        | WorkLevel ->
            DsQuery.trySystemIdOfWork parentId store

    /// parentId에서 projectId 조회 (Device auto-creation용)
    let private resolveProjectId (store: DsStore) (level: ImportLevel) (parentId: Guid) : Guid option =
        match level with
        | SystemLevel ->
            store.ProjectsReadOnly.Values
            |> Seq.tryFind (fun p ->
                p.ActiveSystemIds.Contains(parentId) || p.PassiveSystemIds.Contains(parentId))
            |> Option.map (fun p -> p.Id)
        | FlowLevel ->
            store.FlowsReadOnly.TryGetValue(parentId)
            |> function
                | true, flow ->
                    store.ProjectsReadOnly.Values
                    |> Seq.tryFind (fun p ->
                        p.ActiveSystemIds.Contains(flow.ParentId) || p.PassiveSystemIds.Contains(flow.ParentId))
                    |> Option.map (fun p -> p.Id)
                | _ -> None
        | WorkLevel ->
            DsQuery.trySystemIdOfWork parentId store
            |> Option.bind (fun sysId ->
                store.ProjectsReadOnly.Values
                |> Seq.tryFind (fun p ->
                    p.ActiveSystemIds.Contains(sysId) || p.PassiveSystemIds.Contains(sysId))
                |> Option.map (fun p -> p.Id))

    /// Mermaid 그래프를 DsStore에 임포트 (WithTransaction으로 Undo 1회 보장)
    let importIntoStore (store: DsStore) (graph: MermaidGraph) (level: ImportLevel) (parentId: Guid) : Result<unit, string list> =
        // 검증
        match MermaidAnalyzer.validate graph level with
        | Invalid errors -> Error errors
        | Valid ->

        let hasSubgraphs = not graph.Subgraphs.IsEmpty
        let projectId = resolveProjectId store level parentId

        store.WithTransaction("Mermaid 임포트", fun () ->
            match level with
            | SystemLevel when hasSubgraphs ->
                MermaidMapper.mapToSystem store parentId projectId graph |> ignore
            | SystemLevel ->
                MermaidMapper.mapToSystemFlat store parentId graph |> ignore
            | FlowLevel ->
                match resolveSystemId store level parentId with
                | Some systemId when hasSubgraphs ->
                    MermaidMapper.mapToFlow store parentId systemId projectId graph |> ignore
                | Some systemId ->
                    MermaidMapper.mapToFlowFlat store parentId systemId graph |> ignore
                | None ->
                    failwith $"Flow({parentId})에서 System을 찾을 수 없습니다"
            | WorkLevel ->
                MermaidMapper.mapToWork store parentId projectId graph |> ignore
        )

        store.EmitRefreshAndHistory()
        Ok ()
