module Ds2.Store.Editor.Tests.LsPackReadTests

open Ds2.Backend.Plc
open Xunit

let private boolTag hubAddress plcAddress =
    { HubAddress = hubAddress
      PlcAddress = plcAddress
      DataType = PlcDataTypes.Bool }

[<Fact>]
let ``toTagSpecs preserves tag identity and address`` () =
    let tag = boolTag "hubA" "%IX0.0.7"

    let specs = LsPackRead.toTagSpecs [ tag ]

    Assert.Equal(1, specs.Length)
    Assert.Equal("hubA", specs.[0].Name)
    Assert.Equal("%IX0.0.7", specs.[0].Address)
    Assert.Equal(PlcDataTypes.Bool, specs.[0].DataType)
