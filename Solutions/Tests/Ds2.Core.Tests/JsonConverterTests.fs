module JsonConverterTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Serialization

let private roundTrip<'T> (value: 'T) : 'T =
    value |> JsonConverter.serialize |> JsonConverter.deserialize<'T>

let private assertIOTagEqual (expected: IOTag option) (actual: IOTag option) =
    match expected, actual with
    | None, None -> ()
    | Some e, Some a ->
        Assert.Equal(e.Name, a.Name)
        Assert.Equal(e.Address, a.Address)
        Assert.Equal(e.Description, a.Description)
    | _ ->
        Assert.True(false, sprintf "IOTag option mismatch. expected=%A actual=%A" expected actual)

let private assertRangeSegmentsEqual (expected: RangeSegment<'T> list) (actual: RangeSegment<'T> list) =
    Assert.Equal(List.length expected, List.length actual)
    List.iter2 (fun e a ->
        Assert.True((e.Lower = a.Lower), (sprintf "Range lower mismatch. expected=%A actual=%A" e.Lower a.Lower))
        Assert.True((e.Upper = a.Upper), (sprintf "Range upper mismatch. expected=%A actual=%A" e.Upper a.Upper))) expected actual

let private assertTypedValueSpecEqual (expected: ValueSpec<'T>) (actual: ValueSpec<'T>) =
    match expected, actual with
    | Undefined, Undefined -> ()
    | Single e, Single a ->
        Assert.True((e = a), (sprintf "Single mismatch. expected=%A actual=%A" e a))
    | Multiple e, Multiple a ->
        Assert.True((e = a), (sprintf "Multiple mismatch. expected=%A actual=%A" e a))
    | Ranges e, Ranges a -> assertRangeSegmentsEqual e a
    | _ ->
        Assert.True(false, (sprintf "Typed ValueSpec mismatch. expected=%A actual=%A" expected actual))

let private assertValueSpecEqual (expected: ValueSpec) (actual: ValueSpec) =
    match expected, actual with
    | UndefinedValue,    UndefinedValue    -> ()
    | BoolValue    e,    BoolValue    a    -> assertTypedValueSpecEqual e a
    | Int8Value    e,    Int8Value    a    -> assertTypedValueSpecEqual e a
    | Int16Value   e,    Int16Value   a    -> assertTypedValueSpecEqual e a
    | Int32Value   e,    Int32Value   a    -> assertTypedValueSpecEqual e a
    | Int64Value   e,    Int64Value   a    -> assertTypedValueSpecEqual e a
    | UInt8Value   e,    UInt8Value   a    -> assertTypedValueSpecEqual e a
    | UInt16Value  e,    UInt16Value  a    -> assertTypedValueSpecEqual e a
    | UInt32Value  e,    UInt32Value  a    -> assertTypedValueSpecEqual e a
    | UInt64Value  e,    UInt64Value  a    -> assertTypedValueSpecEqual e a
    | Float32Value e,    Float32Value a    -> assertTypedValueSpecEqual e a
    | Float64Value e,    Float64Value a    -> assertTypedValueSpecEqual e a
    | StringValue  e,    StringValue  a    -> assertTypedValueSpecEqual e a
    | _ ->
        Assert.True(false, sprintf "ValueSpec kind mismatch. expected=%A actual=%A" expected actual)

let private assertApiCallEqual (expected: ApiCall) (actual: ApiCall) =
    Assert.Equal(expected.Id, actual.Id)
    Assert.Equal(expected.Name, actual.Name)
    assertIOTagEqual expected.InTag actual.InTag
    assertIOTagEqual expected.OutTag actual.OutTag
    Assert.Equal(expected.ApiDefId, actual.ApiDefId)
    assertValueSpecEqual expected.InputSpec actual.InputSpec
    assertValueSpecEqual expected.OutputSpec actual.OutputSpec

let private assertXywhEqual (expected: Xywh option) (actual: Xywh option) =
    match expected, actual with
    | None, None -> ()
    | Some e, Some a ->
        Assert.Equal(e.X, a.X)
        Assert.Equal(e.Y, a.Y)
        Assert.Equal(e.W, a.W)
        Assert.Equal(e.H, a.H)
    | _ ->
        Assert.True(false, sprintf "Xywh option mismatch. expected=%A actual=%A" expected actual)

let rec private assertConditionsEqual (expected: ResizeArray<CallCondition>) (actual: ResizeArray<CallCondition>) =
    let e = expected |> Seq.toList
    let a = actual |> Seq.toList
    Assert.Equal(e.Length, a.Length)
    List.iter2 (fun (ec: CallCondition) (ac: CallCondition) ->
        Assert.Equal(ec.Id, ac.Id)
        Assert.Equal(ec.Type, ac.Type)
        Assert.Equal(ec.IsOR, ac.IsOR)
        Assert.Equal(ec.IsRising, ac.IsRising)
        let eConds = ec.Conditions |> Seq.toList
        let aConds = ac.Conditions |> Seq.toList
        Assert.Equal(eConds.Length, aConds.Length)
        List.iter2 assertApiCallEqual eConds aConds
        assertConditionsEqual ec.Children ac.Children) e a

module JsonOptionsFactoryTests =

    [<Fact>]
    let ``DeepCopy options should roundtrip ValueSpec union`` () =
        let options = JsonOptions.createDeepCopyOptions ()
        let expected =
            Float64Value(
                Ranges [
                    { Lower = Some (0.5, Closed); Upper = Some (1.5, Open) }
                    { Lower = Some (2.0, Closed); Upper = None }
                ])

        let json = JsonSerializer.Serialize(expected, options)
        let actual = JsonSerializer.Deserialize<ValueSpec>(json, options)
        assertValueSpecEqual expected actual

    [<Fact>]
    let ``JsonOptions profiles should keep intentional differences explicit`` () =
        let project = JsonOptions.createProjectSerializationOptions ()
        let deep = JsonOptions.createDeepCopyOptions ()

        Assert.True(project.WriteIndented)
        Assert.False(deep.WriteIndented)

        Assert.Equal(JsonNamingPolicy.CamelCase, project.PropertyNamingPolicy)
        Assert.Null(deep.PropertyNamingPolicy)

        Assert.True(project.PropertyNameCaseInsensitive)
        Assert.False(deep.PropertyNameCaseInsensitive)

        Assert.True(project.IncludeFields)
        Assert.False(deep.IncludeFields)

module ValueSpecSerializationTests =

    [<Fact>]
    let ``JsonConverter should roundtrip every ValueSpec case with exact boundaries`` () =
        let int32Ranges =
            Int32Value (
                Ranges [
                    { Lower = Some (1, Closed); Upper = Some (10, Open) }
                    { Lower = None; Upper = Some (99, Closed) }
                ])

        let float64Ranges =
            Float64Value (
                Ranges [
                    { Lower = Some (-10.5, Open); Upper = Some (0.0, Closed) }
                    { Lower = Some (3.14, Closed); Upper = None }
                ])

        let specs: ValueSpec list = [
            UndefinedValue
            BoolValue (Single true)
            BoolValue (Multiple [ true; false; true ])
            Int8Value  (Single -128y)
            Int8Value  (Multiple [ -1y; 0y; 127y ])
            Int16Value (Single -32768s)
            Int16Value (Multiple [ -1s; 0s; 32767s ])
            Int32Value Undefined
            Int32Value (Single -5)
            Int32Value (Multiple [ 1; 2; 3 ])
            int32Ranges
            Int64Value (Single -9223372036854775808L)
            Int64Value (Multiple [ -1L; 0L; 9223372036854775807L ])
            UInt8Value  (Single 255uy)
            UInt16Value (Single 65535us)
            UInt32Value (Single 4294967295u)
            UInt64Value (Single 18446744073709551615UL)
            Float32Value (Single 3.14f)
            Float32Value (Multiple [ -1.5f; 0.0f; 8.25f ])
            Float64Value Undefined
            Float64Value (Single 12.75)
            Float64Value (Multiple [ -1.5; 0.0; 8.25 ])
            float64Ranges
            StringValue (Single "A-01")
            StringValue (Multiple [ "X"; "Y"; "Z" ])
        ]

        for expected in specs do
            let actual = roundTrip expected
            assertValueSpecEqual expected actual

module JsonRoundTripTests =

    [<Fact>]
    let ``JsonConverter should roundtrip Call with all fields and nested ValueSpec contents`` () =
        let systemId = Guid.NewGuid()
        let workId = Guid.NewGuid()

        let apiDef = ApiDef("ApiDef-Full", systemId)
        apiDef.Properties.Description <- Some "api-def-desc"
        apiDef.Properties.IsPush <- true
        apiDef.Properties.TxGuid <- Some(Guid.NewGuid())
        apiDef.Properties.RxGuid <- Some(Guid.NewGuid())

        let mkApiCall name inAddr outAddr =
            let api = ApiCall(name)
            api.InTag <- Some (IOTag($"{name}-in", inAddr, $"{name}-input"))
            api.OutTag <- Some (IOTag($"{name}-out", outAddr, $"{name}-output"))
            api.ApiDefId <- Some apiDef.Id
            api

        let apiInt = mkApiCall "Api-Int" "D100" "D101"
        let apiFloat = mkApiCall "Api-Float" "D200" "D201"
        let apiString = mkApiCall "Api-String" "D300" "D301"
        let apiBool = mkApiCall "Api-Bool" "D400" "D401"

        let intSpec =
            Int32Value (
                Ranges [
                    { Lower = Some (10, Closed); Upper = Some (20, Open) }
                    { Lower = None; Upper = Some (100, Closed) }
                ])

        let floatSpec =
            Float64Value (
                Ranges [
                    { Lower = Some (0.1, Open); Upper = Some (9.9, Closed) }
                ])

        let stringSpec = StringValue (Multiple [ "READY"; "RUNNING"; "STOP" ])
        let boolSpec = BoolValue (Single true)

        apiInt.OutputSpec    <- intSpec
        apiFloat.OutputSpec  <- floatSpec
        apiString.OutputSpec <- stringSpec
        apiBool.OutputSpec   <- boolSpec

        let condition = CallCondition()
        condition.Type <- Some CallConditionType.SkipUnmatch
        condition.IsOR <- true
        condition.IsRising <- true
        condition.Conditions.Add(apiFloat)
        condition.Conditions.Add(apiBool)

        let call = Call("Call", "Full", workId)
        call.Properties.Description <- Some "call-desc"
        call.Properties.CallType <- CallType.SkipIfCompleted
        call.Properties.Timeout <- Some(TimeSpan.FromSeconds(33.0))
        call.Properties.SensorDelay <- Some 12
        call.Status4 <- Status4.Homing
        call.Position <- Some(Xywh(11, 22, 33, 44))
        call.ApiCalls.Add(apiInt)
        call.ApiCalls.Add(apiString)
        call.CallConditions.Add(condition)

        let actual = roundTrip call

        Assert.Equal(call.Id, actual.Id)
        Assert.Equal(call.Name, actual.Name)
        Assert.Equal(call.ParentId, actual.ParentId)
        Assert.Equal(call.Properties.Description, actual.Properties.Description)
        Assert.Equal(call.Properties.CallType, actual.Properties.CallType)
        Assert.Equal(call.Properties.Timeout, actual.Properties.Timeout)
        Assert.Equal(call.Properties.SensorDelay, actual.Properties.SensorDelay)
        Assert.Equal(call.Status4, actual.Status4)
        assertXywhEqual call.Position actual.Position

        let expectedApiCalls = call.ApiCalls |> Seq.toList
        let actualApiCalls = actual.ApiCalls |> Seq.toList
        Assert.Equal(expectedApiCalls.Length, actualApiCalls.Length)
        List.iter2 (fun (eApi: ApiCall) (aApi: ApiCall) ->
            assertApiCallEqual eApi aApi) expectedApiCalls actualApiCalls

        assertConditionsEqual call.CallConditions actual.CallConditions


module WorkRoundTripTests =

    [<Fact>]
    let ``JsonConverter should roundtrip Work with FlowPrefix LocalName and ReferenceOf`` () =
        let flowId = Guid.NewGuid()
        let work = Work("TestFlow", "TestWork", flowId)
        work.Properties.Period <- Some(TimeSpan.FromSeconds(5.0))
        work.Position <- Some(Xywh(11, 22, 100, 40))
        work.TokenRole <- TokenRole.Source
        work.Status4 <- Status4.Homing

        let actual = roundTrip work

        Assert.Equal(work.Id, actual.Id)
        Assert.Equal(work.ParentId, actual.ParentId)
        Assert.Equal("TestFlow", actual.FlowPrefix)
        Assert.Equal("TestWork", actual.LocalName)
        Assert.Equal("TestFlow.TestWork", actual.Name)
        Assert.Equal(work.Properties.Period, actual.Properties.Period)
        Assert.Equal(work.TokenRole, actual.TokenRole)
        Assert.Equal(work.Status4, actual.Status4)
        assertXywhEqual work.Position actual.Position

    [<Fact>]
    let ``JsonConverter should roundtrip Work with ReferenceOf set`` () =
        let flowId = Guid.NewGuid()
        let origId = Guid.NewGuid()
        let work = Work("F1", "W1", flowId)
        work.ReferenceOf <- Some origId

        let actual = roundTrip work

        Assert.Equal(work.Id, actual.Id)
        Assert.Equal(Some origId, actual.ReferenceOf)
        Assert.Equal("F1", actual.FlowPrefix)
        Assert.Equal("W1", actual.LocalName)

module FileRoundTripTests =

    [<Fact>]
    let ``ProjectSerializer save and load should preserve all project fields`` () =
        let project = Project("Project-Full")
        project.Properties.Description <- Some "project-desc"
        project.Properties.Author <- Some "owner"
        project.Properties.DateTime <- Some(DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero))
        project.Properties.Version <- Some "2.0.1"

        let active = DsSystem("System-Active")
        active.Properties.Description <- Some "active-desc"
        active.Properties.EngineVersion <- Some "E-1"
        active.Properties.LangVersion <- Some "L-1"
        active.Properties.Author <- Some "author-A"
        active.Properties.DateTime <- Some(DateTimeOffset(2026, 2, 20, 13, 0, 0, TimeSpan.Zero))
        active.Properties.IRI <- Some "urn:active"
        active.IRI <- Some "https://example.local/active"

        let passive = DsSystem("System-Passive")
        passive.Properties.Description <- Some "passive-desc"
        passive.Properties.EngineVersion <- Some "E-2"
        passive.Properties.LangVersion <- Some "L-2"
        passive.Properties.Author <- Some "author-P"
        passive.Properties.DateTime <- Some(DateTimeOffset(2026, 2, 20, 14, 0, 0, TimeSpan.Zero))
        passive.Properties.IRI <- Some "urn:passive"
        passive.IRI <- Some "https://example.local/passive"

        project.ActiveSystemIds.Add(active.Id)
        project.PassiveSystemIds.Add(passive.Id)

        let filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")
        try
            ProjectSerializer.saveProject filePath project
            let actual = ProjectSerializer.loadProject filePath

            Assert.Equal(project.Id, actual.Id)
            Assert.Equal(project.Name, actual.Name)
            Assert.Equal(project.Properties.Description, actual.Properties.Description)
            Assert.Equal(project.Properties.Author, actual.Properties.Author)
            Assert.Equal(project.Properties.DateTime, actual.Properties.DateTime)
            Assert.Equal(project.Properties.Version, actual.Properties.Version)
            Assert.Equal(1, actual.ActiveSystemIds.Count)
            Assert.Equal(1, actual.PassiveSystemIds.Count)
            Assert.Equal(active.Id, actual.ActiveSystemIds.[0])
            Assert.Equal(passive.Id, actual.PassiveSystemIds.[0])
        finally
            if File.Exists(filePath) then File.Delete(filePath)

