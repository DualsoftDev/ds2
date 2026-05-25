namespace Ds2.SymbolImport.Matching

open System
open System.IO
open System.Collections.Generic
open Newtonsoft.Json.Linq

module DeviceGroupingUtils =

    // ============================================================================
    // Config 경로 관리 (외부에서 설정 가능)
    // ============================================================================

    let private defaultConfigFileName = "input-matching-config.json"

    /// 현재 Config 파일 경로 (외부에서 설정 가능)
    let mutable private currentConfigPath =
        Path.Combine(AppContext.BaseDirectory, defaultConfigFileName)

    /// Config 파일 경로 설정 (런타임에 변경 가능)
    let setConfigPath (path: string) : unit =
        currentConfigPath <- path

    /// 현재 Config 파일 경로 반환
    let getConfigPath () : string = currentConfigPath

    // ============================================================================
    // ApiNaming 설정 (CompoundSuffixes) - 와일드카드 패턴 지원
    // ============================================================================

    /// 기본 CompoundSuffixes (config 파일이 없을 때 사용)
    /// config 로드 실패 시에도 1ST/?ND/?RD/?TH/IN/READY/WORK 등을 포함해 API가 잘려나가지 않도록 확장
    let private defaultCompoundSuffixes =
        [|
            "OK"; "COMP"; "POS"; "RST"; "END"; "CHK"; "READY"; "IN"; "ERR"; "EXT"; "WORK";
            "1ST"; "?ND"; "?RD"; "?TH"; "SV"; "CT"; "B"; "C"; "D"; "E"; "F"; "G"; "H"
        |]

    /// 와일드카드 패턴 매칭 (* = 0개 이상 문자, ? = 1개 문자)
    /// InputMatching에서 SymmetryRules 와일드카드 지원을 위해 public으로 노출
    let matchesWildcard (pattern: string) (input: string) : bool =
        let patternUpper = pattern.ToUpperInvariant()
        let inputUpper = input.ToUpperInvariant()

        // 와일드카드가 없으면 정확히 일치 검사
        if not (patternUpper.Contains('*') || patternUpper.Contains('?')) then
            patternUpper = inputUpper
        else
            // 간단한 와일드카드 매칭 (regex 변환)
            let regexPattern =
                patternUpper
                    .Replace(".", "\\.")
                    .Replace("*", ".*")
                    .Replace("?", ".")
            let regex = System.Text.RegularExpressions.Regex("^" + regexPattern + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            regex.IsMatch(inputUpper)

    /// 토큰이 CompoundSuffixes 패턴 중 하나와 매칭되는지 확인
    let private matchesAnySuffix (suffixPatterns: string[]) (token: string) : bool =
        suffixPatterns |> Array.exists (fun pattern -> matchesWildcard pattern token)

    /// Config 파일에서 CompoundSuffixes 로드 (캐시, 무효화 가능)
    let mutable private cachedCompoundSuffixes : string[] option = None
    let private cacheSync = obj()

    let private loadCompoundSuffixes () : string[] =
        let configPath = currentConfigPath
        System.Diagnostics.Debug.WriteLine(sprintf "[MatchingUtil] Loading CompoundSuffixes from: %s" configPath)
        if not (File.Exists configPath) then
            System.Diagnostics.Debug.WriteLine("[MatchingUtil] Config file not found, using defaults")
            defaultCompoundSuffixes
        else
            try
                let json = File.ReadAllText(configPath)
                let jobj = JObject.Parse(json)
                let apiNaming = jobj.["ApiNaming"]
                if isNull apiNaming then
                    System.Diagnostics.Debug.WriteLine("[MatchingUtil] ApiNaming section not found, using defaults")
                    defaultCompoundSuffixes
                else
                    let suffixes = apiNaming.["CompoundSuffixes"]
                    if isNull suffixes then
                        System.Diagnostics.Debug.WriteLine("[MatchingUtil] CompoundSuffixes not found, using defaults")
                        defaultCompoundSuffixes
                    else
                        let result = suffixes.ToObject<string[]>() |> Array.map (fun s -> s.ToUpperInvariant())
                        System.Diagnostics.Debug.WriteLine(sprintf "[MatchingUtil] Loaded %d CompoundSuffixes: %s" result.Length (String.Join(", ", result)))
                        result
            with ex ->
                System.Diagnostics.Debug.WriteLine(sprintf "[MatchingUtil] Error loading CompoundSuffixes: %s" ex.Message)
                defaultCompoundSuffixes

    /// CompoundSuffixes 패턴 배열 가져오기 (캐시됨)
    let getCompoundSuffixes () : string[] =
        lock cacheSync (fun () ->
            match cachedCompoundSuffixes with
            | Some cached -> cached
            | None ->
                let loaded = loadCompoundSuffixes()
                cachedCompoundSuffixes <- Some loaded
                loaded)

    /// 캐시 무효화 - 설정 변경 후 호출
    let invalidateCompoundSuffixesCache () : unit =
        lock cacheSync (fun () ->
            cachedCompoundSuffixes <- None)

    /// 토큰이 CompoundSuffix 패턴과 매칭되는지 확인
    let isCompoundSuffix (token: string) : bool =
        matchesAnySuffix (getCompoundSuffixes()) token

    // ============================================================================
    // IOTypeTokens 설정 - I/O 타입 토큰 (Device/API 이름에서 제거)
    // ============================================================================

    /// 기본 IOTypeTokens (config 파일이 없을 때 사용)
    let private defaultIOTypeTokens = [| "Q"; "QX"; "QW"; "QB"; "QD"; "I"; "IX"; "IW"; "IB"; "ID"; "O"; "Y"; "X"; "M" |]

    let mutable private cachedIOTypeTokens : HashSet<string> option = None
    let private ioTypeTokensSync = obj()

    let private loadIOTypeTokens () : HashSet<string> =
        let configPath = currentConfigPath
        if not (File.Exists configPath) then
            HashSet<string>(defaultIOTypeTokens, StringComparer.OrdinalIgnoreCase)
        else
            try
                let json = File.ReadAllText(configPath)
                let jobj = JObject.Parse(json)
                let apiNaming = jobj.["ApiNaming"]
                if isNull apiNaming then
                    HashSet<string>(defaultIOTypeTokens, StringComparer.OrdinalIgnoreCase)
                else
                    let tokens = apiNaming.["IOTypeTokens"]
                    if isNull tokens then
                        HashSet<string>(defaultIOTypeTokens, StringComparer.OrdinalIgnoreCase)
                    else
                        let arr = tokens.ToObject<string[]>() |> Array.map (fun s -> s.ToUpperInvariant())
                        System.Diagnostics.Debug.WriteLine(sprintf "[MatchingUtil] Loaded IOTypeTokens: %s" (String.Join(", ", arr)))
                        HashSet<string>(arr, StringComparer.OrdinalIgnoreCase)
            with _ ->
                HashSet<string>(defaultIOTypeTokens, StringComparer.OrdinalIgnoreCase)

    /// IOTypeTokens 가져오기 (캐시됨)
    let getIOTypeTokens () : HashSet<string> =
        lock ioTypeTokensSync (fun () ->
            match cachedIOTypeTokens with
            | Some cached -> cached
            | None ->
                let loaded = loadIOTypeTokens()
                cachedIOTypeTokens <- Some loaded
                loaded)

    /// IOTypeTokens 캐시 무효화
    let invalidateIOTypeTokensCache () : unit =
        lock ioTypeTokensSync (fun () -> cachedIOTypeTokens <- None)

    /// 토큰이 I/O 타입 토큰인지 확인
    let isIOTypeToken (token: string) : bool =
        (getIOTypeTokens()).Contains(token)

    let private trimBoundaryIOTypeTokens (levels: string list) =
        let withoutLeading =
            match levels with
            | first :: rest when isIOTypeToken first -> rest
            | _ -> levels
        match withoutLeading with
        | [] -> []
        | _ ->
            let lastToken = withoutLeading |> List.last
            if withoutLeading.Length >= 2 && isIOTypeToken lastToken then
                withoutLeading |> List.take (withoutLeading.Length - 1)
            else
                withoutLeading

    // ============================================================================
    // WorkNaming 설정 - Work 분할 깊이 (0 = 자동, N = 앞에서 N개 토큰)
    // ============================================================================

    let mutable private cachedWorkSplitDepth : int option = None
    let private workSplitDepthSync = obj()

    let private loadWorkSplitDepth () : int =
        let configPath = currentConfigPath
        if not (File.Exists configPath) then
            0  // 기본값: 자동 (CompoundSuffixes 기반)
        else
            try
                let json = File.ReadAllText(configPath)
                let jobj = JObject.Parse(json)
                let workNaming = jobj.["WorkNaming"]
                if isNull workNaming then
                    0
                else
                    let depth = workNaming.["SplitDepth"]
                    if isNull depth then
                        0
                    else
                        let value = depth.ToObject<int>()
                        System.Diagnostics.Debug.WriteLine(sprintf "[MatchingUtil] Loaded WorkSplitDepth: %d" value)
                        value
            with _ ->
                0

    /// WorkSplitDepth 가져오기 (캐시됨)
    /// 0 = 자동 (CompoundSuffixes 기반), N = 앞에서 N개 토큰을 Work로
    let getWorkSplitDepth () : int =
        lock workSplitDepthSync (fun () ->
            match cachedWorkSplitDepth with
            | Some cached -> cached
            | None ->
                let loaded = loadWorkSplitDepth()
                cachedWorkSplitDepth <- Some loaded
                loaded)

    /// WorkSplitDepth 캐시 무효화
    let invalidateWorkSplitDepthCache () : unit =
        lock workSplitDepthSync (fun () -> cachedWorkSplitDepth <- None)

    // 성능 최적화: ioKeywords 캐시 (MappingSet 리스트 해시 기반)
    let private ioKeywordsCache = Dictionary<int, HashSet<string>>()

    /// ioKeywordsCache 무효화
    let invalidateIOKeywordsCache () : unit =
        lock ioKeywordsCache (fun () -> ioKeywordsCache.Clear())

    /// MappingSet 리스트에서 I/O 키워드 집합 추출 (캐시됨)
    let private getIOKeywordsSet (mappingSets: MappingSet list) : HashSet<string> =
        let hash = mappingSets.GetHashCode()
        lock ioKeywordsCache (fun () ->
            match ioKeywordsCache.TryGetValue(hash) with
            | true, cached -> cached
            | false, _ ->
                let keywords =
                    mappingSets
                    |> List.collect (fun ms ->
                        ms.Apis
                        |> Map.toList
                        |> List.collect (fun (_, apiMap) ->
                            apiMap.OutputKeywords @ apiMap.InputKeywords))
                    |> List.map (fun kw -> kw.TrimStart('_').ToUpperInvariant())
                    |> List.distinct
                let set = HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase)
                ioKeywordsCache.[hash] <- set
                set)

    /// <summary>
    /// 디바이스 이름 파싱을 위한 세그먼트 구분자
    /// </summary>
    let segmentSeparators = Constants.SegmentSeparators

    /// <summary>
    /// Check if a segment is a defined API name in any MappingSet
    /// </summary>
    let isApiName (segment: string) (mappingSets: MappingSet list) : bool =
        mappingSets
        |> List.exists (fun ms ->
            ms.Apis |> Map.containsKey segment)

    /// <summary>
    /// 디바이스 이름에서 Flow (첫 번째 세그먼트) 추출
    /// 구조화된 파싱을 위해 PlcSymbolParser 사용
    /// </summary>
    /// <param name="deviceName">파싱할 디바이스 이름</param>
    /// <returns>Flow 이름 (첫 번째 세그먼트) 또는 구분자가 없으면 첫 레벨</returns>
    /// <example>
    /// extractFlowFromDeviceName "STN1_Work1__Device4_RET" = "STN1"
    /// extractFlowFromDeviceName "S141_SOL_INDEX_ADV" = "S141"
    /// extractFlowFromDeviceName "Device1_ADV" = "Device1"
    /// extractFlowFromDeviceName "Motor1" = "Motor1"
    /// </example>
    let extractFlowFromDeviceName (deviceName: string) : string =
        let parsed = PlcSymbolParser.parseSymbol deviceName
        PlcSymbolParser.getFlowName parsed

    /// <summary>
    /// 디바이스 이름에서 Work (두 번째 또는 세 번째 세그먼트) 추출
    /// PlcSymbolParser 사용하여 I/O 타입 이후 첫 번째 장비 이름 추출
    /// </summary>
    /// <param name="deviceName">파싱할 디바이스 이름</param>
    /// <returns>Work 이름 또는 없으면 "DEFAULT"</returns>
    /// <example>
    /// extractWorkFromDeviceName "STN1_Work1__Device4_RET" = "Work1"
    /// extractWorkFromDeviceName "S141_SOL_INDEX_ADV" = "INDEX"
    /// extractWorkFromDeviceName "Device1_ADV" = "ADV"
    /// extractWorkFromDeviceName "Motor1" = "DEFAULT"
    /// </example>
    let extractWorkFromDeviceName (deviceName: string) : string =
        let parsed = PlcSymbolParser.parseSymbol deviceName
        PlcSymbolParser.getWorkName parsed

    /// <summary>
    /// Allen-Bradley 하드웨어 주소에서 디바이스명 추출
    /// Pattern 1: Module:Slot:Type → Device_SlotX (I/O 타입 제외)
    /// Pattern 2: Module:Type → Module (슬롯 없는 경우)
    /// </summary>
    /// <param name="address">Allen-Bradley 주소</param>
    /// <returns>추출된 디바이스명 또는 None</returns>
    /// <example>
    /// extractABDeviceName "Local:12:O.Data.16" = Some "Device_Slot12"
    /// extractABDeviceName "Local:11:I.Data.5" = Some "Device_Slot11"
    /// extractABDeviceName "LOCAL_PNL:O.Data[6]" = Some "LOCAL_PNL"
    /// extractABDeviceName "SomeTag" = None
    /// </example>
    let extractABDeviceName (address: string) : string option =
        // Pattern 1: Module:Slot:Type (e.g., Local:12:O.Data.16)
        // Pattern 2: Module:Type (e.g., LOCAL_PNL:O.Data[6])
        let parts = address.Split([|':'|], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length >= 3 then
            // Pattern 1: Module:Slot:Type
            let moduleName = parts.[0]
            let slotNumber = parts.[1]

            // Normalize module name
            let modulePrefix =
                if moduleName.Equals("Local", StringComparison.OrdinalIgnoreCase) then "Device"
                else moduleName.Replace(":", "_")

            // Device name should NOT include I/O type to allow matching outputs with inputs on same slot
            Some (sprintf "%s_Slot%s" modulePrefix slotNumber)
        elif parts.Length = 2 then
            // Pattern 2: Module:Type (without slot number)
            let moduleName = parts.[0]
            let typeInfo = parts.[1]

            // Only accept if typeInfo starts with I, O, C, or S (valid AB I/O types)
            if typeInfo.StartsWith("O") || typeInfo.StartsWith("I") ||
               typeInfo.StartsWith("C") || typeInfo.StartsWith("S") then
                Some moduleName
            else
                None
        else
            None

    /// <summary>
    /// 변수 이름에서 디바이스 이름 추출 (I/O 표시자와 API 제거)
    /// Allen-Bradley 하드웨어 주소는 특별 처리
    /// MappingSet의 OutputKeywords/InputKeywords를 사용해서 I/O 표시자 제거
    /// WorkSplitDepth 설정에 따라 Work 분할 깊이 결정:
    ///   - 0: 자동 (CompoundSuffixes 기반으로 API 분리)
    ///   - N>0: 앞에서 N개 토큰을 Device로 사용 (나머지는 API)
    /// </summary>
    /// <param name="tagName">전체 변수 이름</param>
    /// <param name="mappingSets">API 키워드 정의가 포함된 MappingSet 목록</param>
    /// <returns>Device 이름 (I/O 표시자와 API 제거)</returns>
    /// <example>
    /// extractDeviceNameFromTag "STN1_Device1_ADV_O" mappingSets = "STN1_Device1"
    /// extractDeviceNameFromTag "Local:12:O.Data.16" mappingSets = "Device_Slot12"
    /// WorkSplitDepth=3 시: "S141_SOL_INDEX_ADV" → "S141_SOL_INDEX"
    /// </example>
    let extractDeviceNameFromTag (tagName: string) (_mappingSets: MappingSet list) : string =
        // First try Allen-Bradley hardware address extraction
        match extractABDeviceName tagName with
        | Some deviceName -> deviceName
        | None ->
            // Fall back to standard parsing
            let parsed = PlcSymbolParser.parseSymbol tagName

            // Remove leading LS QX/IX tokens and trailing generic I/O tokens.
            let levelsWithoutIOType = trimBoundaryIOTypeTokens parsed.Levels

            // 1단계: CompoundSuffixes 기반으로 API 토큰 수 계산
            // 2단계: WorkSplitDepth(최대 깊이)가 설정되면 Device 깊이 제한
            let rec countApiTokens (tokens: string list) (count: int) : int =
                if tokens.Length <= 1 then
                    count
                else
                    let lastToken = tokens |> List.last
                    if isCompoundSuffix lastToken then
                        countApiTokens (tokens |> List.take (tokens.Length - 1)) (count + 1)
                    else
                        count

            let apiTokenCount =
                if levelsWithoutIOType.Length >= 2 then
                    let initialCount = countApiTokens levelsWithoutIOType 0
                    max 1 initialCount
                else 1

            // CompoundSuffixes 기반 Device 토큰 수
            let autoDeviceCount = levelsWithoutIOType.Length - apiTokenCount

            // WorkSplitDepth(최대 깊이) 적용
            let maxDepth = getWorkSplitDepth()
            let finalDeviceCount =
                if maxDepth > 0 && autoDeviceCount > maxDepth then
                    maxDepth  // 최대 깊이로 제한
                else
                    autoDeviceCount  // CompoundSuffixes 결과 그대로

            let deviceLevels =
                if finalDeviceCount > 0 && levelsWithoutIOType.Length > finalDeviceCount then
                    levelsWithoutIOType |> List.take finalDeviceCount
                elif finalDeviceCount > 0 then
                    levelsWithoutIOType |> List.take (min finalDeviceCount levelsWithoutIOType.Length)
                else
                    levelsWithoutIOType

            if deviceLevels.IsEmpty then
                tagName
            else
                String.Join("_", deviceLevels)

    /// <summary>
    /// 디바이스 접두사가 주어진 태그 이름에서 API 추출
    /// MappingSet의 OutputKeywords/InputKeywords를 사용해서 I/O 표시자 제거
    /// PlcSymbolParser로 구조 파싱 후 I/O 표시자를 제거하고 API 추출
    /// 설정에 없는 API는 맨 끝 접미사를 API로 처리
    /// </summary>
    /// <param name="tagName">전체 변수 이름</param>
    /// <param name="mappingSets">API 키워드 정의가 포함된 MappingSet 목록</param>
    /// <returns>API 이름 (I/O 표시자 제거 후 추출된 API)</returns>
    /// <example>
    /// extractApiFromTag "STN1_Device1_ADV_O" mappingSets = "ADV"
    /// extractApiFromTag "STN1_Device1_RET_I" mappingSets = "RET"
    /// extractApiFromTag "Device1_START" mappingSets = "START"
    /// extractApiFromTag "GB03_SDS_EXHAUST_OFF" mappingSets = "OFF"
    /// </example>
    let extractApiFromTag (tagName: string) (_mappingSets: MappingSet list) : string =
        let parsed = PlcSymbolParser.parseSymbol tagName

        // Remove leading LS QX/IX tokens and trailing generic I/O tokens.
        let levelsWithoutIOType = trimBoundaryIOTypeTokens parsed.Levels

        // 1단계: CompoundSuffixes 기반으로 API 토큰 수 계산
        // 2단계: WorkSplitDepth(최대 깊이)가 설정되면 API 범위 확장
        let rec countApiTokens (tokens: string list) (count: int) : int =
            if tokens.Length <= 1 then
                count
            else
                let lastToken = tokens |> List.last
                if isCompoundSuffix lastToken then
                    countApiTokens (tokens |> List.take (tokens.Length - 1)) (count + 1)
                else
                    count

        if levelsWithoutIOType.IsEmpty then
            Constants.DefaultApi
        elif levelsWithoutIOType.Length >= 2 then
            let suffixCount = countApiTokens levelsWithoutIOType 0
            let autoApiCount = max 1 suffixCount

            // CompoundSuffixes 기반 Device 토큰 수
            let autoDeviceCount = levelsWithoutIOType.Length - autoApiCount

            // WorkSplitDepth(최대 깊이) 적용 - Device가 최대 깊이 초과시 API 범위 확장
            let maxDepth = getWorkSplitDepth()
            let finalApiCount =
                if maxDepth > 0 && autoDeviceCount > maxDepth then
                    levelsWithoutIOType.Length - maxDepth  // Device를 최대 깊이로 제한하면 API가 늘어남
                else
                    autoApiCount

            let apiParts = levelsWithoutIOType |> List.skip (levelsWithoutIOType.Length - finalApiCount)
            String.Join("_", apiParts)
        elif levelsWithoutIOType.Length = 1 then
            levelsWithoutIOType |> List.last
        else
            Constants.DefaultApi

    /// <summary>
    /// 변수 이름에 유효한 세그먼트가 있는지 확인 (구분자 포함)
    /// </summary>
    /// <param name="name">확인할 변수 이름</param>
    /// <returns>이름에 언더스코어 또는 공백 구분자가 있으면 True</returns>
    let hasValidSegments (name: string) : bool =
        name.Contains("_") || name.Contains(" ")

    /// <summary>
    /// Allen-Bradley 하드웨어 주소에서 I/O 타입 감지
    /// Local:Slot:Type 또는 Module:Slot:Type 형식에서 :O. 또는 :I. 패턴 감지
    /// </summary>
    /// <param name="address">Allen-Bradley 주소 (예: "Local:12:O.Data.16")</param>
    /// <returns>Output이면 Some true, Input이면 Some false, 감지 실패 시 None</returns>
    /// <example>
    /// detectABIOType "Local:12:O.Data.16" = Some true  // Output
    /// detectABIOType "Local:11:I.Data.5" = Some false  // Input
    /// detectABIOType "CNET_2:O.Slot[8].Data.5" = Some true
    /// detectABIOType "AB_1:I.Data[0]" = Some false
    /// detectABIOType "SomeTag" = None
    /// </example>
    let detectABIOType (address: string) : bool option =
        if address.Contains(":O.") || address.Contains(":O[") then
            Some true  // Output
        elif address.Contains(":I.") || address.Contains(":I[") then
            Some false  // Input
        elif address.Contains(":C.") || address.Contains(":S.") then
            // Configuration or Status - treat as input by default
            Some false
        else
            None

    /// <summary>
    /// 물리 주소가 유효한 출력 주소인지 확인 (MappingSet 패턴 기반)
    /// Allen-Bradley 주소는 :O. 패턴으로 명확하게 구분
    /// </summary>
    /// <param name="mappingSets">출력 주소 패턴이 정의된 MappingSet 목록</param>
    /// <param name="address">확인할 물리 주소</param>
    /// <returns>OutputAddressPatterns 중 하나로 시작하면 True</returns>
    /// <example>
    /// isOutputAddress mappingSets "%Q0.0" = true   // %Q로 시작
    /// isOutputAddress mappingSets "Local:12:O.Data.16" = true
    /// isOutputAddress mappingSets "Local:11:I.Data.5" = false
    /// </example>
    let isOutputAddress (mappingSets: MappingSet list) (address: string) : bool =
        // First check Allen-Bradley specific patterns (:O. or :I.)
        match detectABIOType address with
        | Some isOutput -> isOutput
        | None ->
            // Fall back to pattern matching
            mappingSets
            |> List.collect (fun ms -> ms.OutputAddressPatterns)
            |> List.distinct
            |> List.exists (fun pattern ->
                // Exclude generic "Local:" pattern to avoid ambiguity
                if pattern = "Local:" then false
                else
                    // Remove trailing wildcard '*' for glob-style patterns
                    let cleanPattern = pattern.TrimEnd('*')
                    address.StartsWith(cleanPattern, StringComparison.OrdinalIgnoreCase))

    // ============================================================================
    // NodeConnectionRules 설정 - Export 시 노드 자동 연결에 사용
    // ============================================================================

    /// 스텝 그룹: 단일 패턴 또는 병렬 실행되는 패턴들
    /// 예: "START" → Single "START"
    /// 예: "[SV_1ST,B_1ST]" → Group ["SV_1ST"; "B_1ST"]
    type StepGroup =
        | Single of string
        | Group of string list

    type CallSequenceRule = {
        Name: string
        Steps: StepGroup list  // 파싱된 스텝 그룹들
        Type: string           // StartReset, Start, Reset, Interlock
    }

    /// Steps 문자열을 StepGroup 리스트로 파싱
    /// 예: "START,[SV_1ST,B_1ST],2ND,WORK_COMP"
    ///   → [Single "START"; Group ["SV_1ST"; "B_1ST"]; Single "2ND"; Single "WORK_COMP"]
    let private parseSteps (stepsStr: string) : StepGroup list =
        let mutable result = []
        let mutable i = 0
        let mutable currentToken = ""

        while i < stepsStr.Length do
            let c = stepsStr.[i]
            match c with
            | '[' ->
                // 그룹 시작 - ']'까지 읽기
                let closeBracket = stepsStr.IndexOf(']', i)
                if closeBracket > i then
                    let groupContent = stepsStr.Substring(i + 1, closeBracket - i - 1)
                    let items = groupContent.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                                |> Array.map (fun s -> s.Trim())
                                |> Array.toList
                    result <- result @ [Group items]
                    i <- closeBracket + 1
                else
                    i <- i + 1
            | ',' ->
                // 구분자 - 현재 토큰 저장
                if currentToken.Trim().Length > 0 then
                    result <- result @ [Single (currentToken.Trim())]
                currentToken <- ""
                i <- i + 1
            | _ ->
                currentToken <- currentToken + string c
                i <- i + 1

        // 마지막 토큰 저장
        if currentToken.Trim().Length > 0 then
            result <- result @ [Single (currentToken.Trim())]

        result

    type WorkConnectionRule = {
        FromPattern: string
        ToPattern: string
        Type: string   // StartReset, Start, Reset, Interlock
        Mode: string   // Sequential, Parallel
    }

    type DevicePairRule = {
        Device1: string
        Device2: string
        Type: string   // Interlock, StartReset, etc.
    }

    type AutoPreRule = {
        SourcePattern: string
        TargetPattern: string
        Type: string   // Interlock, StartReset, etc.
    }

    type NodeConnectionRules = {
        CallSequences: CallSequenceRule list
        WorkConnections: WorkConnectionRule list
        DevicePairs: DevicePairRule list
        AutoPreRules: AutoPreRule list
    }

    let mutable private cachedNodeConnectionRules : NodeConnectionRules option = None

    let private loadNodeConnectionRules () : NodeConnectionRules =
        let configPath = currentConfigPath
        if not (File.Exists configPath) then
            { CallSequences = []; WorkConnections = []; DevicePairs = []; AutoPreRules = [] }
        else
            try
                let json = File.ReadAllText(configPath)
                let jobj = JObject.Parse(json)
                let rules = jobj.["NodeConnectionRules"]
                if isNull rules then
                    { CallSequences = []; WorkConnections = []; DevicePairs = []; AutoPreRules = [] }
                else
                    let callSeqs =
                        match rules.["CallSequences"] with
                        | null -> []
                        | token ->
                            token
                            |> Seq.cast<JToken>
                            |> Seq.map (fun cs ->
                                let stepsStr = cs.["Steps"] |> string
                                {
                                    Name = cs.["Name"] |> string
                                    Steps = parseSteps stepsStr  // 그룹 문법 지원
                                    Type = cs.["Type"] |> string
                                })
                            |> Seq.toList

                    let workConns =
                        match rules.["WorkConnections"] with
                        | null -> []
                        | token ->
                            token
                            |> Seq.cast<JToken>
                            |> Seq.map (fun wc ->
                                {
                                    FromPattern = wc.["FromPattern"] |> string
                                    ToPattern = wc.["ToPattern"] |> string
                                    Type = wc.["Type"] |> string
                                    Mode = wc.["Mode"] |> string
                                })
                            |> Seq.toList

                    let devicePairs =
                        match rules.["DevicePairs"] with
                        | null -> []
                        | token ->
                            token
                            |> Seq.cast<JToken>
                            |> Seq.map (fun dp ->
                                {
                                    Device1 = dp.["Device1"] |> string
                                    Device2 = dp.["Device2"] |> string
                                    Type = dp.["Type"] |> string
                                })
                            |> Seq.toList

                    let autoPreRules =
                        match rules.["AutoPreRules"] with
                        | null -> []
                        | token ->
                            token
                            |> Seq.cast<JToken>
                            |> Seq.map (fun ap ->
                                {
                                    SourcePattern = ap.["SourcePattern"] |> string
                                    TargetPattern = ap.["TargetPattern"] |> string
                                    Type = ap.["Type"] |> string
                                })
                            |> Seq.toList

                    { CallSequences = callSeqs; WorkConnections = workConns; DevicePairs = devicePairs; AutoPreRules = autoPreRules }
            with _ ->
                { CallSequences = []; WorkConnections = []; DevicePairs = []; AutoPreRules = [] }

    let getNodeConnectionRules () : NodeConnectionRules =
        lock cacheSync (fun () ->
            match cachedNodeConnectionRules with
            | Some cached -> cached
            | None ->
                let loaded = loadNodeConnectionRules()
                cachedNodeConnectionRules <- Some loaded
                loaded)

    let invalidateNodeConnectionRulesCache () : unit =
        lock cacheSync (fun () -> cachedNodeConnectionRules <- None)

    /// <summary>
    /// 물리 주소가 유효한 입력 주소인지 확인 (MappingSet 패턴 기반)
    /// Allen-Bradley 주소는 :I. 패턴으로 명확하게 구분
    /// </summary>
    /// <param name="mappingSets">입력 주소 패턴이 정의된 MappingSet 목록</param>
    /// <param name="address">확인할 물리 주소</param>
    /// <returns>InputAddressPatterns 중 하나로 시작하면 True</returns>
    /// <example>
    /// isInputAddress mappingSets "%I0.0" = true    // %I로 시작
    /// isInputAddress mappingSets "Local:11:I.Data.5" = true
    /// isInputAddress mappingSets "Local:12:O.Data.16" = false
    /// </example>
    let isInputAddress (mappingSets: MappingSet list) (address: string) : bool =
        // First check Allen-Bradley specific patterns (:O. or :I.)
        match detectABIOType address with
        | Some isOutput -> not isOutput  // If it's output, it's not input
        | None ->
            // Fall back to pattern matching
            mappingSets
            |> List.collect (fun ms -> ms.InputAddressPatterns)
            |> List.distinct
            |> List.exists (fun pattern ->
                // Exclude generic "Local:" pattern to avoid ambiguity
                if pattern = "Local:" then false
                else
                    // Remove trailing wildcard '*' for glob-style patterns
                    let cleanPattern = pattern.TrimEnd('*')
                    address.StartsWith(cleanPattern, StringComparison.OrdinalIgnoreCase))

    // ============================================================================
    // DisplayNaming 설정 - UI 표시용 간결한 이름 생성 (단일 토큰 설정으로 통합)
    // ============================================================================

    /// 기본 RemoveTokens (config 파일이 없을 때 사용)
    let private defaultRemoveTokens =
        [| "SOL"; "WRS"; "RS"; "LS" |]

    let mutable private cachedDisplayNamingConfig : HashSet<string> option = None
    let private displayNamingSync = obj()

    /// DisplayNaming 설정 로드 - 단일 RemoveTokens 필드 사용
    /// Returns: RemoveTokens set (Flow 접두사, I/O 타입, 디바이스 타입 통합)
    let private loadDisplayNamingConfig () : HashSet<string> =
        let configPath = currentConfigPath
        if not (File.Exists configPath) then
            HashSet<string>(defaultRemoveTokens, StringComparer.OrdinalIgnoreCase)
        else
            try
                let json = File.ReadAllText(configPath)
                let jobj = JObject.Parse(json)
                let displayNaming = jobj.["DisplayNaming"]
                if isNull displayNaming then
                    HashSet<string>(defaultRemoveTokens, StringComparer.OrdinalIgnoreCase)
                else
                    // 새 형식 (RemoveTokens) 우선 사용
                    let removeTokens = displayNaming.["RemoveTokens"]
                    if not (isNull removeTokens) then
                        let arr = removeTokens.ToObject<string[]>() |> Array.map (fun s -> s.ToUpperInvariant())
                        HashSet<string>(arr, StringComparer.OrdinalIgnoreCase)
                    else
                        // 기존 형식 마이그레이션: 3개 필드 병합
                        let mergedTokens = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        let addTokens (node: JToken) =
                            if not (isNull node) then
                                let arr = node.ToObject<string[]>()
                                for t in arr do
                                    mergedTokens.Add(t.ToUpperInvariant()) |> ignore
                        addTokens (displayNaming.["RemoveFlowPrefixes"])
                        addTokens (displayNaming.["RemoveIOTypeTokensForDisplay"])
                        addTokens (displayNaming.["RemoveDeviceTypeTokens"])
                        if mergedTokens.Count = 0 then
                            HashSet<string>(defaultRemoveTokens, StringComparer.OrdinalIgnoreCase)
                        else
                            mergedTokens
            with _ ->
                HashSet<string>(defaultRemoveTokens, StringComparer.OrdinalIgnoreCase)

    /// DisplayNaming 설정 가져오기 (캐시됨)
    let getDisplayNamingConfig () : HashSet<string> =
        lock displayNamingSync (fun () ->
            match cachedDisplayNamingConfig with
            | Some cached -> cached
            | None ->
                let loaded = loadDisplayNamingConfig()
                cachedDisplayNamingConfig <- Some loaded
                loaded)

    /// DisplayNaming 캐시 무효화
    let invalidateDisplayNamingCache () : unit =
        lock displayNamingSync (fun () -> cachedDisplayNamingConfig <- None)

    /// <summary>
    /// Device 이름에서 UI 표시용 DisplayName 생성
    /// 설정된 토큰(Flow 접두사, I/O 타입, 디바이스 타입)을 이름 앞부분에서 순차적으로 제거
    /// </summary>
    /// <param name="deviceName">원본 Device 이름 (예: "S141_SOL_CT_CLP")</param>
    /// <returns>간결한 DisplayName (예: "CT_CLP")</returns>
    /// <example>
    /// getDisplayName "S141_SOL_CT_CLP" = "CT_CLP"
    /// getDisplayName "S141_WRS_CT_CLP" = "CT_CLP"
    /// getDisplayName "S141_Q_RB1" = "RB1"
    /// getDisplayName "S141_SOL-R_FLR_PIN" = "R_FLR_PIN"
    /// getDisplayName "Device1" = "Device1"
    /// </example>
    let getDisplayName (deviceName: string) : string =
        if String.IsNullOrWhiteSpace(deviceName) then deviceName
        else
            let removeTokens = getDisplayNamingConfig()

            // 토큰이 제거 대상인지 확인
            let shouldRemoveToken (token: string) : bool =
                removeTokens.Contains(token.ToUpperInvariant())

            // 언더스코어로 분리된 세그먼트들 처리
            // 각 세그먼트는 하이픈으로 추가 분리될 수 있음 (예: "SOL-R" → ["SOL"; "R"])
            let segments = deviceName.Split([|'_'|], StringSplitOptions.RemoveEmptyEntries) |> Array.toList

            let processedSegments =
                segments
                |> List.map (fun segment ->
                    // 하이픈으로 분리
                    if segment.Contains("-") then
                        let parts = segment.Split([|'-'|], StringSplitOptions.RemoveEmptyEntries)
                        let filteredParts = parts |> Array.filter (fun p -> not (shouldRemoveToken p))
                        if filteredParts.Length = 0 then None
                        else Some (String.Join("-", filteredParts))
                    else
                        // 하이픈 없는 일반 토큰
                        if shouldRemoveToken segment then None
                        else Some segment)
                |> List.choose id

            if processedSegments.IsEmpty then
                deviceName  // 모든 토큰이 제거되면 원본 반환
            else
                String.Join("_", processedSegments)

    /// <summary>
    /// 전체 Tag 이름(Device.API 형식)에서 UI 표시용 DisplayName 생성
    /// </summary>
    /// <param name="fullName">전체 이름 (예: "S141_SOL_CT_CLP.ADV")</param>
    /// <returns>간결한 DisplayName (예: "CT_CLP.ADV")</returns>
    let getDisplayNameForFullTag (fullName: string) : string =
        if String.IsNullOrWhiteSpace(fullName) then fullName
        else
            let parts = fullName.Split([|'.'|], 2)
            if parts.Length = 2 then
                let devicePart = getDisplayName parts.[0]
                sprintf "%s.%s" devicePart parts.[1]
            else
                getDisplayName fullName
