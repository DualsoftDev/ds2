namespace Ds2.Runtime.Sim.Report.Exporters

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Ds2.Runtime.Sim.Report.Model

/// CSV 값 이스케이프
module private CsvHelper =
    let escape (value: string) =
        if String.IsNullOrEmpty(value) then ""
        elif value.Contains(",") || value.Contains("\"") || value.Contains("\n") then
            sprintf "\"%s\"" (value.Replace("\"", "\"\""))
        else value

/// 상세 CSV 내보내기
type CsvExporter() =
    interface IReportExporter with
        member _.SupportedFormat = Csv
        member _.FileExtension = ReportFormats.CsvExt
        member _.FileFilter = "CSV 파일 (*.csv)|*.csv"

        member this.Export(report, options) =
            let sb = StringBuilder()

            // 헤더
            sb.AppendLine("No,Type,Name,SystemId,State,StateFullName,StartTime(sec),EndTime(sec),Duration(sec)") |> ignore

            let mutable rowNo = 1
            for entry in report.Entries do
                for segment in entry.Segments do
                    let startSec, endSec, duration = StateSegment.timeRange report.Metadata.StartTime report.Metadata.TotalDuration.TotalSeconds segment

                    sb.AppendLine(sprintf "%d,%s,%s,%s,%s,%s,%.2f,%.2f,%.2f"
                        rowNo
                        entry.Type
                        (CsvHelper.escape entry.Name)
                        (CsvHelper.escape entry.SystemId)
                        segment.State
                        segment.StateFullName
                        startSec
                        endSec
                        duration) |> ignore
                    rowNo <- rowNo + 1

            File.WriteAllText(options.FilePath, sb.ToString(), Encoding.UTF8)

        member this.ExportAsync(report, options) =
            Task.Run(fun () -> (this :> IReportExporter).Export(report, options))

/// 요약 CSV 내보내기
type CsvSummaryExporter() =
    interface IReportExporter with
        member _.SupportedFormat = CsvSummary
        member _.FileExtension = ReportFormats.CsvExt
        member _.FileFilter = "요약 CSV 파일 (*.csv)|*.csv"

        member this.Export(report, options) =
            let sb = StringBuilder()

            // 헤더
            sb.AppendLine("No,Type,Name,SystemId,TotalGoingTime(sec),StateChanges") |> ignore

            for i, entry in report.Entries |> List.indexed do
                let goingTime = ReportEntry.getTotalGoingTime entry
                let stateChanges = ReportEntry.getStateChangeCount entry

                sb.AppendLine(sprintf "%d,%s,%s,%s,%.2f,%d"
                    (i + 1)
                    entry.Type
                    (CsvHelper.escape entry.Name)
                    (CsvHelper.escape entry.SystemId)
                    goingTime
                    stateChanges) |> ignore

            File.WriteAllText(options.FilePath, sb.ToString(), Encoding.UTF8)

        member this.ExportAsync(report, options) =
            Task.Run(fun () -> (this :> IReportExporter).Export(report, options))
