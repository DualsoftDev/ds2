namespace Ds2.IOList

open System

// =============================================================================
// Signal Generator with Simplified Address Allocation (GLOBAL + LOCAL)
// =============================================================================

module SignalGenerator =

    /// Allocation state for tracking slot positions
    type AllocationState = {
        /// Global counters per SystemType and MemoryArea
        GlobalCounters: Map<string * MemoryArea, int>
        /// Local counters per Flow and MemoryArea
        LocalCounters: Map<string * MemoryArea, int>
    }

    module AllocationState =
        let empty = {
            GlobalCounters = Map.empty
            LocalCounters = Map.empty
        }

        /// Get and increment global counter for SystemType+MemoryArea
        let getAndIncrementGlobal (systemType: string) (memoryArea: MemoryArea) (state: AllocationState) : int * AllocationState =
            let key = (systemType, memoryArea)
            let currentCount = state.GlobalCounters |> Map.tryFind key |> Option.defaultValue 0
            let newState = { state with GlobalCounters = state.GlobalCounters |> Map.add key (currentCount + 1) }
            (currentCount, newState)

        /// Get and increment local counter for Flow+MemoryArea
        let getAndIncrementLocal (flowName: string) (memoryArea: MemoryArea) (state: AllocationState) : int * AllocationState =
            let key = (flowName, memoryArea)
            let currentCount = state.LocalCounters |> Map.tryFind key |> Option.defaultValue 0
            let newState = { state with LocalCounters = state.LocalCounters |> Map.add key (currentCount + 1) }
            (currentCount, newState)

    /// Find template slot by ApiDef name
    let private findSlot (apiDefName: string) (slots: TemplateSlot list) : (int * string) option =
        slots
        |> List.mapi (fun i slot ->
            match slot with
            | Signal(name, pattern) when name = apiDefName -> Some(i, pattern)
            | _ -> None)
        |> List.tryPick id

    /// Substitute template variables in pattern
    let private substitutePattern (pattern: string) (flowName: string) (deviceAlias: string) : string =
        pattern
            .Replace("$(F)", flowName)
            .Replace("$(D)", deviceAlias)

    /// Calculate PLC address from slot index and base address
    let private calculateAddress (memoryArea: MemoryArea) (baseAddress: int) (slotIndex: int) : string =
        let wordOffset = slotIndex / 16
        let bitOffset = slotIndex % 16
        let address = baseAddress + wordOffset

        let prefix =
            match memoryArea with
            | InputWord -> "IW"
            | OutputWord -> "QW"
            | MemoryWord -> "MW"

        $"%%{prefix}{address}.{bitOffset}"

    /// Get base address based on allocation strategy
    /// NOTE: Template base addresses are ignored - only address_config.txt is used
    let private getBaseAddress
        (addressConfig: AddressConfig)
        (_template: MacroTemplate)
        (context: GenerationContext)
        (memoryArea: MemoryArea)
        : int option =

        // Check if this SystemType is GLOBAL
        if AddressConfig.isGlobalSystem addressConfig context.SystemType then
            // GLOBAL: Use SystemType-based address from address_config.txt
            AddressConfig.getSystemBase addressConfig context.SystemType memoryArea
        else
            // LOCAL: Use Flow-based address from address_config.txt
            AddressConfig.getFlowBase addressConfig context.FlowName memoryArea

    /// Generate signals for a single context and memory area
    let private generateForMemoryArea
        (context: GenerationContext)
        (template: MacroTemplate)
        (addressConfig: AddressConfig)
        (memoryArea: MemoryArea)
        (state: AllocationState)
        : (SignalRecord list * GenerationError list * AllocationState) =

        let slots, ioType =
            match memoryArea with
            | InputWord -> template.InputSlots, "IW"
            | OutputWord -> template.OutputSlots, "QW"
            | MemoryWord -> template.MemorySlots, "MW"

        match findSlot context.ApiDefName slots with
        | None ->
            // ApiDef not found in template - ERROR
            let error = GenerationError.apiDefNotInTemplate context.ApiCallId context.ApiDefName context.SystemType
            ([], [error], state)

        | Some (_templateSlotIndex, pattern) ->
            // Found - determine allocation strategy
            let slotIndex, newState =
                if AddressConfig.isGlobalSystem addressConfig context.SystemType then
                    // GLOBAL: Use SystemType counter
                    AllocationState.getAndIncrementGlobal context.SystemType memoryArea state
                else
                    // LOCAL: Use Flow counter
                    AllocationState.getAndIncrementLocal context.FlowName memoryArea state

            // Get base address
            match getBaseAddress addressConfig template context memoryArea with
            | None ->
                let error = {
                    ApiCallId = Some context.ApiCallId
                    Message = $"No base address configured for {context.SystemType}/{context.FlowName} in {memoryArea}"
                    ErrorType = MissingOriginFlowId context.ApiCallId  // Reuse error type
                }
                ([], [error], newState)

            | Some baseAddress ->
                // Generate signal
                let varName = substitutePattern pattern context.FlowName context.DeviceAlias
                let address = calculateAddress memoryArea baseAddress slotIndex

                // Select data type based on memory area
                let dataType =
                    match memoryArea with
                    | InputWord -> context.InputDataType
                    | OutputWord -> context.OutputDataType
                    | MemoryWord -> context.OutputDataType  // MW uses output data type

                let signal = SignalRecord.createIo varName address ioType context.SystemType dataType context.FlowName context.WorkName context.CallName context.ApiDefName
                ([signal], [], newState)

    /// Generate IO signals (IW/QW) for a context
    let private generateIoSignals
        (context: GenerationContext)
        (template: MacroTemplate)
        (addressConfig: AddressConfig)
        (state: AllocationState)
        : (SignalRecord list * GenerationError list * AllocationState) =

        // Try both IW and QW
        let inputSignals, inputErrors, state1 = generateForMemoryArea context template addressConfig InputWord state
        let outputSignals, outputErrors, state2 = generateForMemoryArea context template addressConfig OutputWord state1

        let allSignals = inputSignals @ outputSignals

        // Only report error if API not found in BOTH IW and QW
        let hasAnySignal = not (List.isEmpty allSignals)
        let allErrors =
            if hasAnySignal then
                [] // Success - found in at least one area
            else
                // Failed - not found in either IW or QW
                (inputErrors @ outputErrors)
                |> List.distinctBy (fun err -> err.ErrorType)

        (allSignals, allErrors, state2)

    /// Generate Dummy signals (MW) for a context
    let private generateDummySignals
        (context: GenerationContext)
        (template: MacroTemplate)
        (addressConfig: AddressConfig)
        (state: AllocationState)
        : (SignalRecord list * GenerationError list * AllocationState) =

        // MW signals are optional - if not found in template, just return empty (no error)
        let signals, _errors, newState = generateForMemoryArea context template addressConfig MemoryWord state
        (signals, [], newState)  // Ignore errors for MW - they're optional

    /// Generate all signals for a single context
    let private generateForContext
        (context: GenerationContext)
        (templates: Map<string, MacroTemplate>)
        (addressConfig: AddressConfig)
        (state: AllocationState)
        : (GenerationResult * AllocationState) =

        match Map.tryFind context.SystemType templates with
        | None ->
            // Template not found - ERROR
            let result = {
                IoSignals = []
                DummySignals = []
                Errors = [GenerationError.templateNotFound context.SystemType]
                Warnings = []
            }
            (result, state)

        | Some template ->
            let ioSignals, ioErrors, state1 = generateIoSignals context template addressConfig state
            let dummySignals, dummyErrors, state2 = generateDummySignals context template addressConfig state1

            let result = {
                IoSignals = ioSignals
                DummySignals = dummySignals
                Errors = ioErrors @ dummyErrors
                Warnings = []
            }
            (result, state2)

    /// Generate all signals for multiple contexts
    let generateAll
        (contexts: GenerationContext list)
        (templates: Map<string, MacroTemplate>)
        (addressConfig: AddressConfig)
        : GenerationResult =

        // Fold over contexts, threading allocation state
        let finalResult, _finalState =
            contexts
            |> List.fold (fun (accResult, accState) ctx ->
                let result, newState = generateForContext ctx templates addressConfig accState
                let combinedResult = GenerationResult.combine [accResult; result]
                (combinedResult, newState)
            ) (GenerationResult.empty, AllocationState.empty)

        finalResult
