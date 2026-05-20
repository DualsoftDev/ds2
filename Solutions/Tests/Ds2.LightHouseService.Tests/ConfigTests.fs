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

// **s6-r66 D-S7-4 (scope 확장)** — in-place migration chain 제거. fresh config = schemaVersion=4 정합 +
// embedding/mtls/multiTenant 모든 section 직접 박제 의무. legacy schemaVersion 의 stale config 는 fail-fast.
let private validConfigJson () = """{
  "schemaVersion": 4,
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
  "indexerVersionRange": { "min": "1.0.0", "max": "1.99.99" },
  "embedding": { "enabled": false, "baseUrl": "http://localhost:11434", "model": "bge-m3", "dimension": 1024 },
  "mtls": { "mode": "off", "allowedThumbprints": [] },
  "multiTenant": { "mode": "T1" }
}"""

[<Fact>]
let ``load — fresh schemaVersion=4 config 역직렬화 (s6-r66 D-S7-4)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let cfg = Config.load path
        Assert.Equal(4, cfg.SchemaVersion)
        Assert.Equal("https://0.0.0.0:8443", cfg.ListenUrl)
        Assert.Equal(10737418240L, cfg.MaxUploadBytes)
        Assert.Equal("1.0.0", cfg.IndexerVersionRange.Min)
        Assert.Equal("1.99.99", cfg.IndexerVersionRange.Max)
        Assert.False(cfg.Embedding.Enabled)
        Assert.Equal("http://localhost:11434", cfg.Embedding.BaseUrl)
        Assert.Equal("bge-m3", cfg.Embedding.Model)
        Assert.Equal(1024, cfg.Embedding.Dimension)
        Assert.Equal(MtlsMode.Off, cfg.Mtls.Mode)
        Assert.Empty(cfg.Mtls.AllowedThumbprints)
        // D-S7-4 multiTenant section — default Mode="T1" 박제.
        Assert.Equal(MultiTenantMode.T1, cfg.MultiTenant.Mode)
    finally File.Delete path

[<Fact>]
let ``load — legacy schemaVersion=1/2/3 stale config 는 fail-fast (reinstall 안내)`` () =
    // s6-r66 D-S7-4 scope 확장: in-place migration chain 제거 — schemaVersion ≠ Current (=4) 면 모두 fail-fast.
    // paired-release `/dist` 실행 0회 = prod 배포 0건 = legacy schema 의 config.json 인스턴스 0건 (dead code 정합).
    for stale in [1; 2; 3] do
        let json = (validConfigJson()).Replace("\"schemaVersion\": 4", sprintf "\"schemaVersion\": %d" stale)
        let path = writeTempConfig json
        try
            Assert.Throws<InvalidDataException>(fun () -> Config.load path |> ignore) |> ignore
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

/// **N-1 (s6-r74 c)** — Protocol `CertValidator.Normalize` SSOT 위임 후 strict 정합 의무.
/// 이전 박제는 server 가 non-hex silent strip → client (strict empty) 와 의미 drift.
/// 새 박제: non-hex 문자 (separator `:` / 공백 / hyphen / tab 외) 발견 시 빈 string + validateMtls 가 reject.
[<Fact>]
let ``validateMtls — non-hex 문자 fail-fast (N-1, Protocol CertValidator SSOT)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        // 'G' 는 non-hex (hex = 0-9/A-F/a-f 만). 길이 무관 invalid → InvalidDataException.
        let raw = "GG:bb:cc:dd:ee:ff:11:22:33:44:55:66:77:88:99:00:aa:bb:cc:dd"
        let cfg = { baseCfg with Mtls = { Mode = MtlsMode.Required; AllowedThumbprints = [| raw |] } }
        Assert.Throws<InvalidDataException>(fun () -> Config.validateMtls cfg |> ignore) |> ignore
    finally File.Delete path

[<Fact>]
let ``load — 미존재 path 는 FileNotFoundException`` () =
    let bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")
    Assert.Throws<FileNotFoundException>(fun () -> Config.load bogus |> ignore) |> ignore

[<Fact>]
let ``load — schemaVersion 이 service binary 보다 높으면 fail-fast`` () =
    let json = (validConfigJson()).Replace("\"schemaVersion\": 4", "\"schemaVersion\": 999")
    let path = writeTempConfig json
    try
        Assert.Throws<InvalidDataException>(fun () -> Config.load path |> ignore) |> ignore
    finally File.Delete path

[<Fact>]
let ``validateMultiTenant — 부적합 mode fail-fast (s6-r66 D-S7-4)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        let cfg = { baseCfg with MultiTenant = { Mode = "T9" } }
        Assert.Throws<InvalidDataException>(fun () -> Config.validateMultiTenant cfg |> ignore) |> ignore
    finally File.Delete path

[<Fact>]
let ``validateMultiTenant — null/빈 mode → T1 default (s6-r66 D-S7-4)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        // null Mode (record 안 null reference) → T1 default.
        let cfg = { baseCfg with MultiTenant = { Mode = null } }
        let result = Config.validateMultiTenant cfg
        Assert.Equal(MultiTenantMode.T1, result.MultiTenant.Mode)
        // 빈 string + 공백 동일.
        let cfg2 = { baseCfg with MultiTenant = { Mode = "  " } }
        let result2 = Config.validateMultiTenant cfg2
        Assert.Equal(MultiTenantMode.T1, result2.MultiTenant.Mode)
    finally File.Delete path

[<Fact>]
let ``validateMultiTenant — 소문자 mode normalize → 대문자 (s6-r66 D-S7-4)`` () =
    let path = writeTempConfig (validConfigJson())
    try
        let baseCfg = Config.load path
        for raw, expected in [ "t1", MultiTenantMode.T1; "t2", MultiTenantMode.T2; "t3", MultiTenantMode.T3 ] do
            let cfg = { baseCfg with MultiTenant = { Mode = raw } }
            let result = Config.validateMultiTenant cfg
            Assert.Equal(expected, result.MultiTenant.Mode)
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
