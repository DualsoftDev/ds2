namespace Ds2.IOList

open System

// =============================================================================
// Tag Generation - Auto-generate IO tags with address allocation
// =============================================================================

module TagGeneration =

    /// Tag generation pattern replacement
    let private applyPattern (pattern: string) (flowName: string) (deviceName: string) (apiName: string) : string =
        pattern
            .Replace("$(F)", flowName)
            .Replace("$(D)", deviceName)
            .Replace("$(A)", apiName)

    /// Address state for allocation
    type AddressState = {
        CurrentWord: int
        CurrentBit: int
    }

    /// Allocate address for a signal
    let private allocateAddress (prefix: string) (dataType: string) (state: AddressState) : string * AddressState =
        if dataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase) then
            let address = $"{prefix}{state.CurrentWord}.{state.CurrentBit}"
            let newBit = state.CurrentBit + 1
            if newBit >= 16 then
                (address, { CurrentWord = state.CurrentWord + 1; CurrentBit = 0 })
            else
                (address, { CurrentWord = state.CurrentWord; CurrentBit = newBit })
        else
            let address = $"{prefix}{state.CurrentWord}"
            (address, { CurrentWord = state.CurrentWord + 1; CurrentBit = 0 })

    /// Generate tag and address for a single item
    let generateTag
        (pattern: string)
        (addressPrefix: string)
        (flowName: string)
        (deviceName: string)
        (apiName: string)
        (dataType: string)
        (state: AddressState) : (string * string) * AddressState =

        let symbol = applyPattern pattern flowName deviceName apiName
        let address, newState = allocateAddress addressPrefix dataType state
        ((symbol, address), newState)

// =============================================================================
// C# Interop API
// =============================================================================

/// C# friendly API for tag generation
type TagGenerationApi() =

    /// Generate single tag and address
    static member GenerateSingleTag(
        pattern: string,
        addressPrefix: string,
        startAddress: int,
        flowName: string,
        deviceName: string,
        apiName: string,
        dataType: string
    ) : string * string =
        let initialState = { TagGeneration.CurrentWord = startAddress; TagGeneration.CurrentBit = 0 }
        let (symbol, address), _ = TagGeneration.generateTag pattern addressPrefix flowName deviceName apiName dataType initialState
        (symbol, address)

    /// Generate tags for multiple items (for batch generation)
    static member GenerateBatchTags(
        pattern: string,
        addressPrefix: string,
        startAddress: int,
        items: (string * string * string * string) array  // (flowName, deviceName, apiName, dataType)
    ) : (string * string) array =  // (symbol, address)

        let mutable state = { TagGeneration.CurrentWord = startAddress; TagGeneration.CurrentBit = 0 }
        let results = ResizeArray<string * string>()

        for (flowName, deviceName, apiName, dataType) in items do
            let (symbol, address), newState = TagGeneration.generateTag pattern addressPrefix flowName deviceName apiName dataType state
            results.Add((symbol, address))
            state <- newState

        results.ToArray()
