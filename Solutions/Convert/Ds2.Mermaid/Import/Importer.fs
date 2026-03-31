namespace Ds2.Mermaid

open System
open System.IO
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

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
        | SystemLevel -> None // System 레벨에서는 불필요
        | FlowLevel ->
            store.FlowsReadOnly.TryGetValue(parentId)
            |> function
                | true, flow -> Some flow.ParentId
                | _ -> None
        | WorkLevel ->
            Queries.trySystemIdOfWork parentId store

    /// parentId에서 projectId 조회 (Device auto-creation용)
    let private resolveProjectId (store: DsStore) (level: ImportLevel) (parentId: Guid) : Guid option =
        let findProject systemId =
            store.ProjectsReadOnly.Values
            |> Seq.tryFind (fun p ->
                p.ActiveSystemIds.Contains(systemId) || p.PassiveSystemIds.Contains(systemId))
            |> Option.map (fun p -> p.Id)
        match level with
        | SystemLevel -> Some parentId // parentId가 곧 projectId
        | FlowLevel ->
            store.FlowsReadOnly.TryGetValue(parentId)
            |> function
                | true, flow -> findProject flow.ParentId
                | _ -> None
        | WorkLevel ->
            Queries.trySystemIdOfWork parentId store
            |> Option.bind findProject

    /// Mermaid 그래프를 현재 문서에 적용할 ImportPlan 생성
    let buildImportPlan (store: DsStore) (graph: MermaidGraph) (level: ImportLevel) (parentId: Guid) : Result<ImportPlan, string list> =
        // 검증
        match MermaidAnalyzer.validate graph level with
        | Invalid errors -> Error errors
        | Valid ->

        let hasSubgraphs = not graph.Subgraphs.IsEmpty
        let projectId = resolveProjectId store level parentId

        match level with
        | SystemLevel ->
            Ok (MermaidMapper.mapToSystem store parentId graph)
        | FlowLevel ->
            match resolveSystemId store level parentId with
            | Some systemId when hasSubgraphs ->
                Ok (MermaidMapper.mapToFlow store parentId systemId projectId graph)
            | Some systemId ->
                Ok (MermaidMapper.mapToFlowFlat store parentId systemId graph)
            | None ->
                Error [ $"Flow({parentId})에서 System을 찾을 수 없습니다" ]
        | WorkLevel ->
            Ok (MermaidMapper.mapToWork store parentId projectId graph)

    /// Mermaid 파일을 프로젝트로 불러오기 (새 DsStore 생성 후 반환)
    let loadProjectFromFile (filePath: string) : Result<DsStore, string list> =
        match parseFile filePath with
        | Error errors -> Error errors
        | Ok graph ->

        let projectName =
            Path.GetFileNameWithoutExtension(filePath)

        let newStore = DsStore()
        let project = Project(projectName)
        newStore.DirectWrite(newStore.Projects, project)

        match buildImportPlan newStore graph SystemLevel project.Id with
        | Error errors -> Error errors
        | Ok plan ->
            ImportPlan.applyDirect newStore plan
            Ok newStore
