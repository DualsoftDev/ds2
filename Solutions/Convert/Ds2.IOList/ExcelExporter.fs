namespace Ds2.IOList

open System.IO
open ClosedXML.Excel

// =============================================================================
// Excel Exporter — ClosedXML 기반 IO/Dummy/Summary 3시트
// =============================================================================

module ExcelExporter =

    // ─────────────────────────────────────────────────────────────────────
    // Color Constants
    // ─────────────────────────────────────────────────────────────────────

    let private headerColor   = XLColor.FromHtml("#C6EFCE")  // 연녹색
    let private iwRowColor    = XLColor.FromHtml("#E2EFDA")   // 연청록
    let private qwRowColor    = XLColor.FromHtml("#FFF2CC")   // 연황색
    let private mwRowColor    = XLColor.FromHtml("#F2F2F2")   // 연회색

    // ─────────────────────────────────────────────────────────────────────
    // Sheet Helpers
    // ─────────────────────────────────────────────────────────────────────

    let private setHeaderStyle (ws: IXLWorksheet) (colCount: int) =
        let headerRange = ws.Range(1, 1, 1, colCount)
        headerRange.Style.Font.Bold <- true
        headerRange.Style.Fill.BackgroundColor <- headerColor
        headerRange.Style.Border.BottomBorder <- XLBorderStyleValues.Thin
        headerRange.Style.Alignment.Horizontal <- XLAlignmentHorizontalValues.Center

    let private applyRowColor (ws: IXLWorksheet) (row: int) (colCount: int) (ioType: string) =
        let color =
            match ioType.ToUpperInvariant() with
            | "IW" -> Some iwRowColor
            | "QW" -> Some qwRowColor
            | "MW" -> Some mwRowColor
            | _    -> None
        match color with
        | Some c ->
            let range = ws.Range(row, 1, row, colCount)
            range.Style.Fill.BackgroundColor <- c
        | None -> ()

    let private finishSheet (ws: IXLWorksheet) (colCount: int) (dataRowCount: int) =
        // AutoFilter on header + data
        if dataRowCount > 0 then
            ws.Range(1, 1, 1 + dataRowCount, colCount).SetAutoFilter() |> ignore

        // Freeze header row
        ws.SheetView.FreezeRows(1)

        // AutoFit columns
        ws.Columns().AdjustToContents() |> ignore

    // ─────────────────────────────────────────────────────────────────────
    // IO Sheet
    // ─────────────────────────────────────────────────────────────────────

    let private ioHeaders = [| "No"; "VarName"; "DataType"; "Address"; "IoType"; "Direction"; "FlowName"; "WorkName"; "CallName"; "DeviceName"; "Comment" |]

    let private writeIoSheet (wb: XLWorkbook) (signals: SignalRecord list) =
        let ws = wb.Worksheets.Add("IO")
        let colCount = ioHeaders.Length

        // Header
        for i in 0 .. colCount - 1 do
            ws.Cell(1, i + 1).SetValue(ioHeaders.[i]) |> ignore
        setHeaderStyle ws colCount

        // Data
        let sorted = ExportHelpers.sortSignals signals
        let mutable row = 2
        for s in sorted do
            let comment = s.Comment |> Option.defaultValue ""
            let direction = ExportHelpers.directionOf s.IoType
            ws.Cell(row, 1).SetValue(float (row - 1)) |> ignore  // No
            ws.Cell(row, 2).SetValue(s.VarName)  |> ignore
            ws.Cell(row, 3).SetValue(s.DataType)  |> ignore
            ws.Cell(row, 4).SetValue(s.Address)   |> ignore
            ws.Cell(row, 5).SetValue(s.IoType)    |> ignore
            ws.Cell(row, 6).SetValue(direction)   |> ignore
            ws.Cell(row, 7).SetValue(s.FlowName)  |> ignore
            ws.Cell(row, 8).SetValue(s.WorkName)  |> ignore
            ws.Cell(row, 9).SetValue(s.CallName)  |> ignore
            ws.Cell(row, 10).SetValue(s.DeviceName) |> ignore
            ws.Cell(row, 11).SetValue(comment)    |> ignore
            applyRowColor ws row colCount s.IoType
            row <- row + 1

        finishSheet ws colCount (sorted.Length)

    // ─────────────────────────────────────────────────────────────────────
    // Dummy Sheet
    // ─────────────────────────────────────────────────────────────────────

    let private dummyHeaders = [| "No"; "VarName"; "DataType"; "Address"; "IoType"; "FlowName"; "WorkName"; "CallName"; "Comment" |]

    let private writeDummySheet (wb: XLWorkbook) (signals: SignalRecord list) =
        let ws = wb.Worksheets.Add("Dummy")
        let colCount = dummyHeaders.Length

        // Header
        for i in 0 .. colCount - 1 do
            ws.Cell(1, i + 1).SetValue(dummyHeaders.[i]) |> ignore
        setHeaderStyle ws colCount

        // Data
        let sorted = ExportHelpers.sortSignals signals
        let mutable row = 2
        for s in sorted do
            let comment = s.Comment |> Option.defaultValue ""
            ws.Cell(row, 1).SetValue(float (row - 1)) |> ignore
            ws.Cell(row, 2).SetValue(s.VarName)  |> ignore
            ws.Cell(row, 3).SetValue(s.DataType)  |> ignore
            ws.Cell(row, 4).SetValue(s.Address)   |> ignore
            ws.Cell(row, 5).SetValue(s.IoType)    |> ignore
            ws.Cell(row, 6).SetValue(s.FlowName)  |> ignore
            ws.Cell(row, 7).SetValue(s.WorkName)  |> ignore
            ws.Cell(row, 8).SetValue(s.CallName)  |> ignore
            ws.Cell(row, 9).SetValue(comment)     |> ignore
            applyRowColor ws row colCount s.IoType
            row <- row + 1

        finishSheet ws colCount (sorted.Length)

    // ─────────────────────────────────────────────────────────────────────
    // Summary Sheet
    // ─────────────────────────────────────────────────────────────────────

    let private writeSummarySheet (wb: XLWorkbook) (result: GenerationResult) =
        let ws = wb.Worksheets.Add("Summary")

        // Title
        let titleRange = ws.Range("A1:D1")
        titleRange.Merge() |> ignore
        ws.Cell("A1").SetValue("IO List Summary") |> ignore
        ws.Cell("A1").Style.Font.Bold <- true
        ws.Cell("A1").Style.Font.FontSize <- 16.0

        // Stats
        ws.Cell("A3").SetValue("IO 신호 수:") |> ignore
        ws.Cell("B3").SetValue(float result.IoSignals.Length) |> ignore
        ws.Cell("A4").SetValue("Dummy 신호 수:") |> ignore
        ws.Cell("B4").SetValue(float result.DummySignals.Length) |> ignore
        ws.Cell("A5").SetValue("에러 수:") |> ignore
        ws.Cell("B5").SetValue(float result.Errors.Length) |> ignore
        ws.Cell("A6").SetValue("경고 수:") |> ignore
        ws.Cell("B6").SetValue(float result.Warnings.Length) |> ignore

        // Label column bold
        for r in 3..6 do
            ws.Cell(r, 1).Style.Font.Bold <- true

        // Warnings
        if not result.Warnings.IsEmpty then
            ws.Cell("A8").SetValue("Warnings") |> ignore
            ws.Cell("A8").Style.Font.Bold <- true

            let mutable row = 9
            for w in result.Warnings do
                ws.Cell(row, 1).SetValue(w) |> ignore
                row <- row + 1

        ws.Columns().AdjustToContents() |> ignore

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    /// Export GenerationResult to a single .xlsx file with IO/Dummy/Summary sheets
    let exportToExcel (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        try
            let dir = Path.GetDirectoryName(outputPath)
            if not (System.String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            use wb = new XLWorkbook()

            writeIoSheet wb result.IoSignals
            writeDummySheet wb result.DummySignals
            writeSummarySheet wb result

            wb.SaveAs(outputPath)
            Ok ()
        with ex ->
            Error $"Excel export 실패 '{outputPath}': {ex.Message}"

    /// Export GenerationResult using a template Excel file
    let exportToExcelWithTemplate (result: GenerationResult) (outputPath: string) (templatePath: string option) : Result<unit, string> =
        try
            let dir = Path.GetDirectoryName(outputPath)
            if not (System.String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            // Load template or create new workbook
            use wb =
                match templatePath with
                | Some path when File.Exists(path) ->
                    new XLWorkbook(path)
                | Some path ->
                    Error $"템플릿 파일을 찾을 수 없습니다: {path}" |> ignore
                    new XLWorkbook()
                | None ->
                    new XLWorkbook()

            // Write or overwrite sheets
            // Remove existing sheets if they exist to avoid conflicts
            let removeSheetIfExists name =
                if wb.Worksheets.Contains(name) then
                    wb.Worksheet(name).Delete()

            removeSheetIfExists "IO List"
            removeSheetIfExists "Dummy List"
            removeSheetIfExists "Summary"

            // Write data
            writeIoSheet wb result.IoSignals
            writeDummySheet wb result.DummySignals
            writeSummarySheet wb result

            wb.SaveAs(outputPath)
            Ok ()
        with ex ->
            Error $"Excel export 실패 '{outputPath}': {ex.Message}"
