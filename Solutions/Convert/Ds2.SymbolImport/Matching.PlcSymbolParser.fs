namespace Ds2.SymbolImport.Matching

open System

/// <summary>
/// PLC Symbol Parser following CSOT-17 5-7 Level Structure
/// Based on PLC Symbol List Standardization Specification
///
/// Symbol Structure:
/// Level 1: Station/Process (S141, MCP, LNR)
/// Level 2: I/O Type (SOL, LS, PS, RS, I, O, X, Y, Q, M, D, B)
/// Level 3-4: Equipment names (INDEX, LOAD, CLAMP)
/// Level 5-7: State/API (ADV, RET, OK, END)
///
/// Example: S141_SOL_INDEX_LOAD_CLP_ADV
/// - Level 1: S141 (Station)
/// - Level 2: SOL (Solenoid output type)
/// - Level 3: INDEX (Equipment 1)
/// - Level 4: LOAD (Equipment 2)
/// - Level 5: CLP (Clamp equipment)
/// - Level 6: ADV (Advance API)
/// </summary>
module PlcSymbolParser =

    /// <summary>
    /// All valid separators (underscore, dot, dash, space)
    /// </summary>
    let private allSeparators = [| '_'; '.'; '-'; ' ' |]

    /// <summary>
    /// Parse I/O Type string to IOType discriminated union
    /// </summary>
    /// <param name="text">Text to parse (e.g., "SOL", "LS", "PS")</param>
    /// <returns>IOType enum value</returns>
    let private parseIOType (text: string) : IOType =
        match text.ToUpperInvariant() with
        // Output types
        | "SOL" -> IOType.SOL
        | "O" -> IOType.O
        | "Y" -> IOType.Y
        | "Q" -> IOType.Q
        // Input types
        | "LS" -> IOType.LS
        | "PS" -> IOType.PS
        | "RS" -> IOType.RS
        | "I" -> IOType.I
        | "X" -> IOType.X
        | "M" -> IOType.M
        // Other types
        | "D" -> IOType.D
        | "B" -> IOType.B
        | _ -> IOType.Unknown

    /// <summary>
    /// Check if a string represents a known I/O Type
    /// </summary>
    /// <param name="text">Text to check</param>
    /// <returns>True if text is a known I/O type</returns>
    let private isIOType (text: string) : bool =
        parseIOType text <> IOType.Unknown

    /// <summary>
    /// Get I/O direction from IOType
    /// </summary>
    /// <param name="ioType">I/O Type to check</param>
    /// <returns>Input or Output direction</returns>
    let getDirectionFromIOType (ioType: IOType) : IODirection =
        match ioType with
        | IOType.SOL | IOType.O | IOType.Y | IOType.Q -> Output
        | IOType.LS | IOType.PS | IOType.RS | IOType.I | IOType.X -> Input
        | IOType.M | IOType.D | IOType.B | IOType.Unknown -> Input  // Default to Input for memory/data

    /// <summary>
    /// PLC 심볼을 레벨 리스트로 파싱
    /// 7개 이상의 레벨도 지원
    /// I/O 방향 표시자 제거는 MappingSet 설정값에 의존
    /// </summary>
    /// <param name="symbolName">전체 PLC 심볼 이름 (예: "S141_SOL_INDEX_LOAD_CLP_ADV")</param>
    /// <returns>레벨 리스트를 포함한 ParsedSymbol</returns>
    /// <example>
    /// parseSymbol "S141_SOL_INDEX_LOAD_CLP_ADV" =
    ///   { OriginalName = "S141_SOL_INDEX_LOAD_CLP_ADV"
    ///     Levels = ["S141"; "SOL"; "INDEX"; "LOAD"; "CLP"; "ADV"] }
    /// parseSymbol "STN1_Device1_ADV_O" =
    ///   { OriginalName = "STN1_Device1_ADV_O"
    ///     Levels = ["STN1"; "Device1"; "ADV"; "O"] }  // I/O indicator kept
    /// </example>
    let parseSymbol (symbolName: string) : ParsedSymbol =
        if String.IsNullOrWhiteSpace(symbolName) then
            { OriginalName = symbolName; Levels = [] }
        else
            let parts = symbolName.Split(allSeparators, StringSplitOptions.RemoveEmptyEntries) |> Array.toList
            { OriginalName = symbolName; Levels = parts }

    /// <summary>
    /// Extract device name from parsed symbol (Levels 1-4 or up to last API)
    /// Device name = Station + IOType + Equipment parts
    /// </summary>
    /// <param name="parsed">Parsed symbol</param>
    /// <returns>Device name without API suffix</returns>
    /// <example>
    /// getDeviceName (parseSymbol "S141_SOL_INDEX_LOAD_CLP_ADV") = "S141_SOL_INDEX_LOAD_CLP"
    /// getDeviceName (parseSymbol "Device1_ADV") = "Device1"
    /// </example>
    let getDeviceName (parsed: ParsedSymbol) : string =
        // If we have 5+ levels, device is everything except the last level (API)
        // Otherwise, use the entire name
        if parsed.Levels.Length >= 5 then
            let deviceLevels = parsed.Levels |> List.take (parsed.Levels.Length - 1)
            String.Join("_", deviceLevels)
        elif parsed.Levels.Length >= 2 then
            // For shorter symbols, use all but last level
            let deviceLevels = parsed.Levels |> List.take (parsed.Levels.Length - 1)
            String.Join("_", deviceLevels)
        else
            // Single level - entire name is device
            parsed.OriginalName

    /// <summary>
    /// Extract API name from parsed symbol (last level, or Level 5+)
    /// </summary>
    /// <param name="parsed">Parsed symbol</param>
    /// <returns>API name (last level) or "DO" if no API detected</returns>
    /// <example>
    /// getApiName (parseSymbol "S141_SOL_INDEX_LOAD_CLP_ADV") = "ADV"
    /// getApiName (parseSymbol "Device1_RET") = "RET"
    /// getApiName (parseSymbol "Motor1") = "DO"
    /// </example>
    let getApiName (parsed: ParsedSymbol) : string =
        if parsed.Levels.Length >= 2 then
            // Last level is API
            parsed.Levels |> List.last
        else
            // No API detected - use default
            Constants.DefaultApi

    /// <summary>
    /// 파싱된 심볼에서 Flow 이름 추출 (레벨 1 또는 레벨 1+2)
    /// 레벨 2가 숫자인 경우 레벨 1과 2를 결합하여 Flow 생성 (예: "S102_1")
    /// </summary>
    /// <param name="parsed">파싱된 심볼</param>
    /// <returns>Flow 이름 (레벨 1 또는 레벨 1+2) 또는 레벨이 1개이거나 없으면 "DEFAULT"</returns>
    /// <example>
    /// getFlowName (parseSymbol "S141_SOL_INDEX_ADV") = "S141"
    /// getFlowName (parseSymbol "S102_1_SOL_DEVICE_ADV") = "S102_1"
    /// getFlowName (parseSymbol "Motor1") = "DEFAULT"
    /// </example>
    let private isStationCode (text: string) : bool =
        if String.IsNullOrWhiteSpace text || text.Length < 2 then false
        else
            Char.ToUpperInvariant(text.[0]) = 'S'
            && text.Substring(1) |> Seq.forall Char.IsDigit

    let getFlowName (parsed: ParsedSymbol) : string =
        if parsed.Levels.Length > 1 then
            // Check if Level 2 is a number
            if parsed.Levels.Length >= 2
               && isStationCode parsed.Levels.[0]
               && (parsed.Levels.[1] |> Seq.forall Char.IsDigit) then
                // Level 2 is numeric - combine Level 1 + Level 2
                sprintf "%s_%s" parsed.Levels.[0] parsed.Levels.[1]
            else
                // Level 2 is not numeric - use Level 1 only
                parsed.Levels.[0]
        else
            Constants.DefaultFlowName

    /// <summary>
    /// 알파벳+숫자 접미사 패턴 (RB1, RBT2, Device3 등) 감지
    /// 로봇(RBx, RBTx), 디바이스(Devicex) 등을 숫자별로 분리하기 위함
    /// </summary>
    let private hasNumericSuffix (text: string) : bool =
        if String.IsNullOrEmpty(text) || text.Length < 2 then false
        else
            // 끝이 숫자이고, 시작이 알파벳인 경우 (RB1, RBT2, Device3 등)
            let lastChar = text.[text.Length - 1]
            let firstChar = text.[0]
            Char.IsDigit(lastChar) && Char.IsLetter(firstChar) &&
            // 전부 숫자가 아닌 경우만 (순수 숫자 "123"은 제외)
            not (text |> Seq.forall Char.IsDigit)

    /// <summary>
    /// I/O 타입 키워드인지 확인 (SOL, O, Y, Q, LS, PS, RS, I, X, M, D, B)
    /// Level 2가 I/O 타입이면 Level 3을 확인해야 함
    /// </summary>
    let private isIOTypeKeyword (text: string) : bool =
        match text.ToUpperInvariant() with
        | "SOL" | "O" | "Y" | "Q"           // Output types
        | "QX" | "QW" | "QB" | "QD"
        | "LS" | "PS" | "RS" | "I" | "X"    // Input types
        | "IX" | "IW" | "IB" | "ID"
        | "M" | "D" | "B" -> true           // Memory/Data types
        | _ -> false

    /// <summary>
    /// 파싱된 심볼에서 Work 이름 추출
    /// - Level 2가 I/O 타입(I, O, SOL 등)이면 Level 3 확인
    /// - RBx, RBTx, Devicex 패턴: 해당 레벨 전체를 Work로 사용 (숫자별 분리)
    /// - 순수 숫자인 경우: 다음 레벨을 Work로 사용
    /// </summary>
    /// <param name="parsed">파싱된 심볼</param>
    /// <returns>Work 이름 또는 레벨이 부족하면 "DEFAULT"</returns>
    /// <example>
    /// getWorkName (parseSymbol "S141_I_RB1_1ST_WORK_COMP") = "RB1"
    /// getWorkName (parseSymbol "S141_I_RB2_1ST_WORK_COMP") = "RB2"
    /// getWorkName (parseSymbol "S141_SOL_INDEX_ADV") = "INDEX"
    /// getWorkName (parseSymbol "STN1_Work1_Device4") = "Work1"
    /// getWorkName (parseSymbol "S141_RB1_ARM_ADV") = "RB1"
    /// getWorkName (parseSymbol "S102_1_SOL_DEVICE_ADV") = "SOL"
    /// getWorkName (parseSymbol "Device1_ADV") = "ADV"
    /// getWorkName (parseSymbol "Motor1") = "DEFAULT"
    /// </example>
    let getWorkName (parsed: ParsedSymbol) : string =
        if parsed.Levels.Length >= 2 then
            let level2 = parsed.Levels.[1]

            // Case 1: Level 2가 I/O 타입 (I, O, SOL 등) → Level 3 확인
            if isIOTypeKeyword level2 then
                if parsed.Levels.Length >= 3 then
                    let level3 = parsed.Levels.[2]
                    // Level 3이 알파벳+숫자 패턴 (RB1, RB2 등)이면 Level 3을 Work로
                    if hasNumericSuffix level3 then
                        level3
                    else
                        level3  // I/O 타입 다음 레벨을 Work로 사용
                else
                    Constants.DefaultWorkName
            // Case 2: Level 2가 알파벳+숫자 패턴 (RB1, RBT2, Device3 등) → Level 2를 Work로
            elif hasNumericSuffix level2 then
                level2
            // Case 3: Level 2가 순수 숫자 → Level 3을 Work로
            elif level2 |> Seq.forall Char.IsDigit then
                if not (isStationCode parsed.Levels.[0]) then
                    parsed.Levels.[0]
                elif parsed.Levels.Length >= 3 then
                    parsed.Levels.[2]
                else
                    Constants.DefaultWorkName
            // Case 4: 그 외 → Level 2를 Work로
            else
                level2
        else
            Constants.DefaultWorkName

    /// <summary>
    /// 파싱된 심볼에서 장비 파트 추출 (레벨 3 이후)
    /// </summary>
    /// <param name="parsed">파싱된 심볼</param>
    /// <returns>장비 이름 리스트</returns>
    /// <example>
    /// getEquipmentParts (parseSymbol "S141_SOL_INDEX_LOAD_CLP_ADV") = ["INDEX"; "LOAD"; "CLP"]
    /// </example>
    let getEquipmentParts (parsed: ParsedSymbol) : string list =
        // I/O 타입 이후부터 마지막 전까지가 장비 파트
        let ioTypeIndex =
            parsed.Levels
            |> List.tryFindIndex isIOType
            |> Option.defaultValue 1

        if parsed.Levels.Length > ioTypeIndex + 1 then
            parsed.Levels
            |> List.skip (ioTypeIndex + 1)
            |> List.take (parsed.Levels.Length - ioTypeIndex - 2)
        else
            []

    /// <summary>
    /// Check if parsed symbol has valid multi-level structure (3+ levels)
    /// </summary>
    /// <param name="parsed">Parsed symbol</param>
    /// <returns>True if symbol has 3 or more levels</returns>
    let hasValidStructure (parsed: ParsedSymbol) : bool =
        parsed.Levels.Length >= 3

    /// <summary>
    /// Parse symbol name directly from string (convenience function)
    /// Returns device name and API name as tuple
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>Tuple of (device name, API name)</returns>
    /// <example>
    /// parseDeviceAndApi "S141_SOL_INDEX_LOAD_CLP_ADV" = ("S141_SOL_INDEX_LOAD_CLP", "ADV")
    /// parseDeviceAndApi "Device1_RET" = ("Device1", "RET")
    /// </example>
    let parseDeviceAndApi (symbolName: string) : string * string =
        let parsed = parseSymbol symbolName
        (getDeviceName parsed, getApiName parsed)

    /// <summary>
    /// Parse symbol and extract Flow name
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>Flow name (Level 1)</returns>
    let parseFlowName (symbolName: string) : string =
        let parsed = parseSymbol symbolName
        getFlowName parsed

    /// <summary>
    /// Parse symbol and extract Work name
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>Work name (Level 2 or 3)</returns>
    let parseWorkName (symbolName: string) : string =
        let parsed = parseSymbol symbolName
        getWorkName parsed

    /// <summary>
    /// Backward-compatible device name extraction using LAST delimiter
    /// This maintains compatibility with the previous simple parser
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>Device name (everything before last '_' or ' ')</returns>
    /// <example>
    /// extractDeviceNameSimple "S141_SOL_INDEX_LOAD_CLP_ADV" = "S141_SOL_INDEX_LOAD_CLP"
    /// extractDeviceNameSimple "Device1_ADV" = "Device1"
    /// extractDeviceNameSimple "Motor1" = "Motor1"
    /// </example>
    let extractDeviceNameSimple (symbolName: string) : string =
        let lastUnderscoreIdx = symbolName.LastIndexOf('_')
        let lastSpaceIdx = symbolName.LastIndexOf(' ')
        let lastSepIdx = max lastUnderscoreIdx lastSpaceIdx

        if lastSepIdx > 0 then
            symbolName.Substring(0, lastSepIdx)
        else
            symbolName

    /// <summary>
    /// Backward-compatible API name extraction using LAST delimiter
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>API name (everything after last '_' or ' ') or "DO" if no separator</returns>
    /// <example>
    /// extractApiNameSimple "S141_SOL_INDEX_LOAD_CLP_ADV" = "ADV"
    /// extractApiNameSimple "Device1_RET" = "RET"
    /// extractApiNameSimple "Motor1" = "DO"
    /// </example>
    let extractApiNameSimple (symbolName: string) : string =
        let lastUnderscoreIdx = symbolName.LastIndexOf('_')
        let lastSpaceIdx = symbolName.LastIndexOf(' ')
        let lastSepIdx = max lastUnderscoreIdx lastSpaceIdx

        if lastSepIdx > 0 && lastSepIdx < symbolName.Length - 1 then
            symbolName.Substring(lastSepIdx + 1)
        else
            Constants.DefaultApi

    /// <summary>
    /// 심볼 이름에서 I/O 타입을 감지하여 I/O 방향 가져오기
    /// </summary>
    /// <param name="symbolName">전체 심볼 이름</param>
    /// <returns>Input 또는 Output 방향</returns>
    /// <example>
    /// getDirectionFromSymbol "S141_SOL_INDEX_ADV" = Output
    /// getDirectionFromSymbol "S141_LS_INDEX_OK" = Input
    /// </example>
    let getDirectionFromSymbol (symbolName: string) : IODirection =
        let parsed = parseSymbol symbolName
        let ioType =
            parsed.Levels
            |> List.tryFind isIOType
            |> Option.map parseIOType
            |> Option.defaultValue IOType.Unknown

        if ioType <> IOType.Unknown then
            getDirectionFromIOType ioType
        else
            Output  // 기본값은 Output

    /// <summary>
    /// Check if symbol represents an output variable
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>True if symbol is an output type</returns>
    let isOutputSymbol (symbolName: string) : bool =
        getDirectionFromSymbol symbolName = Output

    /// <summary>
    /// Check if symbol represents an input variable
    /// </summary>
    /// <param name="symbolName">Full symbol name</param>
    /// <returns>True if symbol is an input type</returns>
    let isInputSymbol (symbolName: string) : bool =
        getDirectionFromSymbol symbolName = Input
