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
        SystemType: string option
        Category: string option
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
        SystemType = None
        Category = None
        InputSlots = []
        OutputSlots = []
        MemorySlots = []
        CurrentSection = None
    }

    /// Parse a directive line (@META, @CATEGORY)
    let private parseDirective (line: string) (state: ParseState) =
        let parts = line.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length < 2 then state
        else
            match parts.[0].ToUpperInvariant() with
            | "@META" -> { state with SystemType = Some parts.[1] }
            | "@CATEGORY" -> { state with Category = Some parts.[1] }
            | _ -> state  // Ignore all other directives (like @IW_BASE)

    /// Parse a section header ([RBT.IW], [RBT.QW], [RBT.MW])
    let private parseSectionHeader (line: string) (state: ParseState) =
        let pattern = @"\[([^\.]+)\.(IW|QW|MW)\]"
        let m = Regex.Match(line, pattern)
        if m.Success then
            let sectionType =
                match m.Groups.[2].Value with
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

    /// Build MacroTemplate from parse state
    let private buildTemplate (filePath: string) (state: ParseState) : Result<MacroTemplate, GenerationError> =
        match state.SystemType with
        | None ->
            Error {
                ApiCallId = None
                Message = $"Template file '{filePath}' missing @META directive"
                ErrorType = InvalidTemplateFormat(filePath, "Missing @META directive")
            }
        | Some systemType ->
            Ok {
                SystemType = systemType
                Category = state.Category |> Option.defaultValue systemType
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
                        |> List.map (fun t -> t.SystemType, t)
                        |> Map.ofList
                    Ok templateMap
        with ex ->
            Error [{
                ApiCallId = None
                Message = $"Failed to load templates from '{templateDir}': {ex.Message}"
                ErrorType = InvalidTemplateFormat(templateDir, ex.Message)
            }]
