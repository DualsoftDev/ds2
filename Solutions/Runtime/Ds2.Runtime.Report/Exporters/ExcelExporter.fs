namespace Ds2.Runtime.Report.Exporters

open System
open System.Threading.Tasks
open ClosedXML.Excel
open Ds2.Runtime.Report.Model

/// Excel 내보내기 (ClosedXML 사용)
type ExcelExporter() =

    /// 헤더 스타일 적용
    let styleHeader (range: IXLRange) =
        range.Style.Font.Bold <- true
        range.Style.Fill.BackgroundColor <- XLColor.FromHtml("#4fc3f7")
        range.Style.Font.FontColor <- XLColor.Black
        range.Style.Alignment.Horizontal <- XLAlignmentHorizontalValues.Center

    /// 상태 색상 가져오기
    let getStateColor state =
        match state with
        | "R" -> XLColor.LimeGreen
        | "G" -> XLColor.Orange
        | "F" -> XLColor.DodgerBlue
        | "H" -> XLColor.Gray
        | _ -> XLColor.Gray

    /// 셀에 텍스트 값 설정
    let setText (cell: IXLCell) (text: string) =
        cell.Value <- text

    /// 요약 시트 생성
    let createSummarySheet (wb: XLWorkbook) (report: SimulationReport) =
        let ws = wb.Worksheets.Add("Summary")

        // 메타데이터
        setText (ws.Cell(1, 1)) "시뮬레이션 결과 요약"
        ws.Cell(1, 1).Style.Font.Bold <- true
        ws.Cell(1, 1).Style.Font.FontSize <- 16.0

        setText (ws.Cell(3, 1)) "시작 시간:"
        setText (ws.Cell(3, 2)) (report.Metadata.StartTime.ToString(ReportFormats.DateTimeFormat))
        setText (ws.Cell(4, 1)) "종료 시간:"
        setText (ws.Cell(4, 2)) (report.Metadata.EndTime.ToString(ReportFormats.DateTimeFormat))
        setText (ws.Cell(5, 1)) "총 소요 시간:"
        setText (ws.Cell(5, 2)) (report.Metadata.TotalDuration.ToString(@"hh\:mm\:ss\.fff"))
        setText (ws.Cell(6, 1)) "Work 수:"
        setText (ws.Cell(6, 2)) (string report.Metadata.WorkCount)
        setText (ws.Cell(7, 1)) "Call 수:"
        setText (ws.Cell(7, 2)) (string report.Metadata.CallCount)

        // 요약 테이블
        let startRow = 9
        setText (ws.Cell(startRow, 1)) "No"
        setText (ws.Cell(startRow, 2)) "Type"
        setText (ws.Cell(startRow, 3)) "Name"
        setText (ws.Cell(startRow, 4)) "System"
        setText (ws.Cell(startRow, 5)) "Going Time (sec)"
        setText (ws.Cell(startRow, 6)) "State Changes"
        styleHeader (ws.Range(startRow, 1, startRow, 6))

        for i, entry in report.Entries |> List.indexed do
            let row = startRow + 1 + i
            setText (ws.Cell(row, 1)) (string (i + 1))
            setText (ws.Cell(row, 2)) entry.Type
            setText (ws.Cell(row, 3)) entry.Name
            setText (ws.Cell(row, 4)) entry.SystemId
            setText (ws.Cell(row, 5)) (ReportFormats.fmtFloat (ReportEntry.getTotalGoingTime entry))
            setText (ws.Cell(row, 6)) (string (ReportEntry.getStateChangeCount entry))

            // Work 행 강조
            if entry.Type = "Work" then
                ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor <- XLColor.FromHtml("#FFF3E0")

        ws.Columns().AdjustToContents() |> ignore

    /// 상세 데이터 시트 생성
    let createDetailSheet (wb: XLWorkbook) (report: SimulationReport) =
        let ws = wb.Worksheets.Add("Detail")

        // 헤더
        setText (ws.Cell(1, 1)) "No"
        setText (ws.Cell(1, 2)) "Type"
        setText (ws.Cell(1, 3)) "Name"
        setText (ws.Cell(1, 4)) "System"
        setText (ws.Cell(1, 5)) "State"
        setText (ws.Cell(1, 6)) "State Name"
        setText (ws.Cell(1, 7)) "Start (sec)"
        setText (ws.Cell(1, 8)) "End (sec)"
        setText (ws.Cell(1, 9)) "Duration (sec)"
        styleHeader (ws.Range(1, 1, 1, 9))

        let mutable rowNo = 1
        let mutable row = 2
        for entry in report.Entries do
            for segment in entry.Segments do
                let startSec, endSec, duration = StateSegment.timeRange report.Metadata.StartTime report.Metadata.TotalDuration.TotalSeconds segment

                setText (ws.Cell(row, 1)) (string rowNo)
                setText (ws.Cell(row, 2)) entry.Type
                setText (ws.Cell(row, 3)) entry.Name
                setText (ws.Cell(row, 4)) entry.SystemId
                setText (ws.Cell(row, 5)) segment.State
                ws.Cell(row, 5).Style.Font.FontColor <- getStateColor segment.State
                setText (ws.Cell(row, 6)) segment.StateFullName
                setText (ws.Cell(row, 7)) (ReportFormats.fmtFloat startSec)
                setText (ws.Cell(row, 8)) (ReportFormats.fmtFloat endSec)
                setText (ws.Cell(row, 9)) (ReportFormats.fmtFloat duration)

                rowNo <- rowNo + 1
                row <- row + 1

        ws.Columns().AdjustToContents() |> ignore

    /// 간트차트 시트 생성 (간략한 텍스트 기반)
    let createGanttSheet (wb: XLWorkbook) (report: SimulationReport) =
        let ws = wb.Worksheets.Add("Gantt")
        let totalDuration = max 1.0 report.Metadata.TotalDuration.TotalSeconds

        // 시간 스케일 (1열 = 1초)
        let maxCols = min 100 (int totalDuration + 1)
        let timeScale = totalDuration / float maxCols

        // 헤더 (시간)
        setText (ws.Cell(1, 1)) "Name"
        for col in 1 .. maxCols do
            let sec = float (col - 1) * timeScale
            setText (ws.Cell(1, col + 1)) (sprintf "%.1fs" sec)
        styleHeader (ws.Range(1, 1, 1, maxCols + 1))

        // 각 항목
        for i, entry in report.Entries |> List.indexed do
            let row = i + 2
            let displayName = ReportEntry.getDisplayName entry
            setText (ws.Cell(row, 1)) displayName

            // Work 행 이름 강조
            if entry.Type = "Work" then
                ws.Cell(row, 1).Style.Font.Bold <- true

            // 세그먼트를 셀 색상으로 표시
            for segment in entry.Segments do
                let startSec, endSec, _ = StateSegment.timeRange report.Metadata.StartTime totalDuration segment

                let startCol = int (startSec / timeScale) + 2
                let endCol = int (endSec / timeScale) + 2

                for col in startCol .. min endCol (maxCols + 1) do
                    ws.Cell(row, col).Style.Fill.BackgroundColor <- getStateColor segment.State

        // 열 너비 조정
        ws.Column(1).Width <- 25.0
        for col in 2 .. maxCols + 1 do
            ws.Column(col).Width <- 4.0

    interface IReportExporter with
        member _.SupportedFormat = Excel
        member _.FileExtension = ReportFormats.ExcelExt
        member _.FileFilter = "Excel 파일 (*.xlsx)|*.xlsx"

        member this.Export(report, options) =
            use wb = new XLWorkbook()

            if options.IncludeSummary then
                createSummarySheet wb report

            if options.IncludeDetails then
                createDetailSheet wb report

            if options.IncludeGanttChart then
                createGanttSheet wb report

            wb.SaveAs(options.FilePath)

        member this.ExportAsync(report, options) =
            Task.Run(fun () -> (this :> IReportExporter).Export(report, options))
