namespace Ds2.Collector.DataApi

open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http

type DataApiSecurityOptions = {
    ExternalBinding: bool
    RequireAuthentication: bool
    ApiKeyFile: string option
    RequestsPerMinute: int
}

[<Sealed>]
type DataApiKeyValidator(path: string) =
    let gate = obj()
    let mutable lastWrite = DateTime.MinValue
    let mutable expectedHash = Array.empty<byte>

    let load () =
        lock gate (fun () ->
            let currentWrite = File.GetLastWriteTimeUtc path
            if currentWrite <> lastWrite || expectedHash.Length = 0 then
                let value = File.ReadAllText(path).Trim()
                if value.Length < 32 then
                    invalidOp "Data API key must contain at least 32 characters."
                expectedHash <- SHA256.HashData(Encoding.UTF8.GetBytes value)
                lastWrite <- currentWrite)

    do load ()

    member _.Validate(value: string) =
        if String.IsNullOrWhiteSpace value || value.Length > 4096 then false
        else
            load ()
            let actual = SHA256.HashData(Encoding.UTF8.GetBytes value)
            CryptographicOperations.FixedTimeEquals(actual, expectedHash)

[<RequireQualifiedAccess>]
module DataApiSecurity =
    let private boolEnv name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value ->
            match Boolean.TryParse value with
            | true, parsed -> parsed
            | _ -> fallback

    let private intEnv name fallback minimum maximum =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value ->
            match Int32.TryParse value with
            | true, parsed -> Math.Clamp(parsed, minimum, maximum)
            | _ -> fallback

    let private isLoopbackHost (uri: Uri) =
        uri.IsLoopback
        || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (match IPAddress.TryParse uri.Host with true, address -> IPAddress.IsLoopback address | _ -> false)

    let private parseUrls (urls: string) =
        urls.Split([|';'; ','|], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.map (fun value ->
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri -> uri
            | _ -> invalidArg "urls" $"Invalid Data API binding URL '{value}'.")

    let fromEnvironment (urls: string) =
        let bindings = parseUrls urls
        let externalBinding = bindings |> Array.exists (isLoopbackHost >> not)
        let requireAuth = boolEnv "DS2_DATA_API_REQUIRE_AUTH" externalBinding
        let keyFile =
            match Environment.GetEnvironmentVariable "DS2_DATA_API_API_KEY_FILE" with
            | null | "" -> None
            | path -> Some(Path.GetFullPath(path.Trim()))
        if externalBinding then
            if bindings |> Array.exists (fun uri -> not (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))) then
                invalidOp "Externally bound Data API endpoints must use https://."
            if not requireAuth then
                invalidOp "Externally bound Data API endpoints cannot disable authentication."
        if requireAuth then
            match keyFile with
            | None -> invalidOp "DS2_DATA_API_API_KEY_FILE is required when Data API authentication is enabled."
            | Some path when not (File.Exists path) -> invalidOp $"Data API key file does not exist: {path}"
            | Some path when not (OperatingSystem.IsWindows()) ->
                let mode = File.GetUnixFileMode path
                let exposed = mode &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute |||
                                         UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)
                if exposed <> (enum<UnixFileMode> 0) then
                    invalidOp "Data API key file must not be accessible by group or other users."
            | _ -> ()
        {
            ExternalBinding = externalBinding
            RequireAuthentication = requireAuth
            ApiKeyFile = keyFile
            RequestsPerMinute = intEnv "DS2_DATA_API_REQUESTS_PER_MINUTE" 600 10 100_000
        }

    let tryCredential (context: HttpContext) =
        let authorization = context.Request.Headers.Authorization.ToString()
        if authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            authorization.Substring("Bearer ".Length).Trim()
        else
            context.Request.Headers.["X-API-Key"].ToString().Trim()
