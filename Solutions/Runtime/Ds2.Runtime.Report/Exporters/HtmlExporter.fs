namespace Ds2.Runtime.Report.Exporters

open System.IO
open System.Text
open System.Threading.Tasks
open Ds2.Runtime.Report.Model
open Ds2.Runtime.Report.Templates

/// HTML 내보내기
type HtmlExporter() =
    interface IReportExporter with
        member _.SupportedFormat = Html
        member _.FileExtension = ReportFormats.HtmlExt
        member _.FileFilter = "HTML 파일 (*.html)|*.html"

        member this.Export(report, options) =
            let html = HtmlTemplate.generate report options
            File.WriteAllText(options.FilePath, html, Encoding.UTF8)

        member this.ExportAsync(report, options) =
            Task.Run(fun () -> (this :> IReportExporter).Export(report, options))
