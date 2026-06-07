namespace Ds2.Editor

open System.Runtime.CompilerServices

module ConditionFormulaProjection =

    open Ds2.Core

    /// 빈 condition 의 Runtime 의미. 빈 And = true, 빈 Or = false.
    /// (SSOT done-refactor-condition.md 박제 결정 — 빈 condition 은 Runtime 의미 그대로 표시.)
    let private emptyText (isOR: bool) = if isOR then "false" else "true"

    /// leaf 의 ContactKind 를 수식 표기로 반영한다.
    /// NcContact(B접) 은 `/` 전위, RisingPulse 는 `(R)` 후위, FallingPulse 는 `(F)` 후위.
    /// NoContact(A접) 은 기본이라 표기 생략. Inverter 는 placeholder leaf 라 `*` 로 표기.
    /// (SSOT 박제 결정 — NcContact/RisingPulse/FallingPulse 를 수식에서 구분 가능해야 한다.)
    let private formatApiCallItem (item: ConditionApiCallItem) =
        match item.ContactKind with
        | ContactKind.Inverter -> "*"
        | kind ->
            let name = item.ApiDefDisplayName
            // condition leaf 의 기대값은 InputSpec(Runtime 평가 대상, Phase 2 의 `eq` 저장 위치)이다.
            // 따라서 수식에는 InputSpecText 를 `=spec` 으로 표시한다 (OutputSpec 아님).
            let spec = item.InputSpecText
            let baseText =
                if System.String.IsNullOrEmpty(spec) || spec = "Undefined" then name
                else $"{name}={spec}"
            match kind with
            | ContactKind.NcContact    -> $"/{baseText}"
            | ContactKind.RisingPulse  -> $"{baseText}(R)"
            | ContactKind.FallingPulse -> $"{baseText}(F)"
            | _                        -> baseText

    let rec private formatItems (isOR: bool) (items: ConditionApiCallItem list) (children: ConditionPanelItem list) =
        let op = if isOR then "|" else "&"
        let parts = ResizeArray<string>()
        for item in items do
            parts.Add(formatApiCallItem item)
        for child in children do
            let childText = formatCondition child
            // 부모 op 의 항등원(And→true, Or→false)인 빈 자식은 의미 변화 없이 생략한다.
            if childText <> emptyText isOR then
                parts.Add($"({childText})")
        if parts.Count = 0 then emptyText isOR else System.String.Join($" {op} ", parts)

    and formatCondition (cond: ConditionPanelItem) : string =
        let inner = formatItems cond.IsOR (cond.Items |> Seq.toList) (cond.Children |> Seq.toList)
        // IsInverted 는 NOT 으로 표기한다. (SSOT 완료 조건 — isInverted:true 가 `not (...)` 로 보여야 한다.)
        if cond.IsInverted then $"not ({inner})" else inner

[<Extension>]
type ConditionFormulaExtensions =
    [<Extension>]
    static member FormulaText(item: ConditionPanelItem) =
        ConditionFormulaProjection.formatCondition item
