module Ds2.Collector.Tests.DataApiSecurityTests

open System
open System.IO
open Xunit
open Ds2.Collector.DataApi

let private envGate = obj()
let private securityVariables =
    [| "DS2_DATA_API_REQUIRE_AUTH"
       "DS2_DATA_API_API_KEY_FILE"
       "DS2_DATA_API_REQUESTS_PER_MINUTE" |]

let private withCleanEnvironment action =
    lock envGate (fun () ->
        let saved = securityVariables |> Array.map (fun name -> name, Environment.GetEnvironmentVariable name)
        try
            for name in securityVariables do Environment.SetEnvironmentVariable(name, null)
            action ()
        finally
            for name, value in saved do Environment.SetEnvironmentVariable(name, value))

[<Fact>]
let ``loopback Data API is unauthenticated by default`` () =
    withCleanEnvironment (fun () ->
        let options = DataApiSecurity.fromEnvironment "http://127.0.0.1:62542"
        Assert.False(options.ExternalBinding)
        Assert.False(options.RequireAuthentication)
        Assert.Equal(600, options.RequestsPerMinute))

[<Fact>]
let ``external Data API rejects plaintext binding before startup`` () =
    withCleanEnvironment (fun () ->
        let ex = Assert.Throws<InvalidOperationException>(fun () ->
            DataApiSecurity.fromEnvironment "http://0.0.0.0:62542" |> ignore)
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase))

[<Fact>]
let ``Data API key validator rejects wrong key and reloads rotation`` () =
    let root = Path.Combine(Path.GetTempPath(), "ds2-data-api-key-" + Guid.NewGuid().ToString("N"))
    let path = Path.Combine(root, "key")
    try
        Directory.CreateDirectory root |> ignore
        let first = String('a', 48)
        let second = String('b', 48)
        File.WriteAllText(path, first)
        let validator = DataApiKeyValidator(path)
        Assert.True(validator.Validate first)
        Assert.False(validator.Validate second)
        File.WriteAllText(path, second)
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds 2.0)
        Assert.True(validator.Validate second)
        Assert.False(validator.Validate first)
    finally
        if Directory.Exists root then Directory.Delete(root, true)
