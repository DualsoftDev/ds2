module Ds2.LightHouseService.Tests.DpapiRoundTripTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open Ds2.LightHouseService

/// DPAPI LocalMachine round-trip — install-service.ps1 의 Protect-DpapiLocalMachine 과 동일 형식.
/// install script 가 만들어내는 base64 가 Config.decryptDpapi 로 복원되는지 검증.
let private protectLocalMachine (plain: string) : string =
    let bytes = Encoding.UTF8.GetBytes plain
    let cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)
    Convert.ToBase64String cipher

[<Fact>]
let ``DPAPI LocalMachine round-trip — ASCII`` () =
    let plain = "test-psk-12345"
    let enc = protectLocalMachine plain
    Assert.Equal(plain, Config.decryptDpapi enc)

[<Fact>]
let ``DPAPI LocalMachine round-trip — 한국어 + 특수문자`` () =
    let plain = "한글 PSK !@# $%^"
    let enc = protectLocalMachine plain
    Assert.Equal(plain, Config.decryptDpapi enc)

[<Fact>]
let ``DPAPI LocalMachine round-trip — 64 byte (UUID 길이)`` () =
    let plain = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
    let enc = protectLocalMachine plain
    Assert.Equal(plain, Config.decryptDpapi enc)

[<Fact>]
let ``DPAPI — 잘못된 base64 는 FormatException`` () =
    Assert.Throws<FormatException>(fun () -> Config.decryptDpapi "not-base64!!!" |> ignore) |> ignore
