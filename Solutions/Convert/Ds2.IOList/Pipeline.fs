namespace Ds2.IOList

open System
open System.IO
open Ds2.Core.Store

// =============================================================================
// Public API - IoList Generation Pipeline
// =============================================================================

module Pipeline =

    /// Generate IO and Dummy lists from DS2 store and templates
    let generate (store: DsStore) (templateDir: string) : GenerationResult =
        // Step 1: Load templates
        match TemplateParser.loadFromDirectory templateDir with
        | Error errors ->
            {
                IoSignals = []
                DummySignals = []
                Errors = errors
                Warnings = []
            }
        | Ok templates ->
            // Step 2: Load address config
            let addressConfig =
                match AddressConfigParser.loadFromDirectory templateDir with
                | Ok config -> config
                | Error msg ->
                    printfn "Warning: Failed to load address config: %s (using template defaults)" msg
                    AddressConfig.empty

            // Step 3: Build generation contexts from all ApiCalls
            let contexts, contextErrors = ContextBuilder.buildAllContexts store

            // Step 4: Generate signals with new allocation strategy
            let generationResult = SignalGenerator.generateAll contexts templates addressConfig

            // Step 5: Combine context errors with generation errors
            {
                generationResult with
                    Errors = contextErrors @ generationResult.Errors
            }

    // ─────────────────────────────────────────────────────────────────────
    // Legacy CSV (backward-compatible)
    // ─────────────────────────────────────────────────────────────────────

    /// Export signals to CSV file (legacy, kept for backward compat)
    let exportToCsv (signals: SignalRecord list) (outputPath: string) (_header: string) : Result<unit, string> =
        CsvExporter.exportIoLegacy signals outputPath

    /// Export IO list to CSV (legacy 6 columns)
    let exportIoList (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        CsvExporter.exportIoLegacy result.IoSignals outputPath

    /// Export Dummy list to CSV (legacy 5 columns)
    let exportDummyList (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        CsvExporter.exportDummyLegacy result.DummySignals outputPath

    // ─────────────────────────────────────────────────────────────────────
    // Extended CSV
    // ─────────────────────────────────────────────────────────────────────

    /// Export IO list to CSV (extended 11 columns, sorted)
    let exportIoListExtended (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        CsvExporter.exportIoExtended result.IoSignals outputPath

    /// Export Dummy list to CSV (extended 9 columns, sorted)
    let exportDummyListExtended (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        CsvExporter.exportDummyExtended result.DummySignals outputPath

    // ─────────────────────────────────────────────────────────────────────
    // Excel
    // ─────────────────────────────────────────────────────────────────────

    /// Export to Excel (.xlsx) with IO/Dummy/Summary sheets
    let exportToExcel (result: GenerationResult) (outputPath: string) : Result<unit, string> =
        ExcelExporter.exportToExcel result outputPath

    /// Export to Excel with optional template
    let exportToExcelWithTemplate (result: GenerationResult) (outputPath: string) (templatePath: string option) : Result<unit, string> =
        ExcelExporter.exportToExcelWithTemplate result outputPath templatePath

    // ─────────────────────────────────────────────────────────────────────
    // Unified Export
    // ─────────────────────────────────────────────────────────────────────

    /// Export with options, returns list of generated file paths
    let export (result: GenerationResult) (options: ExportOptions) : Result<string list, string> =
        let stem = if String.IsNullOrWhiteSpace(options.FileStem) then "iolist" else options.FileStem
        let dir = options.OutputDirectory

        if not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore

        let checkOverwrite path =
            if (not options.Overwrite) && File.Exists(path) then
                Error $"파일이 이미 존재합니다: {path}"
            else
                Ok ()

        match options.Format with
        | CsvLegacy ->
            let ioPath = Path.Combine(dir, $"{stem}_io.csv")
            let dummyPath = Path.Combine(dir, $"{stem}_dummy.csv")
            match checkOverwrite ioPath, checkOverwrite dummyPath with
            | Error e, _ | _, Error e -> Error e
            | Ok (), Ok () ->
                match CsvExporter.exportIoLegacy result.IoSignals ioPath with
                | Error e -> Error e
                | Ok () ->
                    match CsvExporter.exportDummyLegacy result.DummySignals dummyPath with
                    | Error e -> Error e
                    | Ok () -> Ok [ ioPath; dummyPath ]

        | CsvExtended ->
            let ioPath = Path.Combine(dir, $"{stem}_io_ext.csv")
            let dummyPath = Path.Combine(dir, $"{stem}_dummy_ext.csv")
            match checkOverwrite ioPath, checkOverwrite dummyPath with
            | Error e, _ | _, Error e -> Error e
            | Ok (), Ok () ->
                match CsvExporter.exportIoExtended result.IoSignals ioPath with
                | Error e -> Error e
                | Ok () ->
                    match CsvExporter.exportDummyExtended result.DummySignals dummyPath with
                    | Error e -> Error e
                    | Ok () -> Ok [ ioPath; dummyPath ]

        | Excel ->
            let xlsxPath = Path.Combine(dir, $"{stem}_iolist.xlsx")
            match checkOverwrite xlsxPath with
            | Error e -> Error e
            | Ok () ->
                match ExcelExporter.exportToExcel result xlsxPath with
                | Error e -> Error e
                | Ok () -> Ok [ xlsxPath ]

    // ─────────────────────────────────────────────────────────────────────
    // Generate + Export
    // ─────────────────────────────────────────────────────────────────────

    /// Generate and export in one step (legacy)
    let generateAndExport
        (store: DsStore)
        (templateDir: string)
        (ioOutputPath: string)
        (dummyOutputPath: string)
        : Result<GenerationResult, string> =

        let result = generate store templateDir

        if not result.Errors.IsEmpty then
            let errorMessages =
                result.Errors
                |> List.map (fun e -> e.Message)
                |> String.concat "\n"
            Error $"Generation failed with {result.Errors.Length} errors:\n{errorMessages}"
        else
            match exportIoList result ioOutputPath with
            | Error msg -> Error msg
            | Ok () ->
                match exportDummyList result dummyOutputPath with
                | Error msg -> Error msg
                | Ok () -> Ok result

    /// Print generation result summary
    let printSummary (result: GenerationResult) =
        printfn "=== Generation Summary ==="
        printfn $"IO Signals:    {result.IoSignals.Length}"
        printfn $"Dummy Signals: {result.DummySignals.Length}"
        printfn $"Errors:        {result.Errors.Length}"
        printfn $"Warnings:      {result.Warnings.Length}"
        printfn ""

        if not result.Errors.IsEmpty then
            printfn "=== Errors ==="
            for error in result.Errors do
                match error.ApiCallId with
                | Some id -> printfn $"[ApiCall {id}] {error.Message}"
                | None -> printfn $"{error.Message}"
            printfn ""

        if not result.Warnings.IsEmpty then
            printfn "=== Warnings ==="
            for warning in result.Warnings do
                printfn $"{warning}"
            printfn ""

// =============================================================================
// ProMaker Integration API
// =============================================================================

/// Public API for ProMaker UI integration
type IoListGeneratorApi() =

    /// Generate IO and Dummy lists
    member _.Generate(store: DsStore, templateDir: string) : GenerationResult =
        Pipeline.generate store templateDir

    /// Generate and export to CSV files (legacy)
    member _.GenerateAndExport
        (store: DsStore,
         templateDir: string,
         ioOutputPath: string,
         dummyOutputPath: string)
        : Result<GenerationResult, string> =
        Pipeline.generateAndExport store templateDir ioOutputPath dummyOutputPath

    /// Export IO list to CSV (legacy 6 columns)
    member _.ExportIoList(result: GenerationResult, outputPath: string) : Result<unit, string> =
        Pipeline.exportIoList result outputPath

    /// Export Dummy list to CSV (legacy 5 columns)
    member _.ExportDummyList(result: GenerationResult, outputPath: string) : Result<unit, string> =
        Pipeline.exportDummyList result outputPath

    /// Export IO list to CSV (extended 11 columns)
    member _.ExportIoListExtended(result: GenerationResult, outputPath: string) : Result<unit, string> =
        Pipeline.exportIoListExtended result outputPath

    /// Export Dummy list to CSV (extended 9 columns)
    member _.ExportDummyListExtended(result: GenerationResult, outputPath: string) : Result<unit, string> =
        Pipeline.exportDummyListExtended result outputPath

    /// Export to Excel (.xlsx)
    member _.ExportToExcel(result: GenerationResult, outputPath: string) : Result<unit, string> =
        Pipeline.exportToExcel result outputPath

    /// Unified export with options
    member _.Export(result: GenerationResult, options: ExportOptions) : Result<string list, string> =
        Pipeline.export result options

    /// Get error summary
    member _.GetErrorSummary(result: GenerationResult) : string =
        if result.Errors.IsEmpty then
            "No errors"
        else
            result.Errors
            |> List.map (fun e -> e.Message)
            |> String.concat "\n"

    /// Check if generation was successful
    member _.IsSuccess(result: GenerationResult) : bool =
        result.Errors.IsEmpty
