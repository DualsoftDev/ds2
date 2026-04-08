namespace Ds2.IOList

open System.IO
open System.Text

// =============================================================================
// CSV Exporter — Legacy (6/5 col) + Extended (11/9 col)
// =============================================================================

module CsvExporter =

    let private utf8Bom = UTF8Encoding(true)

    /// Write header + rows to CSV with UTF-8 BOM + CRLF
    let private writeCsv (outputPath: string) (header: string) (formatRow: SignalRecord -> string) (signals: SignalRecord list) : Result<unit, string> =
        try
            let dir = Path.GetDirectoryName(outputPath)
            if not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            use writer = new StreamWriter(outputPath, false, utf8Bom)
            writer.NewLine <- "\r\n"
            writer.WriteLine(header)

            for signal in signals do
                writer.WriteLine(formatRow signal)

            Ok ()
        with ex ->
            Error $"CSV export 실패 '{outputPath}': {ex.Message}"

    // ─────────────────────────────────────────────────────────────────────
    // Legacy Format (backward-compatible with existing Pipeline.exportToCsv)
    // ─────────────────────────────────────────────────────────────────────

    let private formatIoLegacyRow (s: SignalRecord) =
        let comment = s.Comment |> Option.defaultValue ""
        $"{s.VarName},{s.DataType},{s.Address},{s.IoType},{s.Category},{comment}"

    let private formatDummyLegacyRow (s: SignalRecord) =
        let comment = s.Comment |> Option.defaultValue ""
        $"{s.VarName},{s.DataType},{s.Address},{s.Category},{comment}"

    /// IO CSV — Legacy 6 columns: var_name,data_type,address,io_type,category,comment
    let exportIoLegacy (signals: SignalRecord list) (outputPath: string) : Result<unit, string> =
        let header = "var_name,data_type,address,io_type,category,comment"
        writeCsv outputPath header formatIoLegacyRow signals

    /// Dummy CSV — Legacy 5 columns: var_name,data_type,address,category,comment
    let exportDummyLegacy (signals: SignalRecord list) (outputPath: string) : Result<unit, string> =
        let header = "var_name,data_type,address,category,comment"
        writeCsv outputPath header formatDummyLegacyRow signals

    // ─────────────────────────────────────────────────────────────────────
    // Extended Format (with DS2 model tracing columns)
    // ─────────────────────────────────────────────────────────────────────

    let private esc = ExportHelpers.escapeField

    let private formatIoExtendedRow (s: SignalRecord) =
        let comment = s.Comment |> Option.defaultValue ""
        let direction = ExportHelpers.directionOf s.IoType
        [ s.VarName; s.DataType; s.Address; s.IoType; s.Category
          s.FlowName; s.WorkName; s.CallName; s.DeviceName
          direction; comment ]
        |> List.map esc
        |> String.concat ","

    let private formatDummyExtendedRow (s: SignalRecord) =
        let comment = s.Comment |> Option.defaultValue ""
        [ s.VarName; s.DataType; s.Address; s.IoType; s.Category
          s.FlowName; s.WorkName; s.CallName
          comment ]
        |> List.map esc
        |> String.concat ","

    /// IO CSV — Extended 11 columns (sorted by IoType → Word → Bit)
    let exportIoExtended (signals: SignalRecord list) (outputPath: string) : Result<unit, string> =
        let header = "var_name,data_type,address,io_type,category,flow_name,work_name,call_name,device_name,direction,comment"
        let sorted = ExportHelpers.sortSignals signals
        writeCsv outputPath header formatIoExtendedRow sorted

    /// Dummy CSV — Extended 9 columns (sorted by IoType → Word → Bit)
    let exportDummyExtended (signals: SignalRecord list) (outputPath: string) : Result<unit, string> =
        let header = "var_name,data_type,address,io_type,category,flow_name,work_name,call_name,comment"
        let sorted = ExportHelpers.sortSignals signals
        writeCsv outputPath header formatDummyExtendedRow sorted
