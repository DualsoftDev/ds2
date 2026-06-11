module Ds2.Store.Editor.Tests.PlcBatchReadBufferTests

open Ds2.Backend.Plc
open Ev2.PLC.Common
open Xunit

let private boolTag address =
    {
        HubAddress = address
        PlcAddress = address
        DataType = PlcDataTypes.Bool
    }

[<Fact>]
let ``Batch read buffer decodes bool tags in tag order`` () =
    let tags =
        [
            boolTag "%IX0.0.4"
            boolTag "%IX0.0.5"
            boolTag "%QX0.1.4"
        ]

    let buffer =
        [|
            yield! (CoreDataTypesModule.PlcValue.BoolValue true).ToBytes()
            yield! (CoreDataTypesModule.PlcValue.BoolValue false).ToBytes()
            yield! (CoreDataTypesModule.PlcValue.BoolValue true).ToBytes()
        |]

    match PlcBatchReadBuffer.decode tags buffer with
    | Error msg -> Assert.Fail msg
    | Ok values ->
        let actual =
            values
            |> List.map (fun struct (tag, value) -> tag.HubAddress, PlcValueIo.toHubString value)

        Assert.Equal<string * string>(
            [
                "%IX0.0.4", "true"
                "%IX0.0.5", "false"
                "%QX0.1.4", "true"
            ],
            actual)

[<Fact>]
let ``Batch read buffer rejects short buffer`` () =
    let tags = [ boolTag "%IX0.0.4"; boolTag "%IX0.0.5" ]

    match PlcBatchReadBuffer.decode tags Array.empty with
    | Ok _ -> Assert.Fail "Expected short batch buffer to fail."
    | Error msg -> Assert.Contains("too short", msg)

[<Fact>]
let ``Batch tags are split by LS address group and numeric order`` () =
    let tags =
        [
            yield! [ 0..25 ] |> List.map (fun index -> boolTag (sprintf "%%IX0.0.%d" index))
            yield! [ 0..25 ] |> List.map (fun index -> boolTag (sprintf "%%QX0.1.%d" index))
        ]
        |> List.sortBy (fun tag -> tag.HubAddress)

    let chunks = PlcBatchReadBuffer.chunkTagsByAddressGroup 16 tags

    Assert.Equal<int>([ 16; 10; 16; 10 ], chunks |> List.map List.length)

    let flattened = chunks |> List.collect id |> List.map _.HubAddress
    let expected =
        [
            yield! [ 0..25 ] |> List.map (fun index -> sprintf "%%IX0.0.%d" index)
            yield! [ 0..25 ] |> List.map (fun index -> sprintf "%%QX0.1.%d" index)
        ]

    Assert.Equal<string>(expected, flattened)

    Assert.All(chunks, fun chunk ->
        let prefixes =
            chunk
            |> List.map (fun tag ->
                let address = tag.HubAddress
                address.Substring(0, address.LastIndexOf('.') + 1))
            |> Set.ofList

        Assert.Single(prefixes) |> ignore)
