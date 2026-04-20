namespace Ds2.IOList

open System

// =============================================================================
// Export Types & Helpers (CSV only - no Excel)
// =============================================================================

type ExportFormat =
    | CsvLegacy
    | CsvExtended

type ExportOptions = {
    Format: ExportFormat
    OutputDirectory: string
    FileStem: string
    Overwrite: bool
}

module ExportOptions =
    let defaults dir = {
        Format = CsvLegacy
        OutputDirectory = dir
        FileStem = "iolist"
        Overwrite = true
    }

// =============================================================================
// Export Helpers
// =============================================================================

module ExportHelpers =

    /// IoType → Direction string
    let directionOf (ioType: string) =
        match ioType.ToUpperInvariant() with
        | "IW" -> "Input"
        | "QW" -> "Output"
        | "MW" -> "Memory"
        | _    -> "Unknown"

    /// IoType → sort priority (IW=0, QW=1, MW=2)
    let ioTypePriority (ioType: string) =
        match ioType.ToUpperInvariant() with
        | "IW"  -> 0
        | "QW"  -> 1
        | "MW"  -> 2
        | _     -> 99

    /// Parse address "%IW3070.5" → (word=3070, bit=5)
    let parseAddress (address: string) : int * int =
        // Format: %PREFIX<word>.<bit>
        let s = address.TrimStart('%')
        let dotIdx = s.IndexOf('.')
        if dotIdx < 0 then
            // No bit part, try parse the digits after prefix
            let digits = s |> Seq.skipWhile (fun c -> not (Char.IsDigit c)) |> System.String.Concat
            let word = match Int32.TryParse(digits) with true, v -> v | _ -> 0
            (word, 0)
        else
            let beforeDot = s.[..dotIdx - 1]
            let afterDot = s.[dotIdx + 1..]
            let wordDigits = beforeDot |> Seq.skipWhile (fun c -> not (Char.IsDigit c)) |> System.String.Concat
            let word = match Int32.TryParse(wordDigits) with true, v -> v | _ -> 0
            let bit = match Int32.TryParse(afterDot) with true, v -> v | _ -> 0
            (word, bit)

    /// Sort signals by IoType priority → Word → Bit
    let sortSignals (signals: SignalRecord list) : SignalRecord list =
        signals
        |> List.sortBy (fun s ->
            let priority = ioTypePriority s.IoType
            let word, bit = parseAddress s.Address
            (priority, word, bit))

    /// RFC 4180 CSV field escaping
    let escapeField (value: string) : string =
        if String.IsNullOrEmpty(value) then ""
        elif value.IndexOfAny([| ','; '"'; '\n'; '\r' |]) >= 0 then
            "\"" + value.Replace("\"", "\"\"") + "\""
        else
            value
