namespace Ds2.IOList

open System
open System.IO
open System.Text.RegularExpressions

// =============================================================================
// Template Parser
// =============================================================================

module TemplateParser =

    /// Parse state
    type private ParseState = {
        InputSlots: TemplateSlot list
        OutputSlots: TemplateSlot list
        MemorySlots: TemplateSlot list
        CurrentSection: SectionType option
    }

    and private SectionType =
        | InputSection
        | OutputSection
        | MemorySection

    let private emptyState = {
        InputSlots = []
        OutputSlots = []
        MemorySlots = []
        CurrentSection = None
    }

    /// Parse a directive line (now only used for unknown directives - ignored)
    let private parseDirective (_line: string) (state: ParseState) =
        // All directives like @META, @CATEGORY, @IW_BASE are now ignored
        // SystemType comes from filename instead
        state

    /// Parse a section header ([IW], [QW], [MW] or legacy [RBT.IW] format)
    let private parseSectionHeader (line: string) (state: ParseState) =
        // New simplified format: [IW], [QW], [MW]
        let simplePattern = @"\[(IW|QW|MW)\]"
        let simpleMatch = Regex.Match(line, simplePattern)

        if simpleMatch.Success then
            let sectionType =
                match simpleMatch.Groups.[1].Value with
                | "IW" -> Some InputSection
                | "QW" -> Some OutputSection
                | "MW" -> Some MemorySection
                | _ -> None
            { state with CurrentSection = sectionType }
        else
            // Legacy format: [RBT.IW], [PIN.QW], etc.
            let legacyPattern = @"\[([^\.]+)\.(IW|QW|MW)\]"
            let legacyMatch = Regex.Match(line, legacyPattern)
            if legacyMatch.Success then
                let sectionType =
                    match legacyMatch.Groups.[2].Value with
                    | "IW" -> Some InputSection
                    | "QW" -> Some OutputSection
                    | "MW" -> Some MemorySection
                    | _ -> None
                { state with CurrentSection = sectionType }
            else
                state

    /// Parse a signal line (e.g., "HOME_POS: W_$(F)_I_$(D)_HOME_POS" or "-")
    let private parseSignalLine (line: string) (state: ParseState) =
        match state.CurrentSection with
        | None -> state
        | Some section ->
            let slot =
                if line.Trim() = "-" then
                    Empty
                else
                    // Format: "API_NAME: PATTERN" or just "PATTERN"
                    let parts = line.Split([|':'|], 2)
                    if parts.Length = 2 then
                        let apiName = parts.[0].Trim()
                        let pattern = parts.[1].Trim()
                        Signal(apiName, pattern)
                    else
                        // No colon - extract API name from pattern
                        // Pattern like "W_$(F)_I_$(D)_HOME_POS" → API name = "HOME_POS"
                        let pattern = line.Trim()
                        let lastPart =
                            pattern.Split('_')
                            |> Array.rev
                            |> Array.tryHead
                            |> Option.defaultValue pattern
                        Signal(lastPart, pattern)

            match section with
            | InputSection ->
                { state with InputSlots = state.InputSlots @ [slot] }
            | OutputSection ->
                { state with OutputSlots = state.OutputSlots @ [slot] }
            | MemorySection ->
                { state with MemorySlots = state.MemorySlots @ [slot] }

    /// Parse a single line
    let private parseLine (line: string) (state: ParseState) =
        let trimmed = line.Trim()

        // Skip empty lines and comments
        if String.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//") then
            state
        // Directive
        elif trimmed.StartsWith("@") then
            parseDirective trimmed state
        // Section header
        elif trimmed.StartsWith("[") && trimmed.EndsWith("]") then
            parseSectionHeader trimmed state
        // Signal line
        else
            parseSignalLine trimmed state

    /// Build MacroTemplate from parse state (SystemType from filename)
    let private buildTemplate (filePath: string) (state: ParseState) : Result<MacroTemplate, GenerationError> =
        // Extract SystemType from filename (e.g., "RBT.txt" -> "RBT")
        let fileName = Path.GetFileNameWithoutExtension(filePath)
        let systemType = fileName

        Ok {
            SystemType = systemType
            Category = systemType  // Category = SystemType (same as filename)
            IW_BaseAddress = 0  // No longer used - will come from address_config.txt
            QW_BaseAddress = 0
            MW_BaseAddress = 0
            InputSlots = state.InputSlots
            OutputSlots = state.OutputSlots
            MemorySlots = state.MemorySlots
        }

    /// Parse a template file
    let parseFile (filePath: string) : Result<MacroTemplate, GenerationError> =
        try
            if not (File.Exists(filePath)) then
                Error {
                    ApiCallId = None
                    Message = $"Template file not found: {filePath}"
                    ErrorType = InvalidTemplateFormat(filePath, "File not found")
                }
            else
                let lines = File.ReadAllLines(filePath)
                let finalState = lines |> Array.fold (fun state line -> parseLine line state) emptyState
                buildTemplate filePath finalState
        with ex ->
            Error {
                ApiCallId = None
                Message = $"Failed to parse template file '{filePath}': {ex.Message}"
                ErrorType = InvalidTemplateFormat(filePath, ex.Message)
            }

    /// Load all templates from a directory
    let loadFromDirectory (templateDir: string) : Result<Map<string, MacroTemplate>, GenerationError list> =
        try
            if not (Directory.Exists(templateDir)) then
                Error [{
                    ApiCallId = None
                    Message = $"Template directory not found: {templateDir}"
                    ErrorType = InvalidTemplateFormat(templateDir, "Directory not found")
                }]
            else
                let files =
                    Directory.GetFiles(templateDir, "*.txt")
                    |> Array.filter (fun f ->
                        let fileName = Path.GetFileName(f).ToLowerInvariant()
                        fileName <> "flow.txt" && fileName <> "address_config.txt")  // Exclude config files

                let results = files |> Array.map parseFile |> Array.toList

                let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
                let templates = results |> List.choose (function Ok t -> Some t | Error _ -> None)

                if not errors.IsEmpty then
                    Error errors
                else
                    let templateMap =
                        templates
                        |> List.map (fun t -> t.SystemType.ToUpperInvariant(), t)
                        |> Map.ofList
                    Ok templateMap
        with ex ->
            Error [{
                ApiCallId = None
                Message = $"Failed to load templates from '{templateDir}': {ex.Message}"
                ErrorType = InvalidTemplateFormat(templateDir, ex.Message)
            }]
