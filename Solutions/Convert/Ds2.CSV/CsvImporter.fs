namespace Ds2.CSV

open System
open System.IO
open Ds2.UI.Core

module CsvImporter =

    let private parseResultToStrings = Result.mapError (List.map ParseError.toString)

    let private distinctEntries map entries =
        entries |> List.map map |> List.distinct

    let private buildStore projectName systemName =
        let store = DsStore()
        let projectId = store.AddProject(projectName)
        let systemId = store.AddSystem(systemName, projectId, true)
        store, systemId

    let private validateName label (value: string) =
        let trimmed = value.Trim()
        if String.IsNullOrWhiteSpace(trimmed) then
            Error [ $"{label} 이름이 비어 있습니다." ]
        else
            Ok trimmed

    let preview (document: CsvDocument) : CsvImportPreview =
        let entries = document.Entries
        {
            FlowNames = distinctEntries (fun entry -> entry.FlowName) entries
            WorkNames = distinctEntries (fun entry -> entry.WorkName) entries
            PassiveSystemNames =
                distinctEntries (fun entry -> $"{entry.FlowName}_{entry.DeviceAlias}") entries
            CallNames =
                distinctEntries (fun entry -> $"{entry.DeviceAlias}.{entry.ApiName}") entries
            SyntheticApiCount =
                entries
                |> List.sumBy (fun entry -> if entry.IsSyntheticApi then 1 else 0)
        }

    let parseFile (filePath: string) : Result<CsvDocument, string list> =
        try
            File.ReadAllText(filePath)
            |> CsvParser.parse
            |> parseResultToStrings
        with ex ->
            Error [ $"파일 읽기 실패: {ex.Message}" ]

    let parseContent (content: string) : Result<CsvDocument, string list> =
        content |> CsvParser.parse |> parseResultToStrings

    let importIntoSystem (store: DsStore) (document: CsvDocument) (systemId: Guid) : Result<unit, string list> =
        match DsQuery.getSystem systemId store, CsvMapper.tryResolveProjectId store systemId with
        | None, _ ->
            Error [ $"System({systemId})을 찾을 수 없습니다." ]
        | Some _, None ->
            Error [ $"System({systemId})에 연결된 Project를 찾을 수 없습니다." ]
        | Some _, Some projectId ->
            try
                store.WithTransaction("CSV 임포트", fun () ->
                    CsvMapper.mapToSystem store projectId systemId document)
                store.EmitRefreshAndHistory()
                Ok ()
            with ex ->
                Error [ $"CSV 임포트 실패: {ex.Message}" ]

    let loadProject (document: CsvDocument) (projectName: string) (systemName: string) : Result<DsStore, string list> =
        match validateName "Project" projectName, validateName "System" systemName with
        | Error errors, _
        | _, Error errors -> Error errors
        | Ok projectName, Ok systemName ->
            let store, systemId = buildStore projectName systemName
            importIntoSystem store document systemId
            |> Result.map (fun () -> store)

    let loadProjectFromFile (filePath: string) : Result<DsStore, string list> =
        match parseFile filePath with
        | Error errors -> Error errors
        | Ok document ->
            let defaultName = Path.GetFileNameWithoutExtension(filePath)
            loadProject document defaultName defaultName
