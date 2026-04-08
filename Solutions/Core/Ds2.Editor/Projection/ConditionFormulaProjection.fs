namespace Ds2.Editor

open System.Runtime.CompilerServices

module ConditionFormulaProjection =

    let private formatApiCallItem (item: CallConditionApiCallItem) =
        let name = item.ApiDefDisplayName
        let spec = item.OutputSpecText
        if System.String.IsNullOrEmpty(spec) || spec = "Undefined" then name
        else $"{name}={spec}"

    let rec private formatItems (isOR: bool) (isRising: bool) (items: CallConditionApiCallItem list) (children: CallConditionPanelItem list) =
        let op = if isOR then "|" else "&"
        let parts = ResizeArray<string>()
        for item in items do
            parts.Add(formatApiCallItem item)
        for child in children do
            let childText = formatCondition child
            if childText <> "(empty)" then
                parts.Add($"({childText})")
        if parts.Count = 0 then "(empty)"
        else
            let joined = System.String.Join(op, parts)
            if isRising then $"{joined} ↑" else joined

    and formatCondition (cond: CallConditionPanelItem) : string =
        formatItems cond.IsOR cond.IsRising (cond.Items |> Seq.toList) (cond.Children |> Seq.toList)

[<Extension>]
type ConditionFormulaExtensions =
    [<Extension>]
    static member FormulaText(item: CallConditionPanelItem) =
        ConditionFormulaProjection.formatCondition item
