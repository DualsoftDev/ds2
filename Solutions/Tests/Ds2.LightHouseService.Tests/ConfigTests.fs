module Ds2.LightHouseService.Tests.ConfigTests

open System
open System.IO
open System.Text
open Xunit
open Ds2.LightHouseService

let private writeTempConfig (json: string) : string =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lhs-cfg-%s.json" (Guid.NewGuid().ToString("N")))
    File.WriteAllText(path, json, Encoding.UTF8)
    path

let private validConfigJson () = """{
  "schemaVersion": 1,
  "listenUrl": "https://0.0.0.0:8443",
  "tlsCertPath": "C:\\test\\service.pfx",
  "tlsCertPasswordEncrypted": "AAAA",
  "preSharedKeyEncrypted": "BBBB",
  "storageRoot": "%TEMP%\\lhs-test",
  "maxUploadBytes": 10737418240,
  "zipBombRatioLimit": 50,
  "sessionIdleTtlMinutes": 240,
  "stagingSweepIntervalMinutes": 10,
  "logRetentionDays": 30,
  "logMaxSizeMB": 100,
  "auditRetentionDays": 365,
  "indexerVersionRange": { "min": "1.0.0", "max": "1.99.99" }
}"""

[<Fact>]
let ``load — 정상 config 역직렬화`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let cfg = Config.load path
        Assert.Equal(1, cfg.SchemaVersion)
        Assert.Equal("https://0.0.0.0:8443", cfg.ListenUrl)
        Assert.Equal(10737418240L, cfg.MaxUploadBytes)
        Assert.Equal("1.0.0", cfg.IndexerVersionRange.Min)
        Assert.Equal("1.99.99", cfg.IndexerVersionRange.Max)
    finally File.Delete path

[<Fact>]
let ``load — 미존재 path 는 FileNotFoundException`` () =
    let bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")
    Assert.Throws<FileNotFoundException>(fun () -> Config.load bogus |> ignore) |> ignore

[<Fact>]
let ``load — schemaVersion 이 service binary 보다 높으면 fail-fast`` () =
    let json = (validConfigJson()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999")
    let path = writeTempConfig json
    try
        Assert.Throws<InvalidDataException>(fun () -> Config.load path |> ignore) |> ignore
    finally File.Delete path

[<Fact>]
let ``validateHttpsOnly — http:// 거부`` () =
    let cfg = { Config.load (writeTempConfig (validConfigJson())) with ListenUrl = "http://0.0.0.0:8443" }
    Assert.Throws<InvalidDataException>(fun () -> Config.validateHttpsOnly cfg) |> ignore

[<Fact>]
let ``validateHttpsOnly — https:// 통과`` () =
    let cfg = Config.load (writeTempConfig (validConfigJson()))
    Config.validateHttpsOnly cfg

[<Fact>]
let ``expandEnv — envvar 전개`` () =
    let expanded = Config.expandEnv "%TEMP%\\sample"
    // expand 후엔 %TEMP% literal 이 사라져야
    Assert.DoesNotContain("%TEMP%", expanded)
    Assert.EndsWith("\\sample", expanded)

[<Fact>]
let ``decryptDpapi — 빈 base64 는 ArgumentException`` () =
    Assert.Throws<ArgumentException>(fun () -> Config.decryptDpapi "" |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> Config.decryptDpapi "   " |> ignore) |> ignore
