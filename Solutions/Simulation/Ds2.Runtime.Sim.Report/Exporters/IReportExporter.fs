namespace Ds2.Runtime.Sim.Report.Exporters

open System.Threading.Tasks
open Ds2.Runtime.Sim.Report.Model

/// 리포트 내보내기 인터페이스
type IReportExporter =
    /// 지원하는 형식
    abstract member SupportedFormat: ExportFormat

    /// 파일 확장자
    abstract member FileExtension: string

    /// 파일 필터 (SaveFileDialog용)
    abstract member FileFilter: string

    /// 동기 내보내기
    abstract member Export: report: SimulationReport * options: ExportOptions -> unit

    /// 비동기 내보내기
    abstract member ExportAsync: report: SimulationReport * options: ExportOptions -> Task

/// 내보내기 결과
type ExportResult =
    | Success of filePath: string
    | Error of message: string

/// 내보내기 헬퍼
module ExportHelper =
    /// 파일 확장자 가져오기
    let getExtension format =
        match format with
        | Csv -> ReportFormats.CsvExt
        | CsvSummary -> ReportFormats.CsvExt
        | Html -> ReportFormats.HtmlExt
        | Excel -> ReportFormats.ExcelExt

    /// 파일 필터 가져오기
    let getFilter format =
        match format with
        | Csv -> "CSV 파일 (*.csv)|*.csv"
        | CsvSummary -> "요약 CSV 파일 (*.csv)|*.csv"
        | Html -> "HTML 파일 (*.html)|*.html"
        | Excel -> "Excel 파일 (*.xlsx)|*.xlsx"

    /// 모든 형식의 파일 필터
    let getAllFilters () =
        "HTML 파일 (*.html)|*.html|Excel 파일 (*.xlsx)|*.xlsx|CSV 파일 (*.csv)|*.csv|요약 CSV 파일 (*.csv)|*.csv"
