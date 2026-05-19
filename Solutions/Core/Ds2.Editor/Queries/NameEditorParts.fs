namespace Ds2.Editor

open Ds2.Core.Store

/// <summary>
/// PropertyPanel 의 이름 편집기 컴포넌트 (prefix / editable / suffix) 가
/// 단일 선택 entity 의 종류별로 어떻게 분해될지의 결정.
///
/// - Work: <c>parseWorkNameParts</c> 로 prefix(역할 토큰) / localName 분리.
/// - Call: "DevicesAlias.ApiName" — '.' 앞은 editable, 뒤는 suffix(read-only).
/// - 그 외: 통째로 editable.
/// </summary>
[<Sealed>]
type NameEditorParts(prefix: string, editable: string, suffix: string) =
    member _.Prefix = prefix
    member _.Editable = editable
    member _.Suffix = suffix

    /// 단일 선택이 아니거나 entity 가 unknown 인 경우 — fallback name 을 그대로 editable 로.
    static member ForFallback(fallback: string) =
        NameEditorParts("", fallback, "")

    /// 단일 Work 선택 — full name 을 prefix/local 로 분리.
    static member ForWork(fullName: string) =
        let struct (prefix, local) = TokenRoleOps.parseWorkNameParts fullName
        NameEditorParts(prefix, local, "")

    /// 단일 Call 선택 — "alias.ApiName" 에서 마지막 '.' 기준 분리.
    /// '.' 없으면 fallback (통째로 editable).
    static member ForCall(fullName: string) =
        let dotIdx = if isNull fullName then -1 else fullName.LastIndexOf('.')
        if dotIdx >= 0 then
            NameEditorParts("", fullName.Substring(0, dotIdx), fullName.Substring(dotIdx))
        else
            NameEditorParts.ForFallback(if isNull fullName then "" else fullName)
