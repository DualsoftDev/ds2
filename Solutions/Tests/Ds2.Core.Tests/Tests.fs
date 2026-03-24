module Tests

open System
open Xunit
open Ds2.Core

module CallTests =

    [<Fact>]
    let ``Call Name setter allows alias-only rename with same ApiName`` () =
        let call = Call("Dev", "Api", Guid.NewGuid())
        call.Name <- "NewDev.Api"
        Assert.Equal("NewDev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)
        Assert.Equal("NewDev.Api", call.Name)

    [<Fact>]
    let ``Call Name setter rejects ApiName change`` () =
        let call = Call("Dev", "Api", Guid.NewGuid())
        let ex = Assert.Throws<ArgumentException>(fun () -> call.Name <- "Dev.OtherApi")
        Assert.Contains("ApiName 변경을 허용하지 않습니다", ex.Message)
        Assert.Equal("Dev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)

/// ValueSpec 입력 편의 헬퍼 테스트
module ValueSpecConvenienceTests =

    [<Fact>]
    let ``rangesInt32Closed should support unbounded and bounded segments`` () =
        match ValueSpec.rangesInt32Closed [ (Some 10, Some 20); (None, Some 0) ] with
        | Int32Value (Ranges segments) ->
            Assert.Equal(2, segments.Length)
            Assert.Equal(Some (10, Closed), segments.[0].Lower)
            Assert.Equal(Some (20, Closed), segments.[0].Upper)
            Assert.Equal((None: Bound<int> option), segments.[1].Lower)
            Assert.Equal(Some (0, Closed), segments.[1].Upper)
        | _ -> Assert.True(false, "rangesInt32Closed should return Int32Value with range segments")

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
    let ``Work DeepCopy should preserve TokenRole`` () =
        let flowId = Guid.NewGuid()
        let original = Work("TokenWork", flowId)
        original.TokenRole <- TokenRole.Source

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(TokenRole.Source, copied.TokenRole)

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

    [<Fact>]
    let ``CallCondition DeepCopy should copy Children recursively`` () =
        let child = CallCondition()
        child.Type <- Some CallConditionType.SkipUnmatch
        child.IsOR <- true
        let childApi = ApiCall("ChildApi")
        childApi.OutputSpec <- Int32Value (Single 42)
        child.Conditions.Add(childApi)

        let parent = CallCondition()
        parent.Type <- Some CallConditionType.ComAux
        parent.IsRising <- true
        let parentApi = ApiCall("ParentApi")
        parentApi.OutputSpec <- BoolValue (Single true)
        parent.Conditions.Add(parentApi)
        parent.Children.Add(child)

        let copied = parent.DeepCopy()

        // ID 보존 (CallCondition은 DsEntity 비상속 — jsonClone 사용)
        Assert.Equal(parent.Id, copied.Id)
        Assert.Equal(parent.Type, copied.Type)
        Assert.Equal(parent.IsRising, copied.IsRising)
        Assert.Equal(1, copied.Conditions.Count)
        Assert.Equal(1, copied.Children.Count)

        // Children 독립성 확인
        let copiedChild = copied.Children.[0]
        Assert.Equal(child.Id, copiedChild.Id)
        Assert.Equal(child.Type, copiedChild.Type)
        Assert.Equal(child.IsOR, copiedChild.IsOR)
        Assert.Equal(1, copiedChild.Conditions.Count)

        // 변경이 원본에 영향 없는지 확인
        copiedChild.Conditions.Clear()
        Assert.Equal(1, child.Conditions.Count)
        Assert.Equal(0, copiedChild.Conditions.Count)

        copied.Children.Clear()
        Assert.Equal(1, parent.Children.Count)
        Assert.Equal(0, copied.Children.Count)

    [<Fact>]
    let ``Call DeepCopy should copy ApiCalls and CallConditions independently`` () =
        let workId = Guid.NewGuid()
        let original = Call("DevAlias", "ApiName", workId)
        original.Properties.Timeout <- Some (TimeSpan.FromSeconds(5.0))
        original.Position <- Some (Xywh(10, 20, 100, 50))

        let apiCall = ApiCall("TestApiCall")
        apiCall.OutputSpec <- Int32Value (Single 99)
        apiCall.InTag <- Some (IOTag("In", "Addr1", ""))
        original.ApiCalls.Add(apiCall)

        let cond = CallCondition()
        cond.Type <- Some CallConditionType.AutoAux
        let condApi = ApiCall("CondApi")
        cond.Conditions.Add(condApi)
        original.CallConditions.Add(cond)

        let copied = original.DeepCopy()

        // 기본 속성
        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(workId, copied.ParentId)
        Assert.Equal("DevAlias", copied.DevicesAlias)
        Assert.Equal("ApiName", copied.ApiName)
        Assert.Equal(original.Properties.Timeout, copied.Properties.Timeout)
        Assert.True(copied.Position.IsSome)

        // ApiCalls 복사 (jsonClone은 내부 ApiCall ID 보존)
        Assert.Equal(1, copied.ApiCalls.Count)
        Assert.Equal(original.ApiCalls.[0].Id, copied.ApiCalls.[0].Id)
        Assert.Equal("TestApiCall", copied.ApiCalls.[0].Name)
        Assert.Equal(Int32Value (Single 99), copied.ApiCalls.[0].OutputSpec)

        // CallConditions 복사
        Assert.Equal(1, copied.CallConditions.Count)
        Assert.Equal(1, copied.CallConditions.[0].Conditions.Count)

        // 독립성 확인
        copied.ApiCalls.Clear()
        copied.CallConditions.Clear()
        Assert.Equal(1, original.ApiCalls.Count)
        Assert.Equal(1, original.CallConditions.Count)
