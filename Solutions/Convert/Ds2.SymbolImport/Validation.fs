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

    /// 매핑 batch + 생성 plan 검증.
    let validate (batch: Mapper.MappingBatch) (plans: ModelGenerator.SystemPlan list) : ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()

        // V-S1: unmatched 심볼이 있으면 Warning.
        if not batch.Unmatched.IsEmpty then
            issues.Add {
                Severity = Warning
                Code = "V-S1"
                Message = sprintf "매칭 실패 심볼 %d 건 (segment 부족 등)" batch.Unmatched.Length
            }

        // V-S2: ambiguous 매칭 (segment 1개) — Info.
        let ambiguous = batch.Mapped |> List.filter (fun m -> m.IsAmbiguous)
        if not ambiguous.IsEmpty then
            issues.Add {
                Severity = Info
                Code = "V-S2"
                Message = sprintf "모호 매칭 %d 건 (segment 1 — Default 적용)" ambiguous.Length
            }

        // V-S3: ApiCall 에 InTag/OutTag 모두 없음 — Warning (의미 없는 Call).
        for plan in plans do
            for flow in plan.Flows do
                for work in flow.Works do
                    for call in work.Calls do
                        if call.InTag.IsNone && call.OutTag.IsNone then
                            issues.Add {
                                Severity = Warning
                                Code = "V-S3"
                                Message = sprintf "Call '%s' — InTag/OutTag 모두 누락" call.Name
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
