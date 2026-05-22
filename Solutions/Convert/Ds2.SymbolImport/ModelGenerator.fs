namespace Ds2.SymbolImport

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Ds2.Core

/// <summary>매핑 결과 → DS2 도메인 엔티티 생성 plan. DsStore mutation 은 호출자가 수행
/// (Ds2.UI.Core/Editor 의존 회피 + WPF 없이 테스트 가능 위해 본 모듈은 *plan 만* 생성).</summary>
module ModelGenerator =

    type SystemPlan = {
        Name: string
        IsActive: bool        // true = controller (Flow 보유), false = passive device (ApiDef 보유)
        Flows: FlowPlan list
        ApiDefs: ApiDefPlan list
        UserTags: UserTagPlan list
    }

    and FlowPlan = {
        Name: string
        Works: WorkPlan list
    }

    and WorkPlan = {
        Name: string
        Calls: CallPlan list
    }

    and CallPlan = {
        Name: string                          // "{DeviceName}.{ApiName}"
        DeviceName: string
        ApiName: string
        InTag: IOTag option                   // PLC Input 심볼 → InTag (첫 Input)
        OutTag: IOTag option                  // PLC Output 심볼 → OutTag
    }

    and ApiDefPlan = {
        Name: string                          // Api 이름
        ActionType: ActionType                // v10 — 룰 기반 추론. 기본 Real(Level, None)
        SensingType: SensingType
    }

    and UserTagPlan = {
        Name: string
        LogLevel: string
        TagAddress: string
        ValueType: string
        MatchOp: string
        MatchValue: string
    }

    // ── v10 ActionType/SensingType 추론 (심볼명 패턴) ──
    let private inferActionType (apiName: string) : ActionType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        // _PB (push button) → Latched (Action.set 의 SR 회로 의미)
        if upper = "PB" || upper.EndsWith("_PB") then ActionType.Real (Latched, None)
        else ActionType.Real (Level, None)

    let private inferSensingType (apiName: string) : SensingType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        // _LS / _LMT (limit switch) → OneShot (Sensing.edge)
        if upper = "LS" || upper.EndsWith("_LS") || upper = "LMT" || upper.EndsWith("_LMT") then
            SensingType.Real (OneShot, None)
        else SensingType.Real (Level, None)

    let private toIOTag (entry: SymbolEntry) : IOTag =
        IOTag(entry.Name, entry.Address, entry.Comment)

    // v10 V1/V2 silence — 매칭이 짝을 못 찾은 ApiCall 에 빈 주소 placeholder 부여.
    // ApiDef.ActionType/SensingType 은 Real 유지 → V4 부작용 없음.
    // 운영 검증된 패턴 (DSPilot fix_aasx_v10.py 가 후처리로 동일하게 치환).
    // 사용자가 UI 에서 실 PLC 주소 보정 시 placeholder address 가 단서.
    [<Literal>]
    let PlaceholderInName = "(unset-IN)"
    [<Literal>]
    let PlaceholderOutName = "(unset-OUT)"
    [<Literal>]
    let private PlaceholderDescription = "v10 placeholder"

    let private placeholderInTag () = IOTag(PlaceholderInName, "", PlaceholderDescription)
    let private placeholderOutTag () = IOTag(PlaceholderOutName, "", PlaceholderDescription)

    let private nullableBool defaultValue (value: Nullable<bool>) =
        if value.HasValue then value.Value else defaultValue

    let private normalizeText (value: string) =
        if isNull value then ""
        else value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim()

    let private patternList (patterns: string array) =
        if isNull patterns then []
        else
            patterns
            |> Array.toList
            |> List.map normalizeText
            |> List.filter (String.IsNullOrWhiteSpace >> not)

    let private wildcardMatches (patterns: string list) (value: string) =
        let value = if isNull value then "" else value
        patterns
        |> List.exists (fun pattern ->
            let regex =
                "^"
                + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".")
                + "$"
            Regex.IsMatch(value, regex, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant))

    let private directionMatches (directions: string list) (entry: SymbolEntry) =
        if directions.IsEmpty then true
        else
            let direction =
                match entry.Direction with
                | SymbolDirection.Input -> "Input"
                | SymbolDirection.Output -> "Output"
                | SymbolDirection.Memory -> "Memory"
                | SymbolDirection.UnknownDir -> "Unknown"
            directions
            |> List.exists (fun d ->
                d = "*"
                || String.Equals(d, direction, StringComparison.OrdinalIgnoreCase)
                || (entry.Direction = SymbolDirection.Memory && String.Equals(d, "Marker", StringComparison.OrdinalIgnoreCase)))

    let private entryMatchesRule (rule: MappingConfig.UserTagRuleDto) (entry: SymbolEntry) =
        if isNull (box rule) || not (nullableBool true rule.Enabled) then false
        else
            let directions = patternList rule.Directions
            let addressPatterns = patternList rule.AddressPatterns
            let namePatterns = patternList rule.NamePatterns
            let commentPatterns = patternList rule.CommentPatterns
            let excludeAddressPatterns = patternList rule.ExcludeAddressPatterns
            let excludeNamePatterns = patternList rule.ExcludeNamePatterns
            let excludeCommentPatterns = patternList rule.ExcludeCommentPatterns

            let addressOk = addressPatterns.IsEmpty || wildcardMatches addressPatterns entry.Address
            let textOk =
                (not namePatterns.IsEmpty && wildcardMatches namePatterns entry.Name)
                || (not commentPatterns.IsEmpty && wildcardMatches commentPatterns entry.Comment)
            let excluded =
                wildcardMatches excludeAddressPatterns entry.Address
                || wildcardMatches excludeNamePatterns entry.Name
                || wildcardMatches excludeCommentPatterns entry.Comment

            directionMatches directions entry && addressOk && textOk && not excluded

    let private entriesOfBatch (batch: Mapper.MappingBatch) =
        batch.Mapped
        |> List.collect (fun mapping ->
            (mapping.OutputEntry |> Option.toList) @ mapping.InputEntries)
        |> List.append batch.Unmatched
        |> List.distinctBy (fun entry -> entry.Address, entry.Name)

    let private userTagName (entry: SymbolEntry) =
        let name = normalizeText entry.Name
        if String.IsNullOrWhiteSpace name then
            let comment = normalizeText entry.Comment
            if String.IsNullOrWhiteSpace comment then normalizeText entry.Address else comment
        else
            name

    let private toUserTagPlan (rule: MappingConfig.UserTagRuleDto) (entry: SymbolEntry) =
        { Name = userTagName entry
          LogLevel = if String.IsNullOrWhiteSpace rule.LogLevel then "Error" else normalizeText rule.LogLevel
          TagAddress = normalizeText entry.Address
          ValueType = if String.IsNullOrWhiteSpace rule.ValueType then "Bit" else normalizeText rule.ValueType
          MatchOp = if String.IsNullOrWhiteSpace rule.MatchOp then "RisingEdge" else normalizeText rule.MatchOp
          MatchValue = if isNull rule.MatchValue then "1" else normalizeText rule.MatchValue }

    let private uniquifyUserTagNames (plans: UserTagPlan list) =
        let used = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        plans
        |> List.map (fun plan ->
            if used.Add(plan.Name) then plan
            else
                let baseName = $"{plan.Name}_{plan.TagAddress}"
                let mutable suffix = 2
                let mutable candidate = baseName
                while not (used.Add(candidate)) do
                    candidate <- $"{baseName}_{suffix}"
                    suffix <- suffix + 1
                { plan with Name = candidate })

    let private generateUserTags (config: MappingConfig.InputMatchingConfigDto) (batch: Mapper.MappingBatch) =
        if isNull (box config)
           || isNull (box config.UserTagRules)
           || isNull config.UserTagRules.Rules
           || not (nullableBool true config.UserTagRules.Enabled) then
            []
        else
            let rules = config.UserTagRules.Rules |> Array.toList
            entriesOfBatch batch
            |> List.choose (fun entry ->
                rules
                |> List.tryFind (fun rule -> entryMatchesRule rule entry)
                |> Option.map (fun rule -> toUserTagPlan rule entry))
            |> List.distinctBy (fun plan -> plan.TagAddress)
            |> uniquifyUserTagNames

    /// 매핑 batch → SystemPlan list.
    /// - Controller (active) — 모든 Flow 가 묶임. project 단위 1개.
    /// - Device passive — DeviceName 별 별도 System (ApiDef 보유).
    let generateWithConfig (config: MappingConfig.InputMatchingConfigDto) (batch: Mapper.MappingBatch) : SystemPlan list =
        let mapped = batch.Mapped
        let userTags = generateUserTags config batch

        // Controller flows — Flow → Work → Call (1 Mapping = 1 Call).
        let flows =
            mapped
            |> List.groupBy (fun m -> m.FlowName)
            |> List.map (fun (flowName, flowMappings) ->
                let works =
                    flowMappings
                    |> List.groupBy (fun m -> m.WorkName)
                    |> List.map (fun (workName, workMappings) ->
                        let calls =
                            workMappings
                            |> List.map (fun m ->
                                let outTag =
                                    match m.OutputEntry with
                                    | Some e -> toIOTag e
                                    | None   -> placeholderOutTag ()
                                let inTag =
                                    match m.InputEntries |> List.tryHead with
                                    | Some e -> toIOTag e
                                    | None   -> placeholderInTag ()
                                { Name = sprintf "%s.%s" m.DeviceName m.ApiName
                                  DeviceName = m.DeviceName
                                  ApiName = m.ApiName
                                  InTag = Some inTag
                                  OutTag = Some outTag })
                        { Name = workName; Calls = calls })
                { Name = flowName; Works = works })

        let controller = {
            Name = "Controller"
            IsActive = true
            Flows = flows
            ApiDefs = []
            UserTags = userTags
        }

        // Device passive systems — DeviceName 별. ApiDef 는 (DeviceName, ApiName) distinct.
        let devices =
            mapped
            |> List.groupBy (fun m -> m.DeviceName)
            |> List.map (fun (deviceName, deviceMappings) ->
                let apiDefs =
                    deviceMappings
                    |> List.map (fun m -> m.ApiName)
                    |> List.distinct
                    |> List.map (fun apiName ->
                        { Name = apiName
                          ActionType = inferActionType apiName
                          SensingType = inferSensingType apiName })
                { Name = deviceName
                  IsActive = false
                  Flows = []
                  ApiDefs = apiDefs
                  UserTags = [] })

        controller :: devices

    let generate (batch: Mapper.MappingBatch) : SystemPlan list =
        generateWithConfig Unchecked.defaultof<MappingConfig.InputMatchingConfigDto> batch
