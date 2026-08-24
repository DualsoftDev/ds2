module Ds2.Store.Editor.Tests.LsPackReadTests

open Ds2.Backend.Plc
open Xunit

let private boolTag hubAddress plcAddress =
    { HubAddress = hubAddress
      PlcAddress = plcAddress
      DataType = PlcDataTypes.Bool }

let private bitTags hubPrefix plcPrefix count =
    [ for index in 0 .. count - 1 ->
        boolTag $"{hubPrefix}{index}" $"{plcPrefix}{index}" ]

[<Fact>]
let ``toTagSpecs preserves tag identity and address`` () =
    let tag = boolTag "hubA" "%IX0.0.7"

    let specs = LsPackRead.toTagSpecs [ tag ]

    Assert.Equal(1, specs.Length)
    Assert.Equal("hubA", specs.[0].Name)
    Assert.Equal("%IX0.0.7", specs.[0].Address)
    Assert.Equal(PlcDataTypes.Bool, specs.[0].DataType)

/// 새 Ev2(병렬읽기 업그레이드) 표시 계약: ScanAddress = "블록시작+byte오프셋B.비트".
/// 블록 프리픽스(%IL0/%QL1)가 "비트를 개별 멀티리드하지 않고 LWord 로 묶는다"는 불변식의 증거이고
/// (구 dsev2 stride=10 버그 가드), 오프셋 표기로 같은 블록의 태그들이 서로 구분된다.
[<Fact>]
let ``scanAddressesForTags groups LS XGI Bool bits by LWord parent address`` () =
    let tags =
        bitTags "ix" "%IX0.0." 26
        @ bitTags "qx" "%QX0.1." 26

    let scanAddresses =
        LsPackRead.scanAddressesForTags true tags
        |> Array.concat

    Assert.Equal(tags.Length, scanAddresses.Length)
    // 전 태그가 두 LWord 블록 중 하나에서 나온다 — 블록 밖 개별 읽기 없음.
    Assert.Equal(26, scanAddresses |> Array.filter (fun a -> a.StartsWith "%IL0+") |> Array.length)
    Assert.Equal(26, scanAddresses |> Array.filter (fun a -> a.StartsWith "%QL1+") |> Array.length)
    // 같은 블록이라도 태그별 위치 문자열은 전부 달라야 한다 (구분 불가 뭉침 방지).
    Assert.Equal(tags.Length, scanAddresses |> Array.distinct |> Array.length)
    // 대표 지점: bit 0 = byte 0 의 bit 0, bit 25 = byte 3 의 bit 1.
    Assert.Contains("%IL0+0B.0", scanAddresses)
    Assert.Contains("%IL0+3B.1", scanAddresses)
    Assert.Contains("%QL1+3B.1", scanAddresses)

