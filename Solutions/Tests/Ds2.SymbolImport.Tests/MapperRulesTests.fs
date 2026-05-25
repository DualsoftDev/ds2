module Ds2.SymbolImport.Tests.MapperRulesTests

// 기존 segment-naive 룰 테스트는 dsev2 InputMatching (MappingSets 기반) 으로 교체되면서 폐기됨.
// 새 통합 테스트는 MapperTests.fs 에서 dsev2 매칭 흐름 + 실 PLC fixture 회귀로 진행.

open Ds2.SymbolImport.Matching
open Xunit

[<Fact>]
let ``PlcSymbolParser.getFlowName — station numeric branch keeps S102_1`` () =
    let parsed = PlcSymbolParser.parseSymbol "S102_1_SOL_DEVICE_ADV"
    Assert.Equal("S102_1", PlcSymbolParser.getFlowName parsed)

[<Fact>]
let ``PlcSymbolParser.getFlowName — indexed equipment CV_1 does not split Flow`` () =
    let parsed = PlcSymbolParser.parseSymbol "CV_1_ADV"
    Assert.Equal("CV", PlcSymbolParser.getFlowName parsed)

[<Fact>]
let ``PlcSymbolParser.getWorkName — indexed equipment CV_1 merges into CV Work`` () =
    let parsed = PlcSymbolParser.parseSymbol "CV_1_ADV"
    Assert.Equal("CV", PlcSymbolParser.getWorkName parsed)
