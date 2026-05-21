namespace Ds2.SymbolImport.Matching

open System
open System.IO
open System.Text.RegularExpressions
open Newtonsoft.Json

/// <summary>
/// 통합 InputMatching 모듈 - 단어 유사도 기반 매칭
/// 성능과 유지보수성을 위해 단순화 및 최적화
/// </summary>
module InputMatching =

    // ============================================================================
    // 타입 정의
    // ============================================================================

    type InputOutputMatch = {
        DeviceName: string
        OutputApi: string
        OutputVariable: Variable
        InputApis: string list
        InputVariables: Variable list
        Strategy: MatchingStrategy
        SimilarityScore: float
        Reason: string
    }

    // ============================================================================
    // 단어 유사도 계산
    // ============================================================================

    /// <summary>
    /// Jaccard 유사도 계산 (문자 집합 기반)
    /// </summary>
    let private calculateJaccardSimilarity (s1: string) (s2: string) : float =
        let set1 = s1.ToUpperInvariant() |> Set.ofSeq
        let set2 = s2.ToUpperInvariant() |> Set.ofSeq
        let intersection = Set.intersect set1 set2 |> Set.count |> float
        let union = Set.union set1 set2 |> Set.count |> float
        if union = 0.0 then 0.0 else intersection / union

    /// <summary>
    /// 공통 부분 문자열 비율 계산
    /// </summary>
    let private calculateCommonSubstringRatio (s1: string) (s2: string) : float =
        let str1 = s1.ToUpperInvariant()
        let str2 = s2.ToUpperInvariant()
        let minLen = min str1.Length str2.Length
        if minLen = 0 then 0.0
        else
            let mutable commonCount = 0
            for i = 0 to minLen - 1 do
                if str1.[i] = str2.[i] then commonCount <- commonCount + 1
            float commonCount / float minLen

    /// <summary>
    /// 단어 유사도 계산 (Jaccard + 공통 부분 문자열 가중 평균)
    /// </summary>
    let private calculateSimilarity (pattern: string) (target: string) : float =
        let jaccard = calculateJaccardSimilarity pattern target
        let substring = calculateCommonSubstringRatio pattern target
        (jaccard * 0.6) + (substring * 0.4)  // Jaccard 60%, 공통 부분 문자열 40%

    // ============================================================================
    // 핵심 매칭 로직
    // ============================================================================

    /// 성능 최적화: Regex 대신 단순 문자열 매칭 사용
    /// 패턴: *text* → Contains, text* → StartsWith, *text → EndsWith, text → Equals
    let private matchesKeyword (keyword: string) (name: string) =
        let kw = keyword.Trim()
        let startsWithStar = kw.StartsWith("*")
        let endsWithStar = kw.EndsWith("*")
        let core = kw.Trim('*')

        if String.IsNullOrEmpty(core) then true
        elif startsWithStar && endsWithStar then
            // *text* → Contains
            name.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0
        elif startsWithStar then
            // *text → EndsWith
            name.EndsWith(core, StringComparison.OrdinalIgnoreCase)
        elif endsWithStar then
            // text* → StartsWith
            name.StartsWith(core, StringComparison.OrdinalIgnoreCase)
        else
            // text → Equals
            name.Equals(core, StringComparison.OrdinalIgnoreCase)

    let private matchesAnyKeyword (keywords: string list) (name: string) =
        keywords |> List.exists (fun kw -> matchesKeyword kw name)

    /// 성능 최적화: apiDict 파라미터 추가 (extractApiFromTag 사전 계산)
    /// 이전: 각 매칭마다 extractApiFromTag 호출 O(매칭 수 × extractApiFromTag 비용)
    /// 이후: apiDict에서 O(1) 조회
    let private findMatchingInputs trie (inputDict: Map<string, Variable>) (apiDict: Map<string, string>) (pattern: string) =
        let upper = pattern.ToUpperInvariant()

        // 1. 정확한 prefix 매칭 시도
        let exactMatches =
            PrefixTrie.findAllWithPrefix trie upper
            |> List.choose (fun name ->
                if name.Length = upper.Length || name.Substring(upper.Length) |> Seq.forall Char.IsDigit then
                    match inputDict |> Map.tryFind name with
                    | Some v ->
                        // 성능 최적화: apiDict에서 O(1) 조회 (이전: extractApiFromTag 호출)
                        let apiName = apiDict |> Map.tryFind name |> Option.defaultValue ""
                        Some (apiName, v, 1.0)  // 정확한 매칭은 유사도 1.0
                    | None -> None
                else None)

        // 정확한 매칭이 있으면 바로 반환
        if not exactMatches.IsEmpty then
            exactMatches |> List.map (fun (api, v, _) -> (api, v)) |> List.distinctBy (snd >> fun v -> v.PhysicalAddress)
        else
            // 2. Fuzzy matching: 선택적 중간 단어 제거 패턴 시도
            let patternsToTry =
                [upper]  // 원본 패턴만 사용 (fuzzy matching 비활성화)

            let fuzzyMatches =
                patternsToTry
                |> List.distinct
                |> List.collect (fun p ->
                    PrefixTrie.findAllWithPrefix trie p
                    |> List.choose (fun name ->
                        if name.Length = p.Length || name.Substring(p.Length) |> Seq.forall Char.IsDigit then
                            match inputDict |> Map.tryFind name with
                            | Some v ->
                                // 성능 최적화: apiDict에서 O(1) 조회 (이전: extractApiFromTag 호출)
                                let apiName = apiDict |> Map.tryFind name |> Option.defaultValue ""
                                let similarity = calculateSimilarity upper name
                                if similarity >= 0.6 then  // 60% 이상 유사도만 허용
                                    Some (apiName, v, similarity)
                                else None
                            | None -> None
                        else None))
                |> List.distinctBy (fun (_, v, _) -> v.PhysicalAddress)
                |> List.sortByDescending (fun (_, _, sim) -> sim)
                |> List.map (fun (api, v, _) -> (api, v))

            fuzzyMatches

    /// MappingSet 기반 매칭
    let private matchByMappingSet trie inputDict apiDict mappingSets (deviceName: string) outputApi outputVar =
        // 모든 MappingSet에서 매칭 시도하고 유사도 점수 계산
        let allMatches =
            mappingSets
            |> List.choose (fun ms ->
                if not (matchesAnyKeyword ms.DeviceKeywords deviceName) then None
                else
                    // 먼저 API 이름으로 직접 매칭 시도
                    let directMatch = ms.Apis.TryFind(outputApi)

                    // 직접 매칭 실패 시, 모든 Apis의 OutputKeywords로 전체 변수명 패턴 교체 시도
                    let patternMatch =
                        if directMatch.IsNone then
                            ms.Apis
                            |> Map.toList
                            |> List.tryPick (fun (_, apiMap) ->
                                // 출력 변수명에서 OutputKeywords 중 하나가 포함되어 있는지 확인
                                // AB 주소의 경우 (outputVar.PhysicalAddress에 ":" 포함) Prefix 매칭도 지원
                                let isABAddress = outputVar.PhysicalAddress.Contains(":")
                                let hasOutputKeyword =
                                    apiMap.OutputKeywords
                                    |> List.exists (fun kw ->
                                        if isABAddress then
                                            // AB 주소: Prefix 매칭 (예: O0313 starts with O)
                                            outputVar.LogicalName.StartsWith(kw, StringComparison.OrdinalIgnoreCase)
                                        else
                                            // 일반 주소: 기존 로직 (언더스코어 포함)
                                            outputVar.LogicalName.Contains("_" + kw + "_") ||
                                            outputVar.LogicalName.Contains("_" + kw))
                                if hasOutputKeyword then Some apiMap else None)
                        else None

                    let apiMapOpt = if directMatch.IsSome then directMatch else patternMatch

                    apiMapOpt
                    |> Option.bind (fun apiMap ->
                        // 성능 최적화: 모든 패턴을 먼저 생성하고 중복 제거 후 한 번에 매칭
                        // 이전: O(InputKeywords × OutputKeywords × patterns) 호출
                        // 이후: O(uniquePatterns) 호출
                        let allPatterns =
                            apiMap.InputKeywords
                            |> List.collect (fun inputKw ->
                                apiMap.OutputKeywords
                                |> List.collect (fun outputKw ->
                                    // 4가지 패턴 생성:
                                    // 1. Suffix 패턴: "_OutputKeyword"를 "_InputKeyword"로 치환 (예: _O → _I)
                                    let suffixPattern = outputVar.LogicalName.Replace("_" + outputKw, "_" + inputKw, StringComparison.OrdinalIgnoreCase)

                                    // 2. Prefix 패턴: "OutputKeyword"로 시작하면 "InputKeyword"로 치환 (예: O0313 → I0313)
                                    let prefixPattern =
                                        if outputVar.LogicalName.StartsWith(outputKw, StringComparison.OrdinalIgnoreCase) then
                                            Some (inputKw + outputVar.LogicalName.Substring(outputKw.Length))
                                        else None

                                    // 3. 디바이스 타입 치환 패턴: 비활성화됨
                                    let deviceTypePattern = None

                                    // 4. Fallback: deviceName + InputKeyword + API
                                    let fallbackPattern = deviceName + "_" + inputKw + "_" + outputApi

                                    [Some suffixPattern; prefixPattern; deviceTypePattern; Some fallbackPattern]
                                    |> List.choose id))
                            |> List.map (fun p -> p.ToUpperInvariant())
                            |> List.distinct  // 중복 패턴 제거

                        // 성능 최적화: findMatchingInputs 사용 (apiDict O(1) 조회)
                        let matches =
                            allPatterns
                            |> List.collect (fun pattern -> findMatchingInputs trie inputDict apiDict pattern)
                            |> List.distinctBy (snd >> fun v -> v.PhysicalAddress)

                        if matches.IsEmpty then None
                        else
                            // 유사도 점수 계산: 패턴과 실제 매칭된 변수 이름들의 평균 유사도
                            let similarityScore =
                                matches
                                |> List.map (fun (apiName, var) ->
                                    let expectedPattern = deviceName + "_" + apiName
                                    calculateSimilarity expectedPattern var.LogicalName)
                                |> fun scores -> if scores.IsEmpty then 0.0 else List.average scores

                            let outputKwStr = String.concat "/" apiMap.OutputKeywords
                            Some {
                                DeviceName = deviceName
                                OutputApi = outputApi
                                OutputVariable = outputVar
                                InputApis = matches |> List.map fst
                                InputVariables = matches |> List.map snd
                                Strategy = MappingSet
                                SimilarityScore = similarityScore
                                Reason = sprintf "MappingSet '%s': %s → %A (%d개 입력, 유사도 %.2f)" ms.Name outputKwStr apiMap.InputKeywords matches.Length similarityScore
                            }))

        // 가장 높은 유사도 점수를 가진 매칭 선택
        allMatches
        |> List.sortByDescending (fun m -> m.SimilarityScore)
        |> List.tryHead

    /// 유사도 기반 Input 매칭 (패턴 매칭 실패 시)
    /// O(N) 전체 순회 대신 O(디바이스별 입력 수) 조회
    let private matchBySimilarity (deviceToInputs: Map<string, (string * Variable) list>) outputVarName deviceName =
        match deviceToInputs.TryFind(deviceName) with
        | None -> []
        | Some inputs ->
            inputs
            |> List.choose (fun (name, var) ->
                let sim = calculateSimilarity outputVarName name
                if sim >= 0.5 then Some (name, var, sim) else None)
            |> List.sortByDescending (fun (_, _, sim) -> sim)
            |> List.truncate 3

    // ============================================================================
    // 공개 API
    // ============================================================================

    let isMatchSuccessful m = not m.InputApis.IsEmpty

    let getMatchingStatistics (matches: InputOutputMatch list) =
        let total = matches.Length
        let matched = matches |> List.filter isMatchSuccessful |> List.length
        (total, matched, total - matched, if total > 0 then matches |> List.averageBy (fun m -> m.SimilarityScore) else 0.0)

    let getSuccessfulMatches = List.filter isMatchSuccessful
    let getFailedMatches = List.filter (isMatchSuccessful >> not)

    // ============================================================================
    // JSON 설정 (메이커별 구성)
    // ============================================================================

    [<CLIMutable>]
    type JsonApiMapping =
        { Name: string
          OutputKeywords: string[]
          InputKeywords: string[] }

    [<CLIMutable>]
    type JsonMappingSetBase =
        { Name: string
          DeviceKeywords: string[]
          Apis: JsonApiMapping[] }

    [<CLIMutable>]
    type JsonCommonSection =
        { MappingSets: JsonMappingSetBase[] }

    [<CLIMutable>]
    type JsonVendorConfig =
        { OutputAddressPatterns: string[]
          InputAddressPatterns: string[]
          MappingSets: JsonMappingSetBase[] }

    [<CLIMutable>]
    type JsonVendors =
        { LS: JsonVendorConfig
          AB: JsonVendorConfig
          Mitsubishi: JsonVendorConfig
          Siemens: JsonVendorConfig }

    [<CLIMutable>]
    type JsonConfig =
        { Common: JsonCommonSection
          Vendors: JsonVendors }

    // ============================================================================
    // 후보 생성용 JSON 타입 정의
    // ============================================================================

    /// <summary>
    /// 대칭 규칙 - 패턴 치환으로 매칭 후보 생성
    /// </summary>
    [<CLIMutable>]
    type JsonSymmetryRule =
        { OutputPattern: string
          InputPattern: string
          Description: string }

    /// <summary>
    /// Level 7: 명시적 매핑 - 특수 패턴 직접 매핑
    /// </summary>
    [<CLIMutable>]
    type JsonExplicitMapping =
        { OutputKeyword: string
          InputKeyword: string
          Description: string }

    // ============================================================================
    // WorkNaming JSON 타입 정의
    // ============================================================================

    /// <summary>
    /// Flow 이름 추출 설정
    /// </summary>
    [<CLIMutable>]
    type JsonFlowExtraction =
        { Description: string
          SplitChar: string
          PartIndex: int }

    /// <summary>
    /// Work 그룹핑 패턴
    /// </summary>
    [<CLIMutable>]
    type JsonWorkGroupingPattern =
        { Pattern: string
          GroupFormat: string
          Description: string }

    /// <summary>
    /// 기본 그룹핑 설정
    /// </summary>
    [<CLIMutable>]
    type JsonDefaultGrouping =
        { Description: string
          SplitChar: string
          PartIndex: int }

    /// <summary>
    /// Work 이름 생성 설정
    /// </summary>
    [<CLIMutable>]
    type JsonWorkNaming =
        { Description: string
          FlowExtraction: JsonFlowExtraction
          WorkGrouping: JsonWorkGroupingPattern[]
          DefaultGrouping: JsonDefaultGrouping
          WorkNameFormat: string }

    /// <summary>
    /// API 이름 추출 설정
    /// </summary>
    [<CLIMutable>]
    type JsonApiNaming =
        { Description: string
          CompoundSuffixes: string[] }

    /// <summary>
    /// 후보 생성에 필요한 간소화된 JSON 설정
    /// </summary>
    [<CLIMutable>]
    type JsonCandidateConfig =
        { Common: JsonCommonSection
          Vendors: JsonVendors
          SymmetryRules: JsonSymmetryRule[]
          ExplicitMappings: JsonExplicitMapping[]
          WorkNaming: JsonWorkNaming
          ApiNaming: JsonApiNaming }

    // ============================================================================
    // 후보 생성용 도메인 모델 (간소화)
    // ============================================================================

    /// <summary>
    /// 대칭 규칙 (파싱된 형태)
    /// </summary>
    type SymmetryRule =
        { OutputPattern: string
          InputPattern: string
          Description: string }

    /// <summary>
    /// 명시적 매핑 (파싱된 형태)
    /// </summary>
    type ExplicitMappingRule =
        { OutputKeyword: string
          InputKeyword: string
          Description: string }

    /// <summary>
    /// 후보 생성 설정 (간소화된 형태)
    /// </summary>
    type CandidateGenerationConfig =
        { SymmetryRules: SymmetryRule list
          ExplicitMappings: ExplicitMappingRule list }

    // ============================================================================
    // WorkNaming 도메인 모델
    // ============================================================================

    /// <summary>
    /// Flow 이름 추출 설정 (파싱된 형태)
    /// </summary>
    type FlowExtractionConfig =
        { SplitChar: char
          PartIndex: int }

    /// <summary>
    /// Work 그룹핑 패턴 (파싱된 형태)
    /// </summary>
    type WorkGroupingPattern =
        { Pattern: Regex
          GroupFormat: string
          Description: string }

    /// <summary>
    /// 기본 그룹핑 설정 (파싱된 형태)
    /// </summary>
    type DefaultGroupingConfig =
        { SplitChar: char
          PartIndex: int }

    /// <summary>
    /// Work 이름 생성 설정 (파싱된 형태)
    /// </summary>
    type WorkNamingConfig =
        { FlowExtraction: FlowExtractionConfig
          WorkGrouping: WorkGroupingPattern list
          DefaultGrouping: DefaultGroupingConfig
          WorkNameFormat: string }

    /// <summary>
    /// API 이름 추출 설정 (파싱된 형태)
    /// </summary>
    type ApiNamingConfig =
        { CompoundSuffixes: Set<string> }

    /// <summary>
    /// Convert JsonMappingSetBase + address patterns to MappingSet
    /// </summary>
    let private fromJsonBase (j: JsonMappingSetBase) (outputPatterns: string list) (inputPatterns: string list) : MappingSet =
        { Name = j.Name
          DeviceKeywords = List.ofArray j.DeviceKeywords
          Apis =
            j.Apis
            |> Array.map (fun a -> (a.Name, { OutputKeywords = List.ofArray a.OutputKeywords; InputKeywords = List.ofArray a.InputKeywords }))
            |> Map.ofArray
          OutputAddressPatterns = outputPatterns
          InputAddressPatterns = inputPatterns }

    /// <summary>
    /// Convert MappingSet to JsonMappingSetBase (without address patterns)
    /// </summary>
    let private toJsonBase (m: MappingSet) : JsonMappingSetBase =
        { Name = m.Name
          DeviceKeywords = Array.ofList m.DeviceKeywords
          Apis =
            m.Apis
            |> Map.toArray
            |> Array.map (fun (name, a) -> { Name = name; OutputKeywords = Array.ofList a.OutputKeywords; InputKeywords = Array.ofList a.InputKeywords }) }

    /// <summary>
    /// Load mapping sets from new vendor-specific JSON config
    /// Combines Common patterns with all vendor-specific patterns
    /// </summary>
    let private loadFromFileStrict path =
        if not (File.Exists path) then
            failwithf "Input matching config 파일을 찾을 수 없습니다: %s" path
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonConfig>(json)

                // Validate configuration
                if isNull (box cfg) then
                    failwithf "Configuration is null. File: %s" path
                if isNull (box cfg.Common) then
                    failwithf "Common section is null. File: %s" path
                if isNull (box cfg.Vendors) then
                    failwithf "Vendors section is null. File: %s" path

                // Collect all unique address patterns from all vendors
                let allVendors = [cfg.Vendors.LS; cfg.Vendors.AB; cfg.Vendors.Mitsubishi; cfg.Vendors.Siemens]
                let allOutputPatterns =
                    allVendors
                    |> List.collect (fun v -> List.ofArray v.OutputAddressPatterns)
                    |> List.distinct

                let allInputPatterns =
                    allVendors
                    |> List.collect (fun v -> List.ofArray v.InputAddressPatterns)
                    |> List.distinct

                // Convert Common MappingSets (applies to all vendors)
                let commonSets =
                    cfg.Common.MappingSets
                    |> Array.map (fun ms -> fromJsonBase ms allOutputPatterns allInputPatterns)
                    |> List.ofArray

                // Convert vendor-specific MappingSets
                let vendorSets =
                    allVendors
                    |> List.collect (fun vendor ->
                        let outPatterns = List.ofArray vendor.OutputAddressPatterns
                        let inPatterns = List.ofArray vendor.InputAddressPatterns
                        vendor.MappingSets
                        |> Array.map (fun ms -> fromJsonBase ms outPatterns inPatterns)
                        |> List.ofArray)

                // Combine common and vendor-specific sets
                commonSets @ vendorSets

            with ex ->
                failwithf "Input matching config 로드 실패 (%s): %s" ex.Message path

    let private defaultConfigFileName = "input-matching-config.json"

    /// <summary>
    /// 현재 사용 중인 Config 파일 경로 (외부에서 설정 가능)
    /// 기본값: AppContext.BaseDirectory + input-matching-config.json
    /// </summary>
    let mutable private currentConfigPath =
        Path.Combine(AppContext.BaseDirectory, defaultConfigFileName)

    /// <summary>
    /// Config 파일 경로 설정 (런타임에 변경 가능)
    /// C# UI에서 호출하여 올바른 경로 동기화
    /// </summary>
    let setConfigPath (path: string) : unit =
        currentConfigPath <- path

    /// <summary>
    /// 현재 Config 파일 경로 반환
    /// </summary>
    let getConfigPath () : string = currentConfigPath

    /// <summary>
    /// Config 파일 유효성 검사 결과
    /// </summary>
    type ConfigValidationResult =
        | Valid
        | Invalid of string

    /// <summary>
    /// Config 파일 유무와 유효성 검사
    /// </summary>
    let validateConfigFile (path: string) : ConfigValidationResult =
        if not (File.Exists path) then
            Invalid (sprintf "Input matching config 파일을 찾을 수 없습니다: %s" path)
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonConfig>(json)
                if isNull (box cfg) then
                    Invalid (sprintf "Configuration is null. File: %s" path)
                elif isNull (box cfg.Common) then
                    Invalid (sprintf "Common section is null. File: %s" path)
                elif isNull (box cfg.Vendors) then
                    Invalid (sprintf "Vendors section is null. File: %s" path)
                else
                    Valid
            with ex ->
                Invalid (sprintf "Input matching config 로드 실패 (%s): %s" ex.Message path)

    /// <summary>
    /// 기본 설정 파일 경로 검증
    /// </summary>
    let validateDefaultConfigFile () : ConfigValidationResult =
        validateConfigFile currentConfigPath

    /// <summary>
    /// 설정 파일에서 MappingSets 로드 (안전한 버전)
    /// </summary>
    let tryLoadFromFile (path: string) : MappingSet list option =
        if not (File.Exists path) then
            None
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonConfig>(json)

                // Validate configuration
                if isNull (box cfg) || isNull (box cfg.Common) || isNull (box cfg.Vendors) then
                    None
                else
                    // Collect all unique address patterns from all vendors
                    let allVendors = [cfg.Vendors.LS; cfg.Vendors.AB; cfg.Vendors.Mitsubishi; cfg.Vendors.Siemens]
                    let allOutputPatterns =
                        allVendors
                        |> List.collect (fun v -> List.ofArray v.OutputAddressPatterns)
                        |> List.distinct

                    let allInputPatterns =
                        allVendors
                        |> List.collect (fun v -> List.ofArray v.InputAddressPatterns)
                        |> List.distinct

                    // Convert Common MappingSets (applies to all vendors)
                    let commonSets =
                        cfg.Common.MappingSets
                        |> Array.map (fun ms -> fromJsonBase ms allOutputPatterns allInputPatterns)
                        |> List.ofArray

                    // Convert vendor-specific MappingSets
                    let vendorSets =
                        allVendors
                        |> List.collect (fun vendor ->
                            let outPatterns = List.ofArray vendor.OutputAddressPatterns
                            let inPatterns = List.ofArray vendor.InputAddressPatterns
                            vendor.MappingSets
                            |> Array.map (fun ms -> fromJsonBase ms outPatterns inPatterns)
                            |> List.ofArray)

                    // Combine common and vendor-specific sets
                    Some (commonSets @ vendorSets)

            with _ ->
                None

    /// <summary>
    /// 기본 설정 파일에서 MappingSets 로드 (안전한 버전)
    /// </summary>
    let tryLoadDefaultMappingSets () : MappingSet list option =
        tryLoadFromFile currentConfigPath

    /// <summary>
    /// 캐시된 MappingSets (무효화 가능한 캐시)
    /// TypeInitializationException 방지 및 런타임 캐시 무효화 지원
    /// </summary>
    let private defaultMappingSetsSync = obj()
    let mutable private cachedDefaultMappingSets : MappingSet list option option = None

    let private getDefaultMappingSetsInternal () : MappingSet list option =
        lock defaultMappingSetsSync (fun () ->
            match cachedDefaultMappingSets with
            | Some cached -> cached
            | None ->
                let loaded = tryLoadFromFile currentConfigPath
                cachedDefaultMappingSets <- Some loaded
                loaded)

    /// <summary>
    /// 기본 MappingSets 함수 (캐시 무효화 지원)
    /// NOTE: 설정 파일이 없으면 빈 리스트 반환 (TypeInitializationException 방지)
    /// 프로덕션 코드에서는 tryLoadDefaultMappingSets() 사용을 권장
    /// </summary>
    let getDefaultMappingSets () : MappingSet list =
        getDefaultMappingSetsInternal() |> Option.defaultValue []

    /// <summary>
    /// 기본 MappingSets (하위 호환성 유지 - Deprecated)
    /// NOTE: 이 값은 모듈 초기화 시 한 번만 평가됩니다.
    /// 캐시 무효화를 지원하려면 getDefaultMappingSets() 함수를 사용하세요.
    /// </summary>
    [<System.Obsolete("Use getDefaultMappingSets() instead for cache invalidation support")>]
    let defaultMappingSets : MappingSet list =
        getDefaultMappingSetsInternal() |> Option.defaultValue []

    let loadFromFile (path: string) =
        loadFromFileStrict path

    /// <summary>
    /// 기존 파일에서 벤더 설정 로드 (없으면 기본값 반환)
    /// </summary>
    let private loadExistingVendors (path: string) : JsonVendors =
        let defaultVendor = {
            OutputAddressPatterns = [||]
            InputAddressPatterns = [||]
            MappingSets = [||]
        }
        let defaultVendors = {
            LS = defaultVendor
            AB = defaultVendor
            Mitsubishi = defaultVendor
            Siemens = defaultVendor
        }

        if not (File.Exists path) then
            defaultVendors
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonConfig>(json)
                if isNull (box cfg) || isNull (box cfg.Vendors) then
                    defaultVendors
                else
                    cfg.Vendors
            with _ ->
                defaultVendors

    /// <summary>
    /// Save mapping sets to file (preserves vendor-specific patterns)
    /// 기존 파일의 벤더별 OutputAddressPatterns/InputAddressPatterns를 보존합니다.
    /// </summary>
    let saveToFile path sets =
        try
            // 기존 파일에서 벤더 설정 로드 (패턴 보존)
            let existingVendors = loadExistingVendors path

            // Save all sets as Common, preserve vendor patterns
            let cfg = {
                Common = {
                    MappingSets = sets |> List.map toJsonBase |> Array.ofList
                }
                Vendors = existingVendors
            }
            JsonConvert.SerializeObject(cfg, Formatting.Indented) |> fun json -> File.WriteAllText(path, json)
        with _ ->
            eprintfn "설정 파일 저장 실패: %s" path

    // ============================================================================
    // 후보 생성 설정 로딩 로직 (간소화)
    // ============================================================================

    /// <summary>
    /// null 배열을 빈 리스트로 변환
    /// </summary>
    let private arrayToList (arr: 'T[]) =
        if isNull arr then [] else List.ofArray arr

    /// <summary>
    /// JsonSymmetryRule을 SymmetryRule로 변환
    /// </summary>
    let private parseSymmetryRule (json: JsonSymmetryRule) : SymmetryRule =
        { OutputPattern = if isNull json.OutputPattern then "" else json.OutputPattern
          InputPattern = if isNull json.InputPattern then "" else json.InputPattern
          Description = if isNull json.Description then "" else json.Description }

    /// <summary>
    /// JsonExplicitMapping을 ExplicitMappingRule로 변환
    /// </summary>
    let private parseExplicitMapping (json: JsonExplicitMapping) : ExplicitMappingRule =
        { OutputKeyword = if isNull json.OutputKeyword then "" else json.OutputKeyword
          InputKeyword = if isNull json.InputKeyword then "" else json.InputKeyword
          Description = if isNull json.Description then "" else json.Description }

    /// <summary>
    /// 후보 생성 설정 로드
    /// </summary>
    let private loadCandidateConfig (path: string) : CandidateGenerationConfig option =
        if not (File.Exists path) then
            None
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonCandidateConfig>(json)
                if isNull (box cfg) then None
                else
                    let symmetryRules =
                        if isNull cfg.SymmetryRules then []
                        else cfg.SymmetryRules |> Array.map parseSymmetryRule |> List.ofArray
                    let explicitMappings =
                        if isNull cfg.ExplicitMappings then []
                        else cfg.ExplicitMappings |> Array.map parseExplicitMapping |> List.ofArray
                    Some { SymmetryRules = symmetryRules; ExplicitMappings = explicitMappings }
            with ex ->
                eprintfn "후보 생성 설정 로드 실패: %s - %s" path ex.Message
                None

    /// <summary>
    /// 기본 후보 생성 설정
    /// </summary>
    let private defaultCandidateConfig : CandidateGenerationConfig =
        { SymmetryRules =
            [ { OutputPattern = "_Q_"; InputPattern = "_I_"; Description = "Q/I 대칭" }
              { OutputPattern = "PBLQ"; InputPattern = "PBLI"; Description = "PBLQ/PBLI 대칭" } ]
          ExplicitMappings =
            [ { OutputKeyword = "PROG_SELECT"; InputKeyword = "PROG_ECHO"; Description = "" }
              { OutputKeyword = "START"; InputKeyword = "RUNING"; Description = "" } ] }

    /// <summary>
    /// 캐시된 후보 생성 설정 (mutable로 무효화 가능)
    /// </summary>
    let mutable private cachedCandidateConfig : CandidateGenerationConfig option = None
    let private candidateConfigSync = obj()

    let private getCandidateConfig () : CandidateGenerationConfig =
        lock candidateConfigSync (fun () ->
            match cachedCandidateConfig with
            | Some cfg -> cfg
            | None ->
                let loaded =
                    match loadCandidateConfig currentConfigPath with
                    | Some cfg -> cfg
                    | None -> defaultCandidateConfig
                cachedCandidateConfig <- Some loaded
                loaded)

    // ============================================================================
    // 대칭/명시적 매핑 적용 유틸리티
    // ============================================================================

    /// <summary>
    /// 문자열에 키워드가 포함되어 있는지 확인 (대소문자 무시)
    /// </summary>
    let private containsIgnoreCase (keyword: string) (text: string) =
        text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0

    /// <summary>
    /// 대칭 규칙 적용 - 패턴 치환으로 매칭 후보 생성
    /// OutputPattern에 와일드카드(*)가 있으면 캡처 그룹으로 처리
    /// 예: OutputPattern="_SOL_*_CLP", InputPattern="_WRS_*_CLP"
    ///     → "_SOL_CT_R_FLR_CLP" 매칭 시 *="CT_R_FLR"를 캡처하여 "_WRS_CT_R_FLR_CLP" 생성
    /// </summary>
    let applySymmetryRules (rules: SymmetryRule list) (outputName: string) : string list =
        rules
        |> List.choose (fun rule ->
            let outPattern = rule.OutputPattern
            let inPattern = rule.InputPattern

            // OutputPattern에 와일드카드가 있는지 확인
            if outPattern.Contains("*") then
                // 와일드카드 패턴 처리: * 를 (.*?) 캡처 그룹으로 변환 (non-greedy)
                // 부분 매칭: 앞뒤 컨텍스트 캡처
                let escapedPattern =
                    outPattern
                        .Replace(".", "\\.")
                        .Replace("*", "(.*?)")
                // 전체 문자열을 캡처: (앞부분)(패턴매칭)(뒷부분)
                let regexPattern = "^(.*?)" + escapedPattern + "(.*?)$"
                let regex = Regex(regexPattern, RegexOptions.IgnoreCase)
                let m = regex.Match(outputName)
                if m.Success && m.Groups.Count >= 3 then
                    // Groups: [0]=전체, [1]=앞부분, [2..n-1]=캡처된 와일드카드들, [n]=뒷부분
                    let prefix = m.Groups.[1].Value
                    let suffix = m.Groups.[m.Groups.Count - 1].Value

                    // InputPattern의 *에 캡처된 값들을 순서대로 대입
                    let mutable result = inPattern
                    // 와일드카드 캡처 그룹은 [2]부터 [n-2]까지 (앞뒤 컨텍스트 제외)
                    let wildcardGroupCount = m.Groups.Count - 3  // 전체, 앞, 뒤 제외
                    for i = 0 to wildcardGroupCount - 1 do
                        let captured = m.Groups.[i + 2].Value  // [2]부터 시작
                        let starIdx = result.IndexOf("*")
                        if starIdx >= 0 then
                            result <- result.Substring(0, starIdx) + captured + result.Substring(starIdx + 1)
                    // 앞뒤 컨텍스트 붙이기
                    Some (prefix + result + suffix)
                else None
            else
                // 기존 로직: 단순 문자열 치환
                if containsIgnoreCase outPattern outputName then
                    let idx = outputName.IndexOf(outPattern, StringComparison.OrdinalIgnoreCase)
                    if idx >= 0 then
                        let result = outputName.Substring(0, idx) + inPattern +
                                     outputName.Substring(idx + outPattern.Length)
                        Some result
                    else None
                else None)
        |> List.distinct

    /// <summary>
    /// Level 7: 명시적 매핑 적용 - 특수 패턴 매칭 후보 생성
    /// </summary>
    let applyExplicitMappings (mappings: ExplicitMappingRule list) (outputName: string) : string list =
        mappings
        |> List.choose (fun m ->
            if containsIgnoreCase m.OutputKeyword outputName then
                // 대소문자 무시 치환
                let idx = outputName.IndexOf(m.OutputKeyword, StringComparison.OrdinalIgnoreCase)
                if idx >= 0 then
                    let result = outputName.Substring(0, idx) + m.InputKeyword +
                                 outputName.Substring(idx + m.OutputKeyword.Length)
                    Some result
                else None
            else None)
        |> List.distinct

    // ============================================================================
    // 규칙 기반 매칭 통합
    // ============================================================================

    /// <summary>
    /// 규칙 기반으로 출력에서 입력 후보 생성 (SymmetryRules + ExplicitMappings 체이닝)
    /// 체이닝 순서:
    /// 1. 원본에 SymmetryRules 적용 → level1 후보
    /// 2. 원본에 ExplicitMappings 적용 → level1 후보
    /// 3. level1 후보에 SymmetryRules 적용 → level2 후보 (체이닝)
    /// 4. level1 후보에 ExplicitMappings 적용 → level2 후보 (체이닝)
    /// </summary>
    let generateCandidatesFromOutput (outputName: string) : string list =
        let config = getCandidateConfig()

        // Level 1: 원본에 규칙 적용
        let symmetryLevel1 = applySymmetryRules config.SymmetryRules outputName
        let explicitLevel1 = applyExplicitMappings config.ExplicitMappings outputName
        let level1 = (symmetryLevel1 @ explicitLevel1) |> List.distinct

        // Level 2: Level1 후보에 규칙 체이닝 (Q→I 적용 후 PROG_SELECT→PROG_ECHO 적용 등)
        let level2 =
            level1
            |> List.collect (fun candidate ->
                let sym = applySymmetryRules config.SymmetryRules candidate
                let exp = applyExplicitMappings config.ExplicitMappings candidate
                sym @ exp)
            |> List.distinct

        // 모든 레벨 후보 합치기 (원본 제외)
        (level1 @ level2)
        |> List.filter (fun c -> not (c.Equals(outputName, StringComparison.OrdinalIgnoreCase)))
        |> List.distinct

    /// <summary>
    /// 후보 문자열에 와일드카드 문자가 포함되어 있는지 확인
    /// </summary>
    let private hasWildcard (s: string) : bool =
        s.Contains('*') || s.Contains('?')

    /// <summary>
    /// 규칙 기반 매칭 수행 - 후보와 매칭 결과를 함께 반환 (성능 최적화)
    /// generateCandidatesFromOutput을 한 번만 호출하여 재사용
    /// 와일드카드 패턴 지원: InputPattern에 *나 ?가 포함된 경우 패턴 매칭 수행
    /// </summary>
    /// <param name="outputVar">출력 변수</param>
    /// <param name="inputDict">대문자 키 기반 입력 변수 Dictionary</param>
    /// <returns>(후보 리스트, 매칭된 입력 변수들) 튜플</returns>
    let findMatchingInputVariablesWithCandidates (outputVar: Variable) (inputDict: Map<string, Variable>) : string list * Variable list =
        let candidates = generateCandidatesFromOutput outputVar.LogicalName

        // 후보를 일반 후보와 와일드카드 패턴으로 분리
        let (wildcardCandidates, exactCandidates) = candidates |> List.partition hasWildcard

        // 1. 정확히 일치하는 후보 먼저 검색 (기존 로직)
        let exactMatches =
            exactCandidates
            |> List.choose (fun candidate ->
                inputDict.TryFind(candidate.ToUpperInvariant())
            )

        // 2. 와일드카드 패턴 매칭 (전체 입력 변수에서 검색)
        let wildcardMatches =
            if wildcardCandidates.IsEmpty then []
            else
                inputDict
                |> Map.toList
                |> List.choose (fun (key, var) ->
                    let matchFound =
                        wildcardCandidates
                        |> List.exists (fun pattern ->
                            DeviceGroupingUtils.matchesWildcard pattern key)
                    if matchFound then Some var else None
                )

        // 3. 결과 합치기 (정확 매칭 우선, 중복 제거)
        let allMatches =
            (exactMatches @ wildcardMatches)
            |> List.distinctBy (fun v -> v.LogicalName.ToUpperInvariant())

        (candidates, allMatches)

    /// <summary>
    /// 규칙 기반 매칭 결과를 InputOutputMatch로 변환
    /// MappingSet 기반 매칭과 규칙 기반 매칭을 결합
    /// 성능 최적화: trie, inputDict, apiDict, deviceToInputs를 매개변수로 받아 재사용
    /// </summary>
    let matchInputWithRulesCached
        (mappingSets: MappingSet list)
        (trie: PrefixTrie.TrieNode)
        (inputDict: Map<string, Variable>)
        (apiDict: Map<string, string>)  // 성능 최적화: extractApiFromTag 사전 계산
        (deviceToInputs: Map<string, (string * Variable) list>)
        (deviceName: string)
        (outputApi: string)
        (outputVar: Variable) : InputOutputMatch =

        // 1. 기존 MappingSet 기반 매칭 시도 (apiDict 사용)
        match matchByMappingSet trie inputDict apiDict mappingSets deviceName outputApi outputVar with
        | Some m -> m
        | None ->
            // 2. 규칙 기반 매칭 시도 (SymmetryRules + ExplicitMappings)
            // 성능 최적화: inputDict 사용 + generateCandidatesFromOutput 한 번만 호출
            let (candidates, ruleMatches) = findMatchingInputVariablesWithCandidates outputVar inputDict

            if not ruleMatches.IsEmpty then
                // 규칙 기반 매칭 성공 (candidates 재사용, apiDict O(1) 조회)
                { DeviceName = deviceName
                  OutputApi = outputApi
                  OutputVariable = outputVar
                  InputApis = ruleMatches |> List.map (fun v ->
                      apiDict |> Map.tryFind (v.LogicalName.ToUpperInvariant()) |> Option.defaultValue "")
                  InputVariables = ruleMatches
                  Strategy = MappingSet  // 규칙 기반이지만 MappingSet으로 표시 (F# 소스로 인식)
                  SimilarityScore = 1.0
                  Reason = sprintf "규칙 기반 매칭: %A → %A" candidates (ruleMatches |> List.map (fun v -> v.LogicalName)) }
            else
                // 3. 유사도 기반 매칭 (폴백) - 최적화: deviceToInputs 인덱스 사용
                match matchBySimilarity deviceToInputs outputVar.LogicalName deviceName with
                | [] ->
                    { DeviceName = deviceName; OutputApi = outputApi; OutputVariable = outputVar
                      InputApis = []; InputVariables = []; Strategy = NotMatched
                      SimilarityScore = 0.0; Reason = "매칭되는 패턴을 찾을 수 없음" }
                | candidates ->
                    let (_, _, topScore) = List.head candidates
                    { DeviceName = deviceName; OutputApi = outputApi; OutputVariable = outputVar
                      InputApis = candidates |> List.map (fun (n, _, _) ->
                          apiDict |> Map.tryFind (n.ToUpperInvariant()) |> Option.defaultValue "")
                      InputVariables = candidates |> List.map (fun (_, v, _) -> v)
                      Strategy = MappingSet; SimilarityScore = topScore
                      Reason = sprintf "유사도 기반 매칭 (%.2f)" topScore }

    /// <summary>
    /// 규칙 기반 매칭을 포함한 전체 입출력 매핑 생성
    /// 기존 createInputOutputMappings를 대체
    /// 성능 최적화: trie, inputDict, apiDict를 한 번만 생성하고 재사용
    /// </summary>
    let createInputOutputMappingsWithRules (mappingSets: MappingSet list) (outputs: DeviceApiMapping list) (variables: Variable list) =
        let inputVars = variables |> List.filter (fun v -> v.Direction = Input)

        // 성능 최적화: Trie와 Dictionary를 한 번만 생성 (이전: 각 출력마다 재생성)
        let trie = inputVars |> List.map (fun v -> v.LogicalName.ToUpperInvariant()) |> PrefixTrie.buildPrefixTrie
        let inputDict = inputVars |> List.map (fun v -> v.LogicalName.ToUpperInvariant(), v) |> Map.ofList

        // 성능 최적화: apiDict 사전 계산 - extractApiFromTag를 한 번만 호출
        // 이전: 각 매칭 시마다 extractApiFromTag 호출 O(매칭 수 × 비용)
        // 이후: apiDict에서 O(1) 조회
        let apiDict =
            inputVars
            |> List.map (fun v ->
                (v.LogicalName.ToUpperInvariant(), DeviceGroupingUtils.extractApiFromTag v.LogicalName mappingSets))
            |> Map.ofList

        // 성능 최적화: 디바이스별 입력 인덱스 사전 계산 (matchBySimilarity O(N) → O(디바이스별 입력 수))
        let deviceToInputs =
            inputVars
            |> List.map (fun v ->
                let deviceName = DeviceGroupingUtils.extractDeviceNameFromTag v.LogicalName mappingSets
                (deviceName, (v.LogicalName, v)))
            |> List.groupBy fst
            |> List.map (fun (deviceName, items) -> (deviceName, items |> List.map snd))
            |> Map.ofList

        outputs
        |> List.map (fun m ->
            let outVar = { LogicalName = m.OutputVariableName; PhysicalAddress = m.OutputPhysicalAddress; Direction = Output }
            matchInputWithRulesCached mappingSets trie inputDict apiDict deviceToInputs m.DeviceName m.Api outVar)

    // ============================================================================
    // WorkNaming 설정 로딩 및 유틸리티
    // ============================================================================

    /// <summary>
    /// 기본 WorkNaming 설정 (하드코딩 폴백)
    /// </summary>
    let private defaultWorkNamingConfig : WorkNamingConfig =
        { FlowExtraction = { SplitChar = '_'; PartIndex = 0 }
          WorkGrouping =
            [ { Pattern = Regex(@"_RBT(\d+)($|_)", RegexOptions.IgnoreCase)
                GroupFormat = "RBT{1}"
                Description = "로봇 RBT 그룹핑" }
              { Pattern = Regex(@"_RB(\d+)($|_)", RegexOptions.IgnoreCase)
                GroupFormat = "RB{1}"
                Description = "로봇 RB 그룹핑" } ]
          DefaultGrouping = { SplitChar = '_'; PartIndex = 1 }
          WorkNameFormat = "{Flow}_{WorkBase}" }

    /// <summary>
    /// JsonWorkNaming을 WorkNamingConfig로 변환
    /// </summary>
    let private parseWorkNaming (json: JsonWorkNaming) : WorkNamingConfig =
        if isNull (box json) then
            defaultWorkNamingConfig
        else
            let flowExtraction : FlowExtractionConfig =
                if isNull (box json.FlowExtraction) then
                    { SplitChar = '_'; PartIndex = 0 }
                else
                    { SplitChar = if String.IsNullOrEmpty json.FlowExtraction.SplitChar then '_' else json.FlowExtraction.SplitChar.[0]
                      PartIndex = json.FlowExtraction.PartIndex }

            let workGrouping =
                if isNull json.WorkGrouping then []
                else
                    json.WorkGrouping
                    |> Array.choose (fun p ->
                        if isNull (box p) || String.IsNullOrEmpty p.Pattern then None
                        else
                            try
                                Some { Pattern = Regex(p.Pattern, RegexOptions.IgnoreCase)
                                       GroupFormat = if isNull p.GroupFormat then "" else p.GroupFormat
                                       Description = if isNull p.Description then "" else p.Description }
                            with _ -> None)
                    |> List.ofArray

            let defaultGrouping : DefaultGroupingConfig =
                if isNull (box json.DefaultGrouping) then
                    { SplitChar = '_'; PartIndex = 1 }
                else
                    { SplitChar = if String.IsNullOrEmpty json.DefaultGrouping.SplitChar then '_' else json.DefaultGrouping.SplitChar.[0]
                      PartIndex = json.DefaultGrouping.PartIndex }

            { FlowExtraction = flowExtraction
              WorkGrouping = workGrouping
              DefaultGrouping = defaultGrouping
              WorkNameFormat = if isNull json.WorkNameFormat then "{Flow}_{WorkBase}" else json.WorkNameFormat }

    /// <summary>
    /// WorkNaming 설정 로드 (파일에서)
    /// </summary>
    let private loadWorkNamingConfig (path: string) : WorkNamingConfig =
        if not (File.Exists path) then
            defaultWorkNamingConfig
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonCandidateConfig>(json)
                if isNull (box cfg) then defaultWorkNamingConfig
                else parseWorkNaming cfg.WorkNaming
            with _ ->
                defaultWorkNamingConfig

    /// <summary>
    /// 캐시된 WorkNaming 설정 (mutable로 무효화 가능)
    /// </summary>
    let mutable private cachedWorkNamingConfig : WorkNamingConfig option = None
    let private workNamingConfigSync = obj()

    let private getWorkNamingConfigInternal () : WorkNamingConfig =
        lock workNamingConfigSync (fun () ->
            match cachedWorkNamingConfig with
            | Some cfg -> cfg
            | None ->
                let loaded = loadWorkNamingConfig currentConfigPath
                cachedWorkNamingConfig <- Some loaded
                loaded)

    /// <summary>
    /// WorkNaming 설정 가져오기 (캐시됨)
    /// </summary>
    let getWorkNamingConfig () : WorkNamingConfig =
        getWorkNamingConfigInternal()

    // ============================================================================
    // ApiNaming 설정 로딩 및 유틸리티
    // ============================================================================

    /// <summary>
    /// 기본 ApiNaming 설정 (하드코딩 폴백)
    /// </summary>
    let private defaultApiNamingConfig : ApiNamingConfig =
        { CompoundSuffixes = Set.ofList ["OK"; "COMP"; "POS"; "RST"; "END"; "CHK"] }

    /// <summary>
    /// JsonApiNaming을 ApiNamingConfig로 변환
    /// </summary>
    let private parseApiNaming (json: JsonApiNaming) : ApiNamingConfig =
        if isNull (box json) || isNull json.CompoundSuffixes then
            defaultApiNamingConfig
        else
            { CompoundSuffixes =
                json.CompoundSuffixes
                |> Array.map (fun s -> s.ToUpperInvariant())
                |> Set.ofArray }

    /// <summary>
    /// ApiNaming 설정 로드 (파일에서)
    /// </summary>
    let private loadApiNamingConfig (path: string) : ApiNamingConfig =
        if not (File.Exists path) then
            defaultApiNamingConfig
        else
            try
                let json = File.ReadAllText(path)
                let cfg = JsonConvert.DeserializeObject<JsonCandidateConfig>(json)
                if isNull (box cfg) then defaultApiNamingConfig
                else parseApiNaming cfg.ApiNaming
            with _ ->
                defaultApiNamingConfig

    /// <summary>
    /// 캐시된 ApiNaming 설정 (mutable로 무효화 가능)
    /// </summary>
    let mutable private cachedApiNamingConfig : ApiNamingConfig option = None
    let private apiNamingConfigSync = obj()

    let private getApiNamingConfigInternal () : ApiNamingConfig =
        lock apiNamingConfigSync (fun () ->
            match cachedApiNamingConfig with
            | Some cfg -> cfg
            | None ->
                let loaded = loadApiNamingConfig currentConfigPath
                cachedApiNamingConfig <- Some loaded
                loaded)

    /// <summary>
    /// ApiNaming 설정 가져오기 (캐시됨)
    /// </summary>
    let getApiNamingConfig () : ApiNamingConfig =
        getApiNamingConfigInternal()

    // ============================================================================
    // 캐시 무효화 함수
    // ============================================================================

    /// <summary>
    /// 모든 InputMatching 캐시 무효화
    /// Config 파일 변경 후 즉시 반영하려면 이 함수 호출
    /// </summary>
    let invalidateAllCaches () : unit =
        lock candidateConfigSync (fun () -> cachedCandidateConfig <- None)
        lock workNamingConfigSync (fun () -> cachedWorkNamingConfig <- None)
        lock apiNamingConfigSync (fun () -> cachedApiNamingConfig <- None)
        lock defaultMappingSetsSync (fun () -> cachedDefaultMappingSets <- None)
        DeviceGroupingUtils.invalidateCompoundSuffixesCache()
        DeviceGroupingUtils.invalidateNodeConnectionRulesCache()
        DeviceGroupingUtils.invalidateIOKeywordsCache()

    /// <summary>
    /// DeviceName에서 Flow 이름 추출 (config 기반)
    /// 예: S131_Q_RB1 → S131
    /// </summary>
    let extractFlowName (deviceName: string) : string =
        let config = getWorkNamingConfigInternal()
        let parts = deviceName.Split(config.FlowExtraction.SplitChar)
        if parts.Length > config.FlowExtraction.PartIndex then
            parts.[config.FlowExtraction.PartIndex]
        else
            deviceName

    /// <summary>
    /// DeviceName에서 Work Base 이름 추출 (config 기반)
    /// WorkGrouping 패턴 매칭 → DefaultGrouping 폴백
    /// 예: S131_Q_RB1 → RB1, S131_Q_RBT2 → RBT2
    /// </summary>
    let extractWorkBaseName (deviceName: string) : string =
        let config = getWorkNamingConfigInternal()

        // WorkGrouping 패턴 매칭 시도
        let matchResult =
            config.WorkGrouping
            |> List.tryPick (fun pattern ->
                let m = pattern.Pattern.Match(deviceName)
                if m.Success then
                    // GroupFormat에서 {1}, {2} 등을 캡처 그룹으로 치환
                    let mutable formatted = pattern.GroupFormat
                    for i = 1 to m.Groups.Count - 1 do
                        formatted <- formatted.Replace(sprintf "{%d}" i, m.Groups.[i].Value)
                    Some formatted
                else None)

        match matchResult with
        | Some workBase -> workBase
        | None ->
            // DefaultGrouping 폴백
            let parts = deviceName.Split(config.DefaultGrouping.SplitChar)
            if parts.Length > config.DefaultGrouping.PartIndex then
                parts.[config.DefaultGrouping.PartIndex]
            else
                deviceName

    /// <summary>
    /// DeviceName에서 Work 이름 생성
    /// WorkSplitDepth가 설정되면 DeviceName을 그대로 Work 이름으로 사용
    /// 아니면 config 기반으로 Flow_WorkBase 형식 생성
    /// 예: WorkSplitDepth=4 → S141_SOL_INDEX_LOAD 그대로
    /// 예: WorkSplitDepth=0 → S131_Q_RB1 → S131_RB1
    /// </summary>
    let generateWorkName (deviceName: string) : string =
        let maxDepth = DeviceGroupingUtils.getWorkSplitDepth()
        if maxDepth > 0 then
            // WorkSplitDepth가 설정되면 DeviceName을 그대로 Work 이름으로 사용
            // (extractDeviceNameFromTag에서 이미 깊이가 적용됨)
            deviceName
        else
            // 기존 로직: Flow_WorkBase 형식
            let flowName = extractFlowName deviceName
            let workBase = extractWorkBaseName deviceName
            sprintf "%s_%s" flowName workBase

    /// <summary>
    /// checkedDevices(DeviceName 집합)에서 Work 이름 집합 생성
    /// Active 시스템 필터링에 사용
    /// </summary>
    let generateWorkNamesFromDevices (checkedDevices: Set<string>) : Set<string> =
        checkedDevices
        |> Set.map generateWorkName
