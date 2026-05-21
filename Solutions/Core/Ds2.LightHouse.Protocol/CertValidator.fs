namespace Ds2.LightHouse.Protocol

open System
open System.Text

/// **N-1 (s6-r74 c, 2026-05-21)** — mTLS thumbprint normalize SSOT.
///
/// 박제 의미: client (Promaker `Promaker.Knowledge.CertValidator.Normalize`) vs server
/// (`Ds2.LightHouseService.Config.normalizeThumbprint`) 의 의미 drift 차단. 두 박제 모두 본 helper 호출 의무.
/// 분리 시 동일 input (`"abc!def"`) ↔ client = "" / server = "ABCDEF" 의 silent strip 결함 회피.
///
/// 정합 정책 (strict, client behavior 채택):
/// - hex char (`0-9` / `A-F` / `a-f`) 만 대문자 hex 로 수집
/// - separator (`:` / ` ` / `-` / `\t`) skip (사용자 입력의 통상 separator)
/// - 그 외 non-hex 발견 시 빈 string 반환 (즉시 reject — fail-fast 정합)
/// - 길이 != 40 (SHA-1) && != 64 (SHA-256) → 빈 string
/// - null / whitespace-only → 빈 string
///
/// `[<CompiledName>]` 박제로 C# 의 `CertValidator.Normalize(string)` 호출 정합. F# 호출 = `CertValidator.normalize`.
[<RequireQualifiedAccess>]
module CertValidator =

    [<CompiledName("Normalize")>]
    let normalize (raw: string) : string =
        if String.IsNullOrWhiteSpace raw then "" else
        let sb = StringBuilder(raw.Length)
        let mutable invalid = false
        let mutable i = 0
        while not invalid && i < raw.Length do
            let ch = raw.[i]
            if (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f') then
                sb.Append(Char.ToUpperInvariant ch) |> ignore
            elif ch = ':' || ch = ' ' || ch = '-' || ch = '\t' then
                ()
            else
                invalid <- true
            i <- i + 1
        if invalid then ""
        else
            let result = sb.ToString()
            if result.Length = 40 || result.Length = 64 then result
            else ""
