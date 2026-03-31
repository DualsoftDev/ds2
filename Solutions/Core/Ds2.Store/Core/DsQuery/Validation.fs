namespace Ds2.Store.DsQuery

open System

/// Device Alias / ApiName 유효성 검증
module InputValidation =

    /// DevicesAlias 검증 결과
    type AliasValidationResult =
        | Valid
        | EmptyAlias
        | AliasDotForbidden

    /// ApiName 검증 결과
    type ApiNameValidationResult =
        | Valid of string list
        | EmptyInput
        | EmptyAfterParse
        | ApiNameDotForbidden

    /// DevicesAlias 유효성 검증
    let validateDevicesAlias (alias: string) : AliasValidationResult =
        let trimmed = alias.Trim()
        if String.IsNullOrEmpty(trimmed) then EmptyAlias
        elif trimmed.Contains('.') then AliasDotForbidden
        else AliasValidationResult.Valid

    /// ApiName 텍스트 (세미콜론 구분) 파싱 + 검증
    let validateApiNames (text: string) : ApiNameValidationResult =
        let trimmed = text.Trim()
        if String.IsNullOrEmpty(trimmed) then EmptyInput
        else
            let names =
                trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s.Length > 0)
                |> Array.toList
            if names.IsEmpty then EmptyAfterParse
            elif names |> List.exists (fun n -> n.Contains('.')) then ApiNameDotForbidden
            else ApiNameValidationResult.Valid names
