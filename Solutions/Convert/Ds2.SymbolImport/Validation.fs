namespace Ds2.SymbolImport

open System

/// <summary>SymbolImport 결과 검증 — 매칭 누락 / 충돌 / 잘못된 형식 등.</summary>
module Validation =

    type Severity = Error | Warning | Info

    type ValidationIssue = {
        Severity: Severity
        Code: string
        Message: string
    }

    let private countUnmatchedByDirection (direction: SymbolDirection) (batch: Mapper.MappingBatch) =
        batch.Unmatched |> List.filter (fun e -> e.Direction = direction) |> List.length

    /// 매핑 batch + 생성 plan 검증.
    let validate (batch: Mapper.MappingBatch) (plans: ModelGenerator.SystemPlan list) : ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()

        // V-S1: unmatched 심볼이 있으면 Warning.
        if not batch.Unmatched.IsEmpty then
            let unmatchedOutput = countUnmatchedByDirection SymbolDirection.Output batch
            let unmatchedInput = countUnmatchedByDirection SymbolDirection.Input batch
            let unmatchedMemory = countUnmatchedByDirection SymbolDirection.Memory batch
            let unmatchedUnknown = countUnmatchedByDirection SymbolDirection.UnknownDir batch
            issues.Add {
                Severity = Warning
                Code = "V-S1"
                Message =
                    sprintf "매칭 실패 심볼 %d 건 (Output %d / Input %d / Memory %d / Unknown %d)"
                        batch.Unmatched.Length unmatchedOutput unmatchedInput unmatchedMemory unmatchedUnknown
            }

        // V-S2: NotMatched strategy 또는 낮은 신뢰도 (< 0.5) — Info.
        let lowConfidence =
            batch.Mapped
            |> List.filter (fun m ->
                m.Strategy = Ds2.SymbolImport.Matching.MatchingStrategy.NotMatched
                || m.Confidence < 0.5)
        if not lowConfidence.IsEmpty then
            issues.Add {
                Severity = Info
                Code = "V-S2"
                Message = sprintf "낮은 신뢰도 매핑 %d 건 (NotMatched 또는 < 0.5)" lowConfidence.Length
            }

        // V-S3: ApiCall 에 InTag/OutTag 모두 실 주소 없음 — Warning (의미 없는 Call).
        // ModelGenerator 가 placeholder 로 None 을 채우므로, "양쪽 placeholder/None" 케이스를 잡는다.
        let isRealTag (tag: Ds2.Core.IOTag option) =
            match tag with
            | Some t ->
                t.Name <> ModelGenerator.PlaceholderOutName
                && t.Name <> ModelGenerator.PlaceholderInName
            | None -> false
        for plan in plans do
            for flow in plan.Flows do
                for work in flow.Works do
                    for call in work.Calls do
                        if not (isRealTag call.InTag) && not (isRealTag call.OutTag) then
                            issues.Add {
                                Severity = Warning
                                Code = "V-S3"
                                Message = sprintf "Call '%s' — InTag/OutTag 모두 실 주소 없음 (placeholder)" call.Name
                            }

        // V-S4: 같은 (Device, Api) 가 multiple Mapping — duplicate. Warning.
        let dupes =
            batch.Mapped
            |> List.groupBy (fun m -> m.DeviceName, m.ApiName)
            |> List.filter (fun (_, ms) -> ms.Length > 2)   // in + out 1쌍은 정상. 그 외 중복.
            |> List.map fst
        for (device, api) in dupes do
            issues.Add {
                Severity = Warning
                Code = "V-S4"
                Message = sprintf "중복 매핑: Device '%s' Api '%s'" device api
            }

        List.ofSeq issues
