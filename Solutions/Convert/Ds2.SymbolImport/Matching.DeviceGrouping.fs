namespace Ds2.SymbolImport.Matching

open System
open System.Collections.Generic

module DeviceGroupingCore =

    open DeviceGroupingUtils
    open InputMatching

    let private nonGroupName = Constants.NonGroupName

    /// <summary>
    /// Extract device prefixes from variable names using MappingSet-aware extraction
    /// Uses extractDeviceNameFromTag to remove I/O indicators and API names
    /// </summary>
    /// <param name="variableNames">List of variable names to analyze</param>
    /// <param name="mappingSets">Mapping sets for I/O keyword detection</param>
    /// <returns>Array of (device prefix, matching variable names) tuples</returns>
    /// <example>
    /// "STN1_Device1_ADV_O" → Device: "STN1_Device1"
    /// "STN1_Device1_RET_I" → Device: "STN1_Device1"
    /// </example>
    let extractDevicePrefixes (mappingSets: MappingSet list) (variableNames: string list) : (string * string[])[] =
        // Group variables by their device name
        variableNames
        |> List.groupBy (fun varName -> extractDeviceNameFromTag varName mappingSets)
        |> List.map (fun (deviceName, vars) -> (deviceName, List.toArray vars))
        |> List.sortByDescending (fun (deviceName, vars) -> deviceName.Length, vars.Length)
        |> Array.ofList

    /// <summary>
    /// Find group name from a list of device names (uses first segment as Flow/Group)
    /// SIMPLIFIED: Just extract the first segment from any device name
    /// </summary>
    /// <param name="deviceNames">Array of device names</param>
    /// <returns>Group name (first segment) or "NonGroup" if empty</returns>
    let findGroupName (deviceNames: string[]) : string =
        if deviceNames.Length = 0 then
            nonGroupName
        else
            // Use first segment of first device name as group name
            let firstDevice = deviceNames.[0]
            let parts = firstDevice.Split(segmentSeparators, StringSplitOptions.RemoveEmptyEntries)
            if parts.Length > 0 then parts.[0] else nonGroupName

    /// <summary>
    /// Private helper: Group variables by device based on direction
    /// </summary>
    let private groupVariablesByDevice (direction: IODirection) (mappingSets: MappingSet list) (variables: Variable list) : DeviceGroup[] =
        let isValidAddress = if direction = Output then isOutputAddress mappingSets else isInputAddress mappingSets

        // Patterns for %M address name-based I/O detection (LS SmartIO)
        let namePatterns =
            match direction with
            | Output -> [|"_O"; "_OUT"; "QX_"; "Q_"|]
            | Input -> [|"_I"; "_IN"; "_X"; "IX_"; "I_"; "_rxErr"; "_txErr"|]

        let matchesPattern (name: string) =
            namePatterns
            |> Array.exists (fun p ->
                name.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(p, StringComparison.OrdinalIgnoreCase)
                || name.Contains(p + "_", StringComparison.OrdinalIgnoreCase))

        let validVars =
            variables
            |> List.filter (fun v ->
                v.Direction = direction &&
                (isValidAddress v.PhysicalAddress ||
                 matchesPattern v.LogicalName ||
                 (v.PhysicalAddress.StartsWith("%M") && matchesPattern v.LogicalName)) &&
                (v.PhysicalAddress.Contains(":") || hasValidSegments v.LogicalName))

        if validVars.IsEmpty then
            [||]
        else
            // Group variables by device name using extractDeviceNameFromTag
            // For AB hardware addresses (containing ":"), use PhysicalAddress; otherwise use LogicalName
            let deviceGroups =
                validVars
                |> List.groupBy (fun v ->
                    let tagForExtraction =
                        if v.PhysicalAddress.Contains(":") then v.PhysicalAddress
                        else v.LogicalName
                    extractDeviceNameFromTag tagForExtraction mappingSets)
                |> dict

            // Create DeviceInfo for each device
            let devices =
                deviceGroups
                |> Seq.map (fun kvp ->
                    let deviceName = kvp.Key
                    let vars = kvp.Value

                    let flow = extractFlowFromDeviceName deviceName
                    let work = extractWorkFromDeviceName deviceName

                    // API 이름 중복 검사: 같은 디바이스에서 동일한 API 이름이 여러 개 있으면 첫 번째만 유지
                    let apis =
                        // API 이름과 해당 변수 정보를 함께 저장 (중복 검사용)
                        let apiVariableMap = System.Collections.Generic.Dictionary<string, Variable>()
                        vars
                        |> List.choose (fun v ->
                            // For AB hardware addresses, use LogicalName as API to ensure uniqueness
                            // Otherwise extract API from LogicalName
                            let api =
                                if v.PhysicalAddress.Contains(":") then
                                    v.LogicalName  // Use LogicalName (e.g., O0313) as API
                                else extractApiFromTag v.LogicalName mappingSets

                            // API 이름 중복 체크 - 중복 시 첫 번째 것만 유지하고 나머지는 무시
                            if apiVariableMap.ContainsKey(api) then
                                None  // Skip duplicate API
                            else
                                apiVariableMap.[api] <- v
                                Some { Flow = flow
                                       Work = work
                                       DeviceName = deviceName
                                       Api = api
                                       OutputVariableName = v.LogicalName
                                       OutputPhysicalAddress = v.PhysicalAddress
                                       InputVariableNames = []
                                       InputPhysicalAddresses = []
                                       MatchingStrategy = NotMatched
                                       MatchingConfidence = 0.0 })

                    { DeviceName = deviceName
                      Flow = flow
                      Work = work
                      Apis = apis
                      GroupName = "" })
                |> Seq.toList

            // Group devices by Flow to create device groups
            let flowGroups =
                devices
                |> List.groupBy (fun d -> d.Flow)
                |> List.map (fun (flow, devs) ->
                    { GroupName = flow
                      Devices = devs |> List.map (fun d -> { d with GroupName = flow }) })
                |> List.toArray

            flowGroups

    /// <summary>
    /// Group output variables by device
    /// </summary>
    /// <param name="mappingSets">Mapping sets for API keyword matching and address pattern validation</param>
    /// <param name="variables">List of output variables to group</param>
    /// <returns>Array of device groups</returns>
    let groupOutputsByDevice (mappingSets: MappingSet list) (variables: Variable list) : DeviceGroup[] =
        groupVariablesByDevice Output mappingSets variables

    /// <summary>
    /// Group input variables by device
    /// </summary>
    /// <param name="mappingSets">Mapping sets for API keyword matching and address pattern validation</param>
    /// <param name="variables">List of input variables to group</param>
    /// <returns>Array of device groups</returns>
    let groupInputsByDevice (mappingSets: MappingSet list) (variables: Variable list) : DeviceGroup[] =
        groupVariablesByDevice Input mappingSets variables

    /// <summary>
    /// Create device API mappings from variables with input-output matching using mapping sets
    /// NOTE: Duplicate variable name validation is now performed at the parser level (ParseAsync)
    /// </summary>
    /// <param name="variables">List of variables to process</param>
    /// <param name="mappingSets">Mapping sets to use for matching</param>
    /// <returns>List of all device API mappings with matched inputs</returns>
    let createDeviceApiMappingsWithMappingSets (variables: Variable list) (mappingSets: MappingSet list) : DeviceApiMapping list =
        // First, extract output device mappings
        let deviceGroups = groupOutputsByDevice mappingSets variables
        let outputMappings =
            deviceGroups
            |> Array.collect (fun group ->
                group.Devices
                |> List.collect (fun device -> device.Apis)
                |> List.toArray)
            |> Array.toList

        // DEDUPLICATE: Remove duplicate mappings based on unique key (DeviceName + Api + OutputPhysicalAddress)
        let uniqueOutputMappings =
            outputMappings
            |> List.groupBy (fun m -> (m.DeviceName, m.Api, m.OutputPhysicalAddress))
            |> List.map (fun (_, group) -> group |> List.head)  // Take first from each group

        // Then, match inputs to outputs using mapping sets + rule-based matching
        let matches = InputMatching.createInputOutputMappingsWithRules mappingSets uniqueOutputMappings variables

        // 성능 최적화: Dictionary로 O(1) 조회 (이전: List.tryFind로 O(N×M))
        let matchDict =
            matches
            |> List.map (fun m -> ((m.DeviceName, m.OutputApi, m.OutputVariable.PhysicalAddress), m))
            |> dict

        // Update output mappings with matched input information
        let mappings =
            uniqueOutputMappings
            |> List.map (fun mapping ->
                // Find the corresponding match result (O(1) 조회)
                let key = (mapping.DeviceName, mapping.Api, mapping.OutputPhysicalAddress)
                let matchResult =
                    match matchDict.TryGetValue(key) with
                    | true, m -> Some m
                    | false, _ -> None

                match matchResult with
                | Some m when InputMatching.isMatchSuccessful m ->
                    // Update with matched input information (MULTIPLE INPUTS)
                    let inputNames = m.InputVariables |> List.map (fun v -> v.LogicalName)
                    let inputAddresses = m.InputVariables |> List.map (fun v -> v.PhysicalAddress)
                    { mapping with
                        InputVariableNames = inputNames
                        InputPhysicalAddresses = inputAddresses
                        MatchingStrategy =
                            match m.Strategy with
                            | MappingSet -> MappingSet
                            | Transformer -> Transformer
                            | NotMatched -> NotMatched
                        MatchingConfidence = m.SimilarityScore }
                | _ ->
                    // No match found or match failed
                    mapping  // Keep as NotMatched with confidence 0.0
            )

        // Debug: CSV 출력 (실행 파일과 동일한 위치)
        #if DEBUG
        try
            let exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
            let timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss")
            let csvPath = System.IO.Path.Combine(exeDir, sprintf "matching_results_%s.csv" timestamp)
            let lines =
                [ "Output,OutputAddr,Input,InputAddr,Strategy,Confidence" ]
                @ (mappings |> List.collect (fun m ->
                    if m.InputVariableNames.IsEmpty then
                        [ sprintf "%s,%s,,,%s,%.2f" m.OutputVariableName m.OutputPhysicalAddress (m.MatchingStrategy.ToString()) m.MatchingConfidence ]
                    else
                        List.zip m.InputVariableNames m.InputPhysicalAddresses
                        |> List.map (fun (inName, inAddr) ->
                            sprintf "%s,%s,%s,%s,%s,%.2f" m.OutputVariableName m.OutputPhysicalAddress inName inAddr (m.MatchingStrategy.ToString()) m.MatchingConfidence)))
            System.IO.File.WriteAllLines(csvPath, lines)
            System.Diagnostics.Debug.WriteLine(sprintf "[DeviceGrouping] 매칭 결과 CSV 저장: %s (%d개 매핑)" csvPath mappings.Length)
        with ex ->
            System.Diagnostics.Debug.WriteLine(sprintf "[DeviceGrouping] CSV 저장 실패: %s" ex.Message)
        #endif

        mappings

    /// <summary>
    /// Create device API mappings from variables with input-output matching (main entry point)
    /// Uses default mapping sets
    /// </summary>
    /// <param name="variables">List of variables to process</param>
    /// <returns>List of all device API mappings with matched inputs</returns>
    let createDeviceApiMappings (variables: Variable list) : DeviceApiMapping list =
        // CSV 저장은 createDeviceApiMappingsWithMappingSets에서 수행됨
        createDeviceApiMappingsWithMappingSets variables (InputMatching.getDefaultMappingSets())

    // ============================================================================
    // Input-Output Matching Integration
    // ============================================================================

    /// <summary>
    /// Convert DeviceApiMapping to InputOutputMatch
    /// </summary>
    let private convertToInputOutputMatch (mapping: DeviceApiMapping) : InputMatching.InputOutputMatch =
        let outputVar = {
            LogicalName = mapping.OutputVariableName
            PhysicalAddress = mapping.OutputPhysicalAddress
            Direction = Output
        }

        if mapping.InputVariableNames.IsEmpty then
            {
                DeviceName = mapping.DeviceName
                OutputApi = mapping.Api
                OutputVariable = outputVar
                InputApis = []
                InputVariables = []
                Strategy = NotMatched
                SimilarityScore = 0.0
                Reason = "No matching inputs found"
            }
        else
            let inputPairs =
                List.zip mapping.InputVariableNames mapping.InputPhysicalAddresses
                |> List.map (fun (name, addr) ->
                    let inputVar = { LogicalName = name; PhysicalAddress = addr; Direction = Input }
                    (name.Replace(mapping.DeviceName, "").TrimStart('_').TrimStart(' '), inputVar))

            let inputApis = inputPairs |> List.map fst
            let inputVars = inputPairs |> List.map snd
            {
                DeviceName = mapping.DeviceName
                OutputApi = mapping.Api
                OutputVariable = outputVar
                InputApis = inputApis
                InputVariables = inputVars
                Strategy = mapping.MatchingStrategy
                SimilarityScore = mapping.MatchingConfidence
                Reason = sprintf "Matched %d inputs" inputPairs.Length
            }

    /// <summary>
    /// Create input-output mappings using default mapping sets
    /// </summary>
    /// <param name="variables">List of all variables (both input and output)</param>
    /// <returns>List of input-output matching results</returns>
    let createInputOutputMappings (variables: Variable list) : InputMatching.InputOutputMatch list =
        let outputMappings = createDeviceApiMappings variables
        outputMappings |> List.map convertToInputOutputMatch

    /// <summary>
    /// Create input-output mappings with custom mapping sets
    /// </summary>
    /// <param name="mappingSets">Custom mapping sets</param>
    /// <param name="variables">List of all variables (both input and output)</param>
    /// <returns>List of input-output matching results</returns>
    let createInputOutputMappingsWithMappingSets
        (mappingSets: MappingSet list)
        (variables: Variable list) : InputMatching.InputOutputMatch list =
        let outputMappings = createDeviceApiMappingsWithMappingSets variables mappingSets
        outputMappings |> List.map convertToInputOutputMatch

    /// <summary>
    /// Get matching statistics from input-output matching results
    /// </summary>
    /// <param name="matches">List of input-output matches</param>
    /// <returns>Tuple of (total outputs, matched count, unmatched count, average confidence)</returns>
    let getMatchingStatistics (matches: InputMatching.InputOutputMatch list) : int * int * int * float =
        InputMatching.getMatchingStatistics matches

    /// <summary>
    /// Filter successful matches from input-output matching results
    /// </summary>
    /// <param name="matches">List of input-output matches</param>
    /// <returns>List of successfully matched results only</returns>
    let getSuccessfulMatches (matches: InputMatching.InputOutputMatch list) : InputMatching.InputOutputMatch list =
        InputMatching.getSuccessfulMatches matches

    /// <summary>
    /// Filter failed matches from input-output matching results
    /// </summary>
    /// <param name="matches">List of input-output matches</param>
    /// <returns>List of failed match results (no input found)</returns>
    let getFailedMatches (matches: InputMatching.InputOutputMatch list) : InputMatching.InputOutputMatch list =
        InputMatching.getFailedMatches matches

    // ============================================================================
    // JSON Configuration File Support
    // ============================================================================

    /// <summary>
    /// Create device API mappings from JSON config file
    /// Falls back to default mapping sets if file doesn't exist
    /// </summary>
    /// <param name="configFilePath">Optional path to JSON config file</param>
    /// <param name="variables">List of all variables (both input and output)</param>
    /// <returns>List of device API mappings with input-output pairing</returns>
    let createDeviceApiMappingsFromFile (configFilePath: string option) (variables: Variable list) : DeviceApiMapping list =
        let mappingSets =
            match configFilePath with
            | Some path -> InputMatching.loadFromFile path
            | None -> InputMatching.getDefaultMappingSets()
        createDeviceApiMappingsWithMappingSets variables mappingSets

    /// <summary>
    /// Create input-output mappings from JSON config file
    /// Falls back to default mapping sets if file doesn't exist
    /// </summary>
    /// <param name="configFilePath">Optional path to JSON config file</param>
    /// <param name="variables">List of all variables (both input and output)</param>
    /// <returns>List of input-output matching results</returns>
    let createInputOutputMappingsFromFile (configFilePath: string option) (variables: Variable list) : InputMatching.InputOutputMatch list =
        let mappingSets =
            match configFilePath with
            | Some path -> InputMatching.loadFromFile path
            | None -> InputMatching.getDefaultMappingSets()
        createInputOutputMappingsWithMappingSets mappingSets variables
