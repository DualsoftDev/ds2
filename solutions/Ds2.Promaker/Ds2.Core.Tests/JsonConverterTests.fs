module JsonConverterTests

open System
open System.IO
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
    | UndefinedValue, UndefinedValue -> ()
    | IntValue e, IntValue a -> assertTypedValueSpecEqual e a
    | FloatValue e, FloatValue a -> assertTypedValueSpecEqual e a
    | StringValue e, StringValue a -> assertTypedValueSpecEqual e a
    | BoolValue e, BoolValue a -> assertTypedValueSpecEqual e a
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

module ValueSpecSerializationTests =

    [<Fact>]
    let ``JsonConverter should roundtrip every ValueSpec case with exact boundaries`` () =
        let intRanges =
            IntValue (
                Ranges [
                    { Lower = Some (1, Closed); Upper = Some (10, Open) }
                    { Lower = None; Upper = Some (99, Closed) }
                ])

        let floatRanges =
            FloatValue (
                Ranges [
                    { Lower = Some (-10.5m, Open); Upper = Some (0.0m, Closed) }
                    { Lower = Some (3.14m, Closed); Upper = None }
                ])

        let specs: ValueSpec list = [
            UndefinedValue
            IntValue Undefined
            IntValue (Single -5)
            IntValue (Multiple [ 1; 2; 3 ])
            intRanges
            FloatValue Undefined
            FloatValue (Single 12.75m)
            FloatValue (Multiple [ -1.5m; 0.0m; 8.25m ])
            floatRanges
            StringValue (Single "A-01")
            StringValue (Multiple [ "X"; "Y"; "Z" ])
            BoolValue (Single true)
            BoolValue (Multiple [ true; false; true ])
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
            IntValue (
                Ranges [
                    { Lower = Some (10, Closed); Upper = Some (20, Open) }
                    { Lower = None; Upper = Some (100, Closed) }
                ])

        let floatSpec =
            FloatValue (
                Ranges [
                    { Lower = Some (0.1m, Open); Upper = Some (9.9m, Closed) }
                ])

        let stringSpec = StringValue (Multiple [ "READY"; "RUNNING"; "STOP" ])
        let boolSpec = BoolValue (Single true)

        apiInt.OutputSpec    <- intSpec
        apiFloat.OutputSpec  <- floatSpec
        apiString.OutputSpec <- stringSpec
        apiBool.OutputSpec   <- boolSpec

        let condition = CallCondition()
        condition.Type <- Some CallConditionType.Active
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

        let expectedConditions = call.CallConditions |> Seq.toList
        let actualConditions = actual.CallConditions |> Seq.toList
        Assert.Equal(expectedConditions.Length, actualConditions.Length)
        List.iter2 (fun (e: CallCondition) (a: CallCondition) ->
            Assert.Equal(e.Id, a.Id)
            Assert.Equal(e.Type, a.Type)
            Assert.Equal(e.IsOR, a.IsOR)
            Assert.Equal(e.IsRising, a.IsRising)
            let eItems: ApiCall list = e.Conditions |> Seq.toList
            let aItems: ApiCall list = a.Conditions |> Seq.toList
            Assert.Equal(eItems.Length, aItems.Length)
            List.iter2 (fun (eApi: ApiCall) (aApi: ApiCall) ->
                assertApiCallEqual eApi aApi) eItems aItems
        ) expectedConditions actualConditions

    [<Fact>]
    let ``JsonConverter should roundtrip non-project entities with all writable fields`` () =
        let systemId = Guid.NewGuid()
        let flowId = Guid.NewGuid()
        let workId = Guid.NewGuid()

        let system = DsSystem("System-Full")
        system.Properties.Description <- Some "system-desc"
        system.Properties.EngineVersion <- Some "eng-1"
        system.Properties.LangVersion <- Some "lang-2"
        system.Properties.Author <- Some "author"
        system.Properties.DateTime <- Some(DateTimeOffset(2026, 2, 20, 10, 30, 0, TimeSpan.Zero))
        system.Properties.IRI <- Some "urn:system:full"
        system.IRI <- Some "https://example.local/system/full"
        system.Id <- systemId

        let flow = Flow("Flow-Full", systemId)
        flow.Properties.Description <- Some "flow-desc"
        flow.Id <- flowId

        let work = Work("Work-Full", flowId)
        work.Properties.Description <- Some "work-desc"
        work.Properties.Motion <- Some "Linear"
        work.Properties.Script <- Some "do_work();"
        work.Properties.ExternalStart <- true
        work.Properties.IsFinished <- false
        work.Properties.NumRepeat <- 3
        work.Properties.Duration <- Some(TimeSpan.FromMilliseconds(2500.0))
        work.Status4 <- Status4.Going
        work.Position <- Some(Xywh(101, 102, 103, 104))
        work.Id <- workId

        let apiDef = ApiDef("ApiDef-Entity", systemId)
        apiDef.Properties.Description <- Some "apiDef-desc"
        apiDef.Properties.IsPush <- true
        apiDef.Properties.TxGuid <- Some(Guid.NewGuid())
        apiDef.Properties.RxGuid <- Some(Guid.NewGuid())

        let arrowWork = ArrowBetweenWorks(systemId, Guid.NewGuid(), Guid.NewGuid(), ArrowType.StartReset)
        let arrowCall = ArrowBetweenCalls(workId, Guid.NewGuid(), Guid.NewGuid(), ArrowType.Group)

        let button = HwButton("Button-Full", systemId)
        button.InTag <- Some(IOTag("btn-in", "X100", "button input"))
        button.OutTag <- Some(IOTag("btn-out", "X101", "button output"))
        button.FlowGuids.Add(flowId)
        button.FlowGuids.Add(Guid.NewGuid())

        let lamp = HwLamp("Lamp-Full", systemId)
        lamp.InTag <- Some(IOTag("lamp-in", "Y100", "lamp input"))
        lamp.OutTag <- Some(IOTag("lamp-out", "Y101", "lamp output"))
        lamp.FlowGuids.Add(flowId)

        let condition = HwCondition("Condition-Full", systemId)
        condition.InTag <- Some(IOTag("cond-in", "Z100", "condition input"))
        condition.OutTag <- Some(IOTag("cond-out", "Z101", "condition output"))
        condition.FlowGuids.Add(flowId)

        let action = HwAction("Action-Full", systemId)
        action.InTag <- Some(IOTag("act-in", "A100", "action input"))
        action.OutTag <- Some(IOTag("act-out", "A101", "action output"))
        action.FlowGuids.Add(flowId)

        let systemRt = roundTrip system
        let flowRt = roundTrip flow
        let workRt = roundTrip work
        let apiDefRt = roundTrip apiDef
        let arrowWorkRt = roundTrip arrowWork
        let arrowCallRt = roundTrip arrowCall
        let buttonRt = roundTrip button
        let lampRt = roundTrip lamp
        let conditionRt = roundTrip condition
        let actionRt = roundTrip action

        Assert.Equal(system.Id, systemRt.Id)
        Assert.Equal(system.Name, systemRt.Name)
        Assert.Equal(system.Properties.Description, systemRt.Properties.Description)
        Assert.Equal(system.Properties.EngineVersion, systemRt.Properties.EngineVersion)
        Assert.Equal(system.Properties.LangVersion, systemRt.Properties.LangVersion)
        Assert.Equal(system.Properties.Author, systemRt.Properties.Author)
        Assert.Equal(system.Properties.DateTime, systemRt.Properties.DateTime)
        Assert.Equal(system.Properties.IRI, systemRt.Properties.IRI)
        Assert.Equal(system.IRI, systemRt.IRI)

        Assert.Equal(flow.Id, flowRt.Id)
        Assert.Equal(flow.Name, flowRt.Name)
        Assert.Equal(flow.ParentId, flowRt.ParentId)
        Assert.Equal(flow.Properties.Description, flowRt.Properties.Description)

        Assert.Equal(work.Id, workRt.Id)
        Assert.Equal(work.Name, workRt.Name)
        Assert.Equal(work.ParentId, workRt.ParentId)
        Assert.Equal(work.Properties.Description, workRt.Properties.Description)
        Assert.Equal(work.Properties.Motion, workRt.Properties.Motion)
        Assert.Equal(work.Properties.Script, workRt.Properties.Script)
        Assert.Equal(work.Properties.ExternalStart, workRt.Properties.ExternalStart)
        Assert.Equal(work.Properties.IsFinished, workRt.Properties.IsFinished)
        Assert.Equal(work.Properties.NumRepeat, workRt.Properties.NumRepeat)
        Assert.Equal(work.Properties.Duration, workRt.Properties.Duration)
        Assert.Equal(work.Status4, workRt.Status4)
        assertXywhEqual work.Position workRt.Position

        Assert.Equal(apiDef.Id, apiDefRt.Id)
        Assert.Equal(apiDef.Name, apiDefRt.Name)
        Assert.Equal(apiDef.ParentId, apiDefRt.ParentId)
        Assert.Equal(apiDef.Properties.Description, apiDefRt.Properties.Description)
        Assert.Equal(apiDef.Properties.IsPush, apiDefRt.Properties.IsPush)
        Assert.Equal(apiDef.Properties.TxGuid, apiDefRt.Properties.TxGuid)
        Assert.Equal(apiDef.Properties.RxGuid, apiDefRt.Properties.RxGuid)

        Assert.Equal(arrowWork.Id, arrowWorkRt.Id)
        Assert.Equal(arrowWork.ParentId, arrowWorkRt.ParentId)
        Assert.Equal(arrowWork.SourceId, arrowWorkRt.SourceId)
        Assert.Equal(arrowWork.TargetId, arrowWorkRt.TargetId)
        Assert.Equal(arrowWork.ArrowType, arrowWorkRt.ArrowType)

        Assert.Equal(arrowCall.Id, arrowCallRt.Id)
        Assert.Equal(arrowCall.ParentId, arrowCallRt.ParentId)
        Assert.Equal(arrowCall.SourceId, arrowCallRt.SourceId)
        Assert.Equal(arrowCall.TargetId, arrowCallRt.TargetId)
        Assert.Equal(arrowCall.ArrowType, arrowCallRt.ArrowType)

        assertIOTagEqual button.InTag buttonRt.InTag
        assertIOTagEqual button.OutTag buttonRt.OutTag
        Assert.True((button.FlowGuids |> Seq.toList) = (buttonRt.FlowGuids |> Seq.toList), "Button FlowGuids mismatch")

        assertIOTagEqual lamp.InTag lampRt.InTag
        assertIOTagEqual lamp.OutTag lampRt.OutTag
        Assert.True((lamp.FlowGuids |> Seq.toList) = (lampRt.FlowGuids |> Seq.toList), "Lamp FlowGuids mismatch")

        assertIOTagEqual condition.InTag conditionRt.InTag
        assertIOTagEqual condition.OutTag conditionRt.OutTag
        Assert.True((condition.FlowGuids |> Seq.toList) = (conditionRt.FlowGuids |> Seq.toList), "Condition FlowGuids mismatch")

        assertIOTagEqual action.InTag actionRt.InTag
        assertIOTagEqual action.OutTag actionRt.OutTag
        Assert.True((action.FlowGuids |> Seq.toList) = (actionRt.FlowGuids |> Seq.toList), "Action FlowGuids mismatch")

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

    [<Fact>]
    let ``JsonConverter saveToFile and loadFromFile should roundtrip an entity`` () =
        let work = Work("W1", Guid.NewGuid())
        work.Properties.Motion <- Some "Linear"
        work.Position <- Some(Xywh(10, 20, 120, 40))
        work.Status4 <- Status4.Going

        let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")
        try
            JsonConverter.saveToFile path work
            let rt = JsonConverter.loadFromFile<Work> path
            Assert.Equal(work.Id, rt.Id)
            Assert.Equal(work.Name, rt.Name)
            Assert.Equal(work.Properties.Motion, rt.Properties.Motion)
            Assert.Equal(work.Status4, rt.Status4)
            assertXywhEqual work.Position rt.Position
        finally
            if File.Exists(path) then File.Delete(path)
