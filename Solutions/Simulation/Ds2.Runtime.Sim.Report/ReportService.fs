namespace Ds2.Runtime.Sim.Report

open System
open System.IO
open Ds2.Runtime.Sim.Report.Model
open Ds2.Runtime.Sim.Report.Exporters

/// 상태 변경 기록 (간트차트용)
type StateChangeRecord = {
    NodeId    : string
    NodeName  : string
    NodeType  : string
    SystemId  : string
    State     : string
    Timestamp : DateTime
}

/// 리포트 서비스 - 시뮬레이션 결과 리포트 생성 및 내보내기
module ReportService =
    let private createSegments (endTime: DateTime) (stateHistory: (string * DateTime) list) =
        let transitions =
            stateHistory
            |> List.pairwise
            |> List.map (fun ((state, startTs), (_, endTs)) ->
                StateSegment.create state startTs (Some endTs))

        let finalSegment =
            match stateHistory |> List.tryLast with
            | Some (state, ts) -> [ StateSegment.create state ts (Some endTime) ]
            | None -> []

        transitions @ finalSegment

    let private createMetadata (startTime: DateTime) (endTime: DateTime) (entries: ReportEntry list) =
        let countByType nodeType =
            entries |> List.sumBy (fun entry -> if entry.Type = nodeType then 1 else 0)

        {
            StartTime = startTime
            EndTime = endTime
            TotalDuration = endTime - startTime
            WorkCount = countByType "Work"
            CallCount = countByType "Call"
            GeneratedAt = DateTime.Now
        }

    let private createReport (startTime: DateTime) (endTime: DateTime) (entries: ReportEntry list) : SimulationReport =
        {
            Metadata = createMetadata startTime endTime entries
            Entries = entries
        }

    /// 상태 변경 기록에서 리포트 생성
    let fromStateChanges (startTime: DateTime) (endTime: DateTime) (records: StateChangeRecord seq) : SimulationReport =
        let entries =
            records
            |> Seq.groupBy (fun r -> r.NodeId)
            |> Seq.map (fun (nodeId, nodeRecords) ->
                nodeId, nodeRecords |> Seq.sortBy (fun r -> r.Timestamp) |> Seq.toList)
            |> Seq.mapi (fun i (nodeId, nodeRecords) ->
                let firstRecord = nodeRecords |> List.head
                let stateHistory = nodeRecords |> List.map (fun record -> record.State, record.Timestamp)

                {
                    Id = nodeId
                    Name = firstRecord.NodeName
                    Type = firstRecord.NodeType
                    SystemId = firstRecord.SystemId
                    ParentWorkId = None
                    Segments = createSegments endTime stateHistory
                    RowIndex = i
                })
            |> Seq.toList

        createReport startTime endTime entries

    /// 빈 리포트 생성
    let empty () = SimulationReport.empty ()

    let private getExporter (format: ExportFormat) : IReportExporter =
        match format with
        | Csv -> CsvExporter() :> IReportExporter
        | CsvSummary -> CsvSummaryExporter() :> IReportExporter
        | Html -> HtmlExporter() :> IReportExporter
        | Excel -> ExcelExporter() :> IReportExporter

    /// 내보내기 실행
    let export (report: SimulationReport) (options: ExportOptions) : ExportResult =
        try
            let exporter = getExporter options.Format
            exporter.Export(report, options)
            Success options.FilePath
        with ex ->
            Error ex.Message

    let private inferFormat (filePath: string) : ExportFormat =
        match Path.GetExtension(filePath).ToLowerInvariant() with
        | ".html" | ".htm" -> Html
        | ".xlsx" | ".xls" -> Excel
        | ".csv"           -> Csv
        | _                -> Csv

    /// 파일 확장자에 따라 자동 내보내기
    let exportAuto (report: SimulationReport) (filePath: string) =
        let format = inferFormat filePath
        let options = ExportOptions.defaults format filePath
        export report options
