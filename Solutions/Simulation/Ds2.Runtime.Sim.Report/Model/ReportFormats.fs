namespace Ds2.Runtime.Sim.Report.Model

/// 리포트 공통 포맷/확장자 상수
[<RequireQualifiedAccess>]
module ReportFormats =

    // 파일 확장자
    let [<Literal>] CsvExt   = ".csv"
    let [<Literal>] HtmlExt  = ".html"
    let [<Literal>] ExcelExt = ".xlsx"

    // 날짜/시간 포맷
    let [<Literal>] DateTimeFormat = "yyyy-MM-dd HH:mm:ss"

    // 소수 포맷 (printf 스타일)
    let [<Literal>] FloatFmt = "%.2f"

    /// sprintf FloatFmt value 단축 헬퍼
    let fmtFloat (v: float) = sprintf FloatFmt v
