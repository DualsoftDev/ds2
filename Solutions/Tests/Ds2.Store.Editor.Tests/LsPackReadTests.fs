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

let private countAddress expected (addresses: string array) =
    addresses
    |> Array.filter ((=) expected)
    |> Array.length

[<Fact>]
let ``toTagSpecs preserves tag identity and address`` () =
    let tag = boolTag "hubA" "%IX0.0.7"

    let specs = LsPackRead.toTagSpecs [ tag ]

    Assert.Equal(1, specs.Length)
    Assert.Equal("hubA", specs.[0].Name)
    Assert.Equal("%IX0.0.7", specs.[0].Address)
    Assert.Equal(PlcDataTypes.Bool, specs.[0].DataType)

[<Fact>]
let ``scanAddressesForTags groups LS XGI Bool bits by LWord parent address`` () =
    let tags =
        bitTags "ix" "%IX0.0." 26
        @ bitTags "qx" "%QX0.1." 26

    let scanAddresses =
        LsPackRead.scanAddressesForTags true "XGI" tags
        |> Array.concat

    Assert.Equal(tags.Length, scanAddresses.Length)
    Assert.Equal(26, countAddress "%IL0" scanAddresses)
    Assert.Equal(26, countAddress "%QL1" scanAddresses)
    Assert.True(scanAddresses |> Array.forall (fun address -> address = "%IL0" || address = "%QL1"))
