module Ds2.Backend.Tests.PlcConnectionSettingsTests

open System
open System.IO
open Ds2.Core.StandardSubmodels
open Ds2.Hub.Shared
open Xunit

/// 임시 폴더에 PlcConnection.json 을 쓰고 읽은 결과를 돌려준다. 폴더는 끝나면 지운다.
let private loadFromJson (json: string) : PlcConnectionSettings =
    let root = Path.Combine(Path.GetTempPath(), "Ds2.Backend.Tests", "PlcConnectionSettings", Guid.NewGuid().ToString("N"))
    let path = Path.Combine(root, "PlcConnection.json")
    try
        Directory.CreateDirectory root |> ignore
        File.WriteAllText(path, json)
        PlcConnectionSettings.LoadOrDefault path
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``defaults use 100ms scan interval`` () =
    let settings = PlcConnectionSettings()
    settings.EnsureProfiles()

    Assert.Equal(100, PlcConnectionSettings.DefaultScanIntervalMs)
    Assert.Equal(100, settings.ScanIntervalMs)
    Assert.All(settings.Profiles.Values, fun profile -> Assert.Equal(100, profile.ScanIntervalMs))

/// 예전 기본값 50ms 로 저장된 파일은 읽을 때 100ms 로 올린다 — 플랫 값과 벤더 프로파일 둘 다.
[<Fact>]
let ``LoadOrDefault upgrades previous 50ms default interval`` () =
    let settings =
        loadFromJson """
            {
              "vendor": "LsXgi",
              "name": "PLC#1",
              "ipAddress": "192.168.0.10",
              "port": 2004,
              "timeoutMs": 3000,
              "scanIntervalMs": 50,
              "localEthernet": true,
              "networkNumber": 0,
              "stationNumber": 255,
              "transport": "tcp",
              "usbDeviceSelector": "",
              "profiles": {
                "LsXgi": {
                  "name": "PLC#1",
                  "ipAddress": "192.168.0.10",
                  "port": 2004,
                  "timeoutMs": 3000,
                  "scanIntervalMs": 50,
                  "localEthernet": true,
                  "networkNumber": 0,
                  "stationNumber": 255,
                  "transport": "tcp",
                  "usbDeviceSelector": ""
                },
                "Mitsubishi": {
                  "name": "PLC#1",
                  "ipAddress": "192.168.0.10",
                  "port": 5007,
                  "timeoutMs": 3000,
                  "scanIntervalMs": 50,
                  "localEthernet": true,
                  "networkNumber": 0,
                  "stationNumber": 255,
                  "transport": "udp",
                  "usbDeviceSelector": ""
                }
              }
            }
            """

    Assert.Equal(100, settings.ScanIntervalMs)
    Assert.All(settings.Profiles.Values, fun profile -> Assert.Equal(100, profile.ScanIntervalMs))
    Assert.Equal(PlcTransports.Udp, settings.Profiles.["Mitsubishi"].Transport)

/// 사용자가 직접 넣은 주기는 기본값 승격 대상이 아니다.
[<Fact>]
let ``LoadOrDefault preserves user custom interval`` () =
    let settings = loadFromJson """{ "vendor": "LsXgi", "scanIntervalMs": 200 }"""
    Assert.Equal(200, settings.ScanIntervalMs)

/// SX 는 잠긴 채로 시작해야 한다 — 쓰기 허용 영역이 비어 있으면 커넥터가 쓰기 권한 발급 자체를 거부한다.
[<Fact>]
let ``MicrexSx settings start write locked`` () =
    let fresh = PlcConnectionSettings()
    Assert.Empty fresh.SxWritableAreas
    Assert.Equal(String.Empty, fresh.SxIoMapPath)
