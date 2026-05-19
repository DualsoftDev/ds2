module Ds2.Store.Editor.Tests.TagWizardBaseAddressTests

open System.Collections.Generic
open Ds2.Editor
open Ds2.Editor.TagWizardBaseAddress
open Xunit

[<Fact>]
let ``tryParseFirstNumeric 는 첫 정수 추출, 실패 시 None`` () =
    Assert.Equal(Some 1234, tryParseFirstNumeric "%IW1234.0.0")
    Assert.Equal(Some 4000, tryParseFirstNumeric "4000")
    Assert.Equal(Some 0,    tryParseFirstNumeric "%QW0.0.0")
    Assert.Equal(None,      tryParseFirstNumeric "")
    Assert.Equal(None,      tryParseFirstNumeric "   ")
    Assert.Equal(None,      tryParseFirstNumeric "abc")
    Assert.Equal(None,      tryParseFirstNumeric null)

[<Fact>]
let ``assignDefaultFlowBases 는 기존 설정 없으면 index 1000 단위 할당`` () =
    let empty = Dictionary<string, FlowBaseExisting>() :> IReadOnlyDictionary<_, _>
    let result = assignDefaultFlowBases [ "F0"; "F1"; "F2" ] empty

    Assert.Equal(3, result.Length)
    Assert.Equal("F0", result.[0].FlowName)
    Assert.Equal("0", result.[0].IwBase)
    Assert.Equal("0", result.[0].QwBase)
    Assert.Equal("0", result.[0].MwBase)
    Assert.Equal("1000", result.[1].IwBase)
    Assert.Equal("1000", result.[1].QwBase)
    Assert.Equal("1000", result.[1].MwBase)
    Assert.Equal("2000", result.[2].IwBase)

[<Fact>]
let ``assignDefaultFlowBases 는 기존 설정이 있으면 그 값 사용`` () =
    let existing = Dictionary<string, FlowBaseExisting>()
    existing.["F1"] <- { IwBase = Some 5000; QwBase = Some 6000; MwBase = Some 7000 }
    let ro = existing :> IReadOnlyDictionary<_, _>

    let result = assignDefaultFlowBases [ "F0"; "F1"; "F2" ] ro

    Assert.Equal("F0", result.[0].FlowName)
    Assert.Equal("0", result.[0].IwBase)  // F0: 기존 없음 → 자동
    Assert.Equal("F1", result.[1].FlowName)
    Assert.Equal("5000", result.[1].IwBase)  // F1: 기존 사용
    Assert.Equal("6000", result.[1].QwBase)
    Assert.Equal("7000", result.[1].MwBase)
    Assert.Equal("2000", result.[2].IwBase)  // F2: 자동 (i=2 * 1000)

[<Fact>]
let ``assignDefaultFlowBases 는 부분 설정 (IW만) 시 IW 만 사용 + 나머지 fallback`` () =
    let existing = Dictionary<string, FlowBaseExisting>()
    existing.["F0"] <- { IwBase = Some 9999; QwBase = None; MwBase = None }
    let ro = existing :> IReadOnlyDictionary<_, _>

    let result = assignDefaultFlowBases [ "F0" ] ro

    Assert.Equal("9999", result.[0].IwBase)  // 기존 IW
    Assert.Equal("0", result.[0].QwBase)     // QW 없음 → fallback (i=0)
    Assert.Equal("0", result.[0].MwBase)
