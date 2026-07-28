namespace Ds2.Core.Encoding

open System
open System.Text

/// URL-safe Base64 encoding per RFC 4648 §5 without padding.
/// ADR-009: IDTA Part 2 uses this exact form for identifier path segments.
module Base64Url =

    /// Encode a UTF-8 string to Base64url (no padding).
    let encode (input: string) : string =
        if isNull input then nullArg "input"
        let bytes = Encoding.UTF8.GetBytes input
        // .NET 9 has Base64Url built in; use manual conversion for portability.
        let b64 = Convert.ToBase64String bytes
        b64.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// Decode Base64url back to the original UTF-8 string.
    /// Throws FormatException on invalid input.
    let decode (input: string) : string =
        if isNull input then nullArg "input"
        let normalized = input.Replace('-', '+').Replace('_', '/')
        // Restore padding (Base64 requires length % 4 == 0).
        let padding =
            match normalized.Length % 4 with
            | 0 -> ""
            | 2 -> "=="
            | 3 -> "="
            | _ -> raise (FormatException "Invalid Base64url length")
        let bytes = Convert.FromBase64String(normalized + padding)
        Encoding.UTF8.GetString bytes

    /// True iff `input` contains only Base64url alphabet characters (A-Z a-z 0-9 - _).
    let isValidChars (input: string) : bool =
        if isNull input then false
        else
            input |> Seq.forall (fun c ->
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c = '-' || c = '_')

    /// Round-trip check: encode ∘ decode is identity for well-formed inputs.
    let isRoundTrip (input: string) : bool =
        try decode (encode input) = input
        with _ -> false
