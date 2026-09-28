namespace Ds2.CSV

open System
open System.IO
open System.Text
open Ds2.Core
open Ds2.Core.Store

module CsvImporter =

    let private parseResultToStrings = Result.mapError (List.map ParseError.toString)

    let private distinctEntries (map: CsvEntry -> string) (entries: CsvEntry list) : string list =
        entries |> List.map map |> List.distinct

    let private buildStore projectName systemName =
        let store = DsStore()
        let project = Project(projectName)
        let system = DsSystem(systemName)
        store.DirectWrite(store.Projects, project)
        store.DirectWrite(store.Systems, system)
        project.ActiveSystemIds.Add(system.Id)
        let systemId = system.Id
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
            // 다른 프로세스(Excel 등)가 점유 중이어도 읽을 수 있도록 FileShare.ReadWrite + Delete.
            let content =
                use fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite ||| FileShare.Delete)
                use sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
                sr.ReadToEnd()
            content
            |> CsvParser.parse
            |> parseResultToStrings
        with ex ->
            Error [ $"파일 읽기 실패: {ex.Message}" ]

    let parseContent (content: string) : Result<CsvDocument, string list> =
        content |> CsvParser.parse |> parseResultToStrings

    let buildSystemImportPlan (store: DsStore) (document: CsvDocument) (systemId: Guid) : Result<ImportPlan, string list> =
        match Queries.getSystem systemId store, CsvMapper.tryResolveProjectId store systemId with
        | None, _ ->
            Error [ $"System({systemId})을 찾을 수 없습니다." ]
        | Some _, None ->
            Error [ $"System({systemId})에 연결된 Project를 찾을 수 없습니다." ]
        | Some _, Some projectId ->
            Ok (CsvMapper.mapToSystemPlan store projectId systemId document)

    let loadProject (document: CsvDocument) (projectName: string) (systemName: string) : Result<DsStore, string list> =
        match validateName "Project" projectName, validateName "System" systemName with
        | Error errors, _
        | _, Error errors -> Error errors
        | Ok projectName, Ok systemName ->
            let store, systemId = buildStore projectName systemName
            buildSystemImportPlan store document systemId
            |> Result.map (fun plan ->
                ImportPlan.applyDirect store plan
                store)

    let loadProjectFromFile (filePath: string) : Result<DsStore, string list> =
        match parseFile filePath with
        | Error errors -> Error errors
        | Ok document ->
            let defaultName = Path.GetFileNameWithoutExtension(filePath)
            loadProject document defaultName defaultName

    // ==================== ds2-basic-csv/v1 (3열 FLOW,WORK,CALL) ====================
    // 계약 문서: DualSoftAI docs/workFlowLLM/DS2_BASIC_CSV_AI_GUIDE.md
    // '>' = Call Start 엣지, ';' = 경로 구분(합집합 DAG), 행 순서 = Work StartReset 체인.

    let parseBasicContent (content: string) : Result<BasicCsvDocument, string list> =
        content |> BasicCsvParser.parse |> Result.mapError (List.map ParseError.toString)

    let parseBasicFile (filePath: string) : Result<BasicCsvDocument, string list> =
        try
            let content =
                use fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite ||| FileShare.Delete)
                use sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
                sr.ReadToEnd()
            parseBasicContent content
        with ex ->
            Error [ $"파일 읽기 실패: {ex.Message}" ]

    let previewBasic (document: BasicCsvDocument) : BasicCsvPreview =
        let works = document.Works
        {
            FlowNames = works |> List.map (fun w -> w.FlowName) |> List.distinct
            WorkNames = works |> List.map (fun w -> w.WorkName) |> List.distinct
            PassiveSystemNames =
                works
                |> List.collect (fun w -> w.Nodes |> List.map (fun (_, dev, _) -> dev))
                |> List.distinct
            CallNodeCount = works |> List.sumBy (fun w -> List.length w.Nodes)
            CallEdgeCount = works |> List.sumBy (fun w -> List.length w.Edges)
            WorkArrowCount = max (List.length works - 1) 0
        }

    /// autoStartClear=true 면 첫 Flow 앞 Start(Source), 마지막 Flow 뒤 Clear(Sink) 를 자동 추가한다.
    let buildBasicSystemImportPlanWith (autoStartClear: bool) (store: DsStore) (document: BasicCsvDocument) (systemId: Guid) : Result<ImportPlan, string list> =
        match Queries.getSystem systemId store, CsvMapper.tryResolveProjectId store systemId with
        | None, _ ->
            Error [ $"System({systemId})을 찾을 수 없습니다." ]
        | Some _, None ->
            Error [ $"System({systemId})에 연결된 Project를 찾을 수 없습니다." ]
        | Some _, Some projectId ->
            Ok (BasicCsvMapper.mapToSystemPlanWith autoStartClear store projectId systemId document)

    let buildBasicSystemImportPlan (store: DsStore) (document: BasicCsvDocument) (systemId: Guid) : Result<ImportPlan, string list> =
        buildBasicSystemImportPlanWith false store document systemId

    /// Source Work 마다 TokenSpec 을 하나씩 만들어 둔다.
    ///
    /// TokenRole.Source 만 주고 TokenSpec 을 비워 두면 그래프 검증이 «TokenSpec 미설정» 으로
    /// 경고하고, 토큰 이름이 "Work이름#번호" 로 표시된다 — 불러오자마자 손댈 것이 남는다.
    /// 라벨은 Work 이름을 그대로 쓴다(그 토큰이 어디서 나오는지가 곧 이름이다).
    let private ensureTokenSpecsForSources (store: DsStore) =
        let sources =
            store.Works.Values
            |> Seq.filter (fun w -> w.TokenRole.HasFlag(TokenRole.Source))
            |> Seq.toList
        if not sources.IsEmpty then
            for project in store.Projects.Values do
                let linked =
                    project.TokenSpecs |> Seq.choose (fun spec -> spec.WorkId) |> Set.ofSeq
                let mutable nextId =
                    if project.TokenSpecs.Count = 0 then 1
                    else (project.TokenSpecs |> Seq.map (fun spec -> spec.Id) |> Seq.max) + 1
                for work in sources do
                    if not (linked.Contains work.Id) then
                        project.TokenSpecs.Add(
                            { Id = nextId
                              Label = work.LocalName
                              Fields = Map.empty
                              WorkId = Some work.Id })
                        nextId <- nextId + 1

    let loadBasicProjectWith (autoStartClear: bool) (document: BasicCsvDocument) (projectName: string) (systemName: string) : Result<DsStore, string list> =
        match validateName "Project" projectName, validateName "System" systemName with
        | Error errors, _
        | _, Error errors -> Error errors
        | Ok projectName, Ok systemName ->
            let store, systemId = buildStore projectName systemName
            buildBasicSystemImportPlanWith autoStartClear store document systemId
            |> Result.map (fun plan ->
                ImportPlan.applyDirect store plan
                ensureTokenSpecsForSources store
                store)

    /// csvForAI LLM 생성 지침. 어셈블리에 임베드된 docs/CSV_FOR_AI_GUIDE.md 를 그대로 돌려준다.
    ///
    /// 코드 상수로 복제하지 않는다 — 지침·문서·RAG 사본이 갈라지면 어느 것이 진짜인지 알 수 없게 되고,
    /// 실제로 기존 3열 지침 상수와 SSOT 문서가 이미 어긋나 있다(§13 «v1 비지원» 목록).
    let aiLlmGuide () : string =
        let asm = Reflection.Assembly.GetExecutingAssembly()
        let name =
            asm.GetManifestResourceNames()
            |> Array.tryFind (fun n -> n.EndsWith("CSV_FOR_AI_GUIDE.md", StringComparison.OrdinalIgnoreCase))
        match name with
        | None -> "(지침 문서를 찾을 수 없습니다 — docs/CSV_FOR_AI_GUIDE.md 가 임베드되지 않았습니다.)"
        | Some n ->
            use stream = asm.GetManifestResourceStream n
            use reader = new IO.StreamReader(stream, Text.Encoding.UTF8)
            reader.ReadToEnd()

    // ---------- ds2-csv-for-ai/v1 (7열) ----------

    let parseAiContent (content: string) : Result<AiCsvDocument, string list> =
        AiCsvParser.parse content
        |> Result.mapError (List.map (fun (e: ParseError) ->
            if e.LineNumber > 0 then $"{e.LineNumber}행: {e.Message}" else e.Message))

    let parseAiFile (path: string) : Result<AiCsvDocument, string list> =
        try parseAiContent (IO.File.ReadAllText(path, Text.Encoding.UTF8))
        with ex -> Error [ $"파일을 읽을 수 없습니다: {ex.Message}" ]

    let previewAi (document: AiCsvDocument) = AiCsvParser.preview document

    /// store 를 변경하지 않는다(plan 생성 계약).
    let buildAiSystemImportPlan (store: DsStore) (document: AiCsvDocument) (systemId: Guid) : Result<ImportPlan, string list> =
        match Queries.getSystem systemId store with
        | None -> Error [ $"System {systemId} 을 찾을 수 없습니다." ]
        | Some _ ->
            match store.Projects.Values |> Seq.tryHead with
            | None -> Error [ "Project 가 없습니다." ]
            | Some project -> Ok (AiCsvMapper.mapToSystemPlan store project.Id systemId document)

    /// Active System 이름은 **SYS Active 행에서** 가져온다 — 모델이 스스로 이름을 갖는다.
    let loadAiProject (document: AiCsvDocument) (projectName: string) : Result<DsStore, string list> =
        let activeName =
            document.Systems |> List.tryFind (fun s -> s.IsActive) |> Option.map (fun s -> s.Name)
        match activeName with
        | None -> Error [ "AI060: Active System 행이 없습니다." ]
        | Some systemName ->
            match validateName "Project" projectName, validateName "System" systemName with
            | Error errors, _
            | _, Error errors -> Error errors
            | Ok projectName, Ok systemName ->
                let store, systemId = buildStore projectName systemName
                buildAiSystemImportPlan store document systemId
                |> Result.map (fun plan ->
                    ImportPlan.applyDirect store plan
                    // ApiDef 특성·IO 태그·조건·초기 Finish 는 엔티티가 생긴 뒤라야 붙일 수 있다.
                    AiCsvMapper.applyPostImport store document |> ignore
                    // 기존 규칙 재사용 — Source 마다 TokenSpec 을 붙여 «TokenSpec 미설정» 경고를 없앤다.
                    ensureTokenSpecsForSources store
                    store)

    let loadBasicProject (document: BasicCsvDocument) (projectName: string) (systemName: string) : Result<DsStore, string list> =
        loadBasicProjectWith false document projectName systemName
