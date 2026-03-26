namespace Ds2.IOList

open System
open System.IO
open System.Text.RegularExpressions

// =============================================================================
// Address Allocation Configuration (Simplified: GLOBAL + LOCAL)
// =============================================================================

/// System-level address configuration (for GLOBAL-scoped systems)
type SystemAddressConfig = {
    SystemType: string
    IW_BaseAddress: int option
    QW_BaseAddress: int option
    MW_BaseAddress: int option
}

/// Flow-level address configuration (for LOCAL-scoped systems)
type FlowAddressConfig = {
    FlowName: string
    IW_BaseAddress: int option
    QW_BaseAddress: int option
    MW_BaseAddress: int option
}

/// Complete address configuration
type AddressConfig = {
    /// SystemTypes using GLOBAL allocation
    GlobalSystems: Map<string, SystemAddressConfig>
    /// Flows using LOCAL allocation (for systems NOT in GlobalSystems)
    LocalFlows: Map<string, FlowAddressConfig>
}

module AddressConfig =
    /// Create empty configuration
    let empty = {
        GlobalSystems = Map.empty
        LocalFlows = Map.empty
    }

    /// Check if SystemType uses GLOBAL allocation
    let isGlobalSystem (config: AddressConfig) (systemType: string) : bool =
        config.GlobalSystems |> Map.containsKey systemType

    /// Get base address for a GLOBAL SystemType
    let getSystemBase (config: AddressConfig) (systemType: string) (memoryArea: MemoryArea) : int option =
        config.GlobalSystems
        |> Map.tryFind systemType
        |> Option.bind (fun sysConfig ->
            match memoryArea with
            | InputWord -> sysConfig.IW_BaseAddress
            | OutputWord -> sysConfig.QW_BaseAddress
            | MemoryWord -> sysConfig.MW_BaseAddress)

    /// Get base address for a LOCAL Flow
    let getFlowBase (config: AddressConfig) (flowName: string) (memoryArea: MemoryArea) : int option =
        config.LocalFlows
        |> Map.tryFind flowName
        |> Option.bind (fun flowConfig ->
            match memoryArea with
            | InputWord -> flowConfig.IW_BaseAddress
            | OutputWord -> flowConfig.QW_BaseAddress
            | MemoryWord -> flowConfig.MW_BaseAddress)

// =============================================================================
// Address Config Parser
// =============================================================================

module AddressConfigParser =

    /// Parse state
    type private ParseState = {
        GlobalSystems: Map<string, SystemAddressConfig>
        LocalFlows: Map<string, FlowAddressConfig>
        CurrentSystem: string option
        CurrentSystemIW: int option
        CurrentSystemQW: int option
        CurrentSystemMW: int option
        CurrentFlow: string option
        CurrentFlowIW: int option
        CurrentFlowQW: int option
        CurrentFlowMW: int option
    }

    let private emptyState = {
        GlobalSystems = Map.empty
        LocalFlows = Map.empty
        CurrentSystem = None
        CurrentSystemIW = None
        CurrentSystemQW = None
        CurrentSystemMW = None
        CurrentFlow = None
        CurrentFlowIW = None
        CurrentFlowQW = None
        CurrentFlowMW = None
    }

    /// Flush current system config to map
    let private flushSystem (state: ParseState) : ParseState =
        match state.CurrentSystem with
        | None -> state
        | Some systemType ->
            let sysConfig = {
                SystemType = systemType
                IW_BaseAddress = state.CurrentSystemIW
                QW_BaseAddress = state.CurrentSystemQW
                MW_BaseAddress = state.CurrentSystemMW
            }
            { state with
                GlobalSystems = state.GlobalSystems |> Map.add systemType sysConfig
                CurrentSystem = None
                CurrentSystemIW = None
                CurrentSystemQW = None
                CurrentSystemMW = None
            }

    /// Flush current flow config to map
    let private flushFlow (state: ParseState) : ParseState =
        match state.CurrentFlow with
        | None -> state
        | Some flowName ->
            let flowConfig = {
                FlowName = flowName
                IW_BaseAddress = state.CurrentFlowIW
                QW_BaseAddress = state.CurrentFlowQW
                MW_BaseAddress = state.CurrentFlowMW
            }
            { state with
                LocalFlows = state.LocalFlows |> Map.add flowName flowConfig
                CurrentFlow = None
                CurrentFlowIW = None
                CurrentFlowQW = None
                CurrentFlowMW = None
            }

    /// Parse a directive line
    let private parseDirective (line: string) (state: ParseState) : ParseState =
        let parts = line.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length < 2 then state
        else
            match parts.[0].ToUpperInvariant() with
            | "@SYSTEM" ->
                // Flush previous system if any
                let flushed = flushSystem state
                { flushed with CurrentSystem = Some parts.[1] }

            | "@FLOW" ->
                // Flush previous flow if any
                let flushed = flushFlow state
                { flushed with CurrentFlow = Some parts.[1] }

            | "@IW_BASE" ->
                match Int32.TryParse(parts.[1]) with
                | true, addr ->
                    if state.CurrentSystem.IsSome then
                        { state with CurrentSystemIW = Some addr }
                    elif state.CurrentFlow.IsSome then
                        { state with CurrentFlowIW = Some addr }
                    else
                        state
                | false, _ -> state

            | "@QW_BASE" ->
                match Int32.TryParse(parts.[1]) with
                | true, addr ->
                    if state.CurrentSystem.IsSome then
                        { state with CurrentSystemQW = Some addr }
                    elif state.CurrentFlow.IsSome then
                        { state with CurrentFlowQW = Some addr }
                    else
                        state
                | false, _ -> state

            | "@MW_BASE" ->
                match Int32.TryParse(parts.[1]) with
                | true, addr ->
                    if state.CurrentSystem.IsSome then
                        { state with CurrentSystemMW = Some addr }
                    elif state.CurrentFlow.IsSome then
                        { state with CurrentFlowMW = Some addr }
                    else
                        state
                | false, _ -> state

            | _ -> state

    /// Parse a single line
    let private parseLine (line: string) (state: ParseState) : ParseState =
        let trimmed = line.Trim()

        // Skip empty lines and comments
        if String.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//") then
            state
        // Directive
        elif trimmed.StartsWith("@") then
            parseDirective trimmed state
        else
            state

    /// Build AddressConfig from parse state
    let private buildConfig (filePath: string) (state: ParseState) : Result<AddressConfig, string> =
        // Flush any pending system/flow
        let flushed = state |> flushSystem |> flushFlow

        Ok {
            GlobalSystems = flushed.GlobalSystems
            LocalFlows = flushed.LocalFlows
        }

    /// Parse address config file
    let parseFile (filePath: string) : Result<AddressConfig, string> =
        try
            if not (File.Exists(filePath)) then
                // Return empty config if file doesn't exist (optional file)
                Ok AddressConfig.empty
            else
                let lines = File.ReadAllLines(filePath)
                let finalState = lines |> Array.fold (fun state line -> parseLine line state) emptyState
                buildConfig filePath finalState
        with ex ->
            Error $"Failed to parse address config file '{filePath}': {ex.Message}"

    /// Load address config from directory (looks for address_config.txt)
    let loadFromDirectory (templateDir: string) : Result<AddressConfig, string> =
        let configPath = Path.Combine(templateDir, "address_config.txt")
        parseFile configPath
