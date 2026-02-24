module Tests

open System
open Xunit
open Ds2.Core

/// Project 생성 테스트
module ProjectTests =

    [<Fact>]
    let ``Project constructor should create project with valid GUID`` () =
        let project = Project("TestProject")

        // ID가 유효한 GUID인지 확인 (Guid 타입이므로 항상 유효)
        Assert.NotEqual(Guid.Empty, project.Id)

        // 이름이 올바르게 설정되었는지 확인
        Assert.Equal("TestProject", project.Name)

/// System 생성 테스트
module SystemTests =

    [<Fact>]
    let ``DsSystem constructor should create system with valid GUID`` () =
        let system = DsSystem("TestSystem")

        Assert.NotEqual(Guid.Empty, system.Id)
        Assert.Equal("TestSystem", system.Name)

/// Flow 생성 테스트
module FlowTests =

    [<Fact>]
    let ``Flow constructor should create flow with valid GUID`` () =
        let systemId = Guid.NewGuid()
        let flow = Flow("TestFlow", systemId)

        Assert.NotEqual(Guid.Empty, flow.Id)
        Assert.Equal("TestFlow", flow.Name)
        Assert.Equal(systemId, flow.ParentId)

/// Work 생성 테스트
module WorkTests =

    [<Fact>]
    let ``Work constructor should create work with valid GUID`` () =
        let flowId = Guid.NewGuid()
        let work = Work("TestWork", flowId)

        Assert.NotEqual(Guid.Empty, work.Id)
        Assert.Equal("TestWork", work.Name)
        Assert.Equal(flowId, work.ParentId)

/// 엔티티 좌표 테스트
module EntityPositionTests =

    [<Fact>]
    let ``Work should store Xywh position in entity`` () =
        let work = Work("WorkWithPosition", Guid.NewGuid())
        work.Position <- Some (Xywh(1, 2, 3, 4))

        Assert.True(work.Position.IsSome)
        Assert.Equal(1, work.Position.Value.X)
        Assert.Equal(2, work.Position.Value.Y)
        Assert.Equal(3, work.Position.Value.W)
        Assert.Equal(4, work.Position.Value.H)

    [<Fact>]
    let ``Call should store Xywh position in entity`` () =
        let call = Call("Dev", "CallWithPosition", Guid.NewGuid())
        call.Position <- Some (Xywh(10, 20, 30, 40))

        Assert.True(call.Position.IsSome)
        Assert.Equal(10, call.Position.Value.X)
        Assert.Equal(20, call.Position.Value.Y)
        Assert.Equal(30, call.Position.Value.W)
        Assert.Equal(40, call.Position.Value.H)

/// ValueSpec 입력 편의 헬퍼 테스트
module ValueSpecConvenienceTests =

    [<Fact>]
    let ``rangesIntClosed should support unbounded and bounded segments`` () =
        match ValueSpec.rangesIntClosed [ (Some 10, Some 20); (None, Some 0) ] with
        | IntValue (Ranges segments) ->
            Assert.Equal(2, segments.Length)
            Assert.Equal(Some (10, Closed), segments.[0].Lower)
            Assert.Equal(Some (20, Closed), segments.[0].Upper)
            Assert.Equal((None: Bound<int> option), segments.[1].Lower)
            Assert.Equal(Some (0, Closed), segments.[1].Upper)
        | _ -> Assert.True(false, "rangesIntClosed should return IntValue with range segments")

/// DeepCopy 테스트
module DeepCopyTests =

    [<Fact>]
    let ``DsSystem DeepCopy should create new instance with new GUID`` () =
        let original = DsSystem("TestSystem")
        original.Properties.Author <- Some "Author1"
        original.IRI <- Some "http://test.com"

        let copied = original.DeepCopy()

        // GUID가 새로 생성되는지 확인
        Assert.NotEqual(original.Id, copied.Id)
        Assert.NotEqual(Guid.Empty, copied.Id)

        // 속성이 복사되는지 확인
        Assert.Equal(original.Name, copied.Name)
        Assert.Equal(original.Properties.Author, copied.Properties.Author)
        Assert.Equal(original.IRI, copied.IRI)

        // Properties가 독립적으로 복사되는지 확인
        copied.Properties.Author <- Some "Author2"
        Assert.Equal(Some "Author1", original.Properties.Author)
        Assert.Equal(Some "Author2", copied.Properties.Author)

    [<Fact>]
    let ``Flow DeepCopy should create new instance with new GUID`` () =
        let systemId = Guid.NewGuid()
        let original = Flow("TestFlow", systemId)
        original.Properties.Description <- Some "Flow Description"

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(original.ParentId, copied.ParentId)
        Assert.Equal(original.Name, copied.Name)
        Assert.Equal(original.Properties.Description, copied.Properties.Description)

    [<Fact>]
    let ``Work DeepCopy should create new instance with Position and Status`` () =
        let flowId = Guid.NewGuid()
        let original = Work("TestWork", flowId)
        original.Position <- Some (Xywh(10, 20, 30, 40))
        original.Status4 <- Status4.Going
        original.Properties.Motion <- Some "Motion1"

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(original.ParentId, copied.ParentId)
        Assert.Equal(original.Name, copied.Name)
        Assert.Equal(Status4.Going, copied.Status4)
        Assert.True(copied.Position.IsSome)
        Assert.Equal(10, copied.Position.Value.X)
        Assert.Equal(Some "Motion1", copied.Properties.Motion)

    [<Fact>]
    let ``ApiCall DeepCopy should copy IOTag independently`` () =
        let original = ApiCall("TestApiCall")
        original.InTag <- Some (IOTag("Input", "Addr1", "Desc1"))
        original.OutTag <- Some (IOTag("Output", "Addr2", "Desc2"))

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.True(copied.InTag.IsSome)
        Assert.Equal("Input", copied.InTag.Value.Name)
        Assert.Equal("Addr1", copied.InTag.Value.Address)

        // IOTag가 독립적으로 복사되는지 확인
        copied.InTag.Value.Name <- "Modified"
        Assert.Equal("Input", original.InTag.Value.Name)
        Assert.Equal("Modified", copied.InTag.Value.Name)

    [<Fact>]
    let ``CallCondition DeepCopy should copy ValueSpec correctly`` () =
        let apiCall1 = ApiCall("ApiCall1")
        apiCall1.OutputSpec <- ValueSpec.singleInt 100
        let apiCall2 = ApiCall("ApiCall2")
        apiCall2.OutputSpec <- ValueSpec.singleBool true

        let original = CallCondition()
        original.Type <- Some CallConditionType.Common
        original.IsOR <- true
        original.IsRising <- true
        original.Conditions.Add(apiCall1)
        original.Conditions.Add(apiCall2)

        let copied = original.DeepCopy()

        Assert.Equal(Some CallConditionType.Common, copied.Type)
        Assert.True(copied.IsOR)
        Assert.True(copied.IsRising)
        Assert.Equal(2, copied.Conditions.Count)

        // OutputSpec이 올바르게 복사되는지 확인
        match copied.Conditions.[0].OutputSpec with
        | IntValue (Single 100) -> Assert.True(true)
        | _ -> Assert.Fail("OutputSpec not copied correctly")

        match copied.Conditions.[1].OutputSpec with
        | BoolValue (Single true) -> Assert.True(true)
        | _ -> Assert.Fail("OutputSpec not copied correctly")

    [<Fact>]
    let ``CallCondition with complex ValueSpec ranges should DeepCopy correctly`` () =
        let original = CallCondition()
        let apiCall = ApiCall("RangeApiCall")

        // 복잡한 ValueSpec: 범위 값
        let rangeSpec = ValueSpec.rangesIntClosed [ (Some 10, Some 20); (None, Some 100) ]
        apiCall.OutputSpec <- rangeSpec
        original.Conditions.Add(apiCall)

        let copied = original.DeepCopy()

        Assert.Equal(1, copied.Conditions.Count)
        match copied.Conditions.[0].OutputSpec with
        | IntValue (Ranges segments) ->
            Assert.Equal(2, segments.Length)
            Assert.Equal(Some (10, Closed), segments.[0].Lower)
            Assert.Equal(Some (20, Closed), segments.[0].Upper)
            Assert.Equal((None: Bound<int> option), segments.[1].Lower)
            Assert.Equal(Some (100, Closed), segments.[1].Upper)
        | _ -> Assert.Fail("Complex ValueSpec ranges not copied correctly")

    [<Fact>]
    let ``ApiDef DeepCopy should copy Properties correctly`` () =
        let systemId = Guid.NewGuid()
        let original = ApiDef("TestApiDef", systemId)
        original.Properties.IsPush <- true
        original.Properties.TxGuid <- Some (Guid.NewGuid())

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(original.ParentId, copied.ParentId)
        Assert.True(copied.Properties.IsPush)
        Assert.Equal(original.Properties.TxGuid, copied.Properties.TxGuid)

        // Properties가 독립적인지 확인
        copied.Properties.IsPush <- false
        Assert.True(original.Properties.IsPush)
        Assert.False(copied.Properties.IsPush)

    [<Fact>]
    let ``HwButton DeepCopy should copy FlowGuids independently`` () =
        let systemId = Guid.NewGuid()
        let guid1 = Guid.NewGuid()
        let guid2 = Guid.NewGuid()
        let original = HwButton("TestButton", systemId)
        original.FlowGuids.Add(guid1)
        original.FlowGuids.Add(guid2)
        original.InTag <- Some (IOTag("ButtonIn", "BtnAddr", "Button input"))

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(2, copied.FlowGuids.Count)
        Assert.Contains(guid1, copied.FlowGuids)
        Assert.Contains(guid2, copied.FlowGuids)

        // FlowGuids가 독립적으로 복사되는지 확인
        copied.FlowGuids.Clear()
        Assert.Equal(2, original.FlowGuids.Count)
        Assert.Equal(0, copied.FlowGuids.Count)

    [<Fact>]
    let ``Multiple nested DeepCopy should maintain independence`` () =
        let original = DsSystem("OriginalSystem")
        original.Properties.Author <- Some "OriginalAuthor"

        let copy1 = original.DeepCopy()
        copy1.Properties.Author <- Some "Copy1Author"

        let copy2 = copy1.DeepCopy()
        copy2.Properties.Author <- Some "Copy2Author"

        // 모든 인스턴스가 독립적인지 확인
        Assert.Equal(Some "OriginalAuthor", original.Properties.Author)
        Assert.Equal(Some "Copy1Author", copy1.Properties.Author)
        Assert.Equal(Some "Copy2Author", copy2.Properties.Author)

        // 모든 GUID가 다른지 확인
        Assert.NotEqual(original.Id, copy1.Id)
        Assert.NotEqual(copy1.Id, copy2.Id)
        Assert.NotEqual(original.Id, copy2.Id)
