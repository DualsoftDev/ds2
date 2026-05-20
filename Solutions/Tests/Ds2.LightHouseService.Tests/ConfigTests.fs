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
let ``load — 정상 config 역직렬화 (s6-r53 D-S7-1 schema 1→3 chain migration)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let cfg = Config.load path
        // s6-r53 D-S7-1: schemaVersion 1 → 2 → 3 chain in-place migration. Embedding (Enabled=false) + Mtls
        // (Mode="off") 자동 채움 — 회귀 0 (BM25-only + PSK 단독 인증 현행 동작 유지).
        Assert.Equal(3, cfg.SchemaVersion)
        Assert.Equal("https://0.0.0.0:8443", cfg.ListenUrl)
        Assert.Equal(10737418240L, cfg.MaxUploadBytes)
        Assert.Equal("1.0.0", cfg.IndexerVersionRange.Min)
        Assert.Equal("1.99.99", cfg.IndexerVersionRange.Max)
        // P4-C.3 migration 의 Embedding default 검증 — Enabled=false (BM25-only fallback 유지).
        Assert.False(cfg.Embedding.Enabled)
        Assert.Equal("http://localhost:11434", cfg.Embedding.BaseUrl)
        Assert.Equal("bge-m3", cfg.Embedding.Model)
        Assert.Equal(1024, cfg.Embedding.Dimension)
        // D-S7-1 migration 의 Mtls default 검증 — Mode="off" (PSK 단독 인증 현행 유지).
        Assert.Equal(MtlsMode.Off, cfg.Mtls.Mode)
        Assert.Empty(cfg.Mtls.AllowedThumbprints)
    finally File.Delete path

[<Fact>]
let ``load — schemaVersion 2 (legacy embedding 박제) → 3 단일 step migration`` () =
    // schemaVersion 2 (Embedding 박제됨) → 3 (Mtls 자동 채움). embedding 값 보존 검증.
    let json = """{
  "schemaVersion": 2,
  "listenUrl": "https://127.0.0.1:8443",
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
  "indexerVersionRange": { "min": "1.0.0", "max": "2.99.99" },
  "embedding": { "enabled": true, "baseUrl": "http://server:11434", "model": "bge-m3", "dimension": 1024 }
}"""
    let path = writeTempConfig json
    try
        let cfg = Config.load path
        Assert.Equal(3, cfg.SchemaVersion)
        Assert.True(cfg.Embedding.Enabled)  // 보존
        Assert.Equal("http://server:11434", cfg.Embedding.BaseUrl)  // 보존
        Assert.Equal(MtlsMode.Off, cfg.Mtls.Mode)  // default 채움
    finally File.Delete path

[<Fact>]
let ``validateMtls — 부적합 mode fail-fast`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        let cfg = { baseCfg with Mtls = { Mode = "yes"; AllowedThumbprints = Array.empty } }
        Assert.Throws<InvalidDataException>(fun () -> Config.validateMtls cfg |> ignore) |> ignore
    finally File.Delete path

[<Fact>]
let ``validateMtls — mode normalize (대소문자 + 공백)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        let cfg = { baseCfg with Mtls = { Mode = "  OPTIONAL "; AllowedThumbprints = Array.empty } }
        let result = Config.validateMtls cfg
        Assert.Equal(MtlsMode.Optional, result.Mtls.Mode)
    finally File.Delete path

[<Fact>]
let ``validateMtls — thumbprint normalize (대소문자 + ':' + 공백 제거)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        // SHA-1 thumbprint (40 hex) — ':' + 공백 혼재 입력 → normalize 후 대문자 hex 40자.
        let raw = "aa:bb:cc:dd:ee:ff:11:22:33:44:55:66:77:88:99:00:aa:bb:cc:dd"
        let cfg = { baseCfg with Mtls = { Mode = MtlsMode.Required; AllowedThumbprints = [| raw |] } }
        let result = Config.validateMtls cfg
        Assert.Equal(1, result.Mtls.AllowedThumbprints.Length)
        Assert.Equal("AABBCCDDEEFF11223344556677889900AABBCCDD", result.Mtls.AllowedThumbprints.[0])
    finally File.Delete path

[<Fact>]
let ``validateMtls — 잘못된 thumbprint 길이 fail-fast`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        // SHA-1 40 / SHA-256 64 외 길이 거부.
        let cfg = { baseCfg with Mtls = { Mode = MtlsMode.Required; AllowedThumbprints = [| "DEADBEEF" |] } }
        Assert.Throws<InvalidDataException>(fun () -> Config.validateMtls cfg |> ignore) |> ignore
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
