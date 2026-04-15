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
        let props = SimulationSystemProperties()
        original.SetSimulationProperties(props)
        original.IRI <- Some "http://test.com"

        let copied = original.DeepCopy()

        // GUID가 새로 생성되는지 확인
        Assert.NotEqual(original.Id, copied.Id)
        Assert.NotEqual(Guid.Empty, copied.Id)

        // 속성이 복사되는지 확인
        Assert.Equal(original.Name, copied.Name)
        Assert.True(original.GetSimulationProperties().IsSome, "Original should have SimulationProperties")
        Assert.True(copied.GetSimulationProperties().IsSome, "Copied should have SimulationProperties")
        Assert.Equal(original.IRI, copied.IRI)


    [<Fact>]
    let ``Flow DeepCopy should create new instance with new GUID`` () =
        let systemId = Guid.NewGuid()
        let original = Flow("TestFlow", systemId)
        let props = SimulationFlowProperties()
        props.Description <- Some "Flow Description"
        original.SetSimulationProperties(props)

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(original.ParentId, copied.ParentId)
        Assert.Equal(original.Name, copied.Name)
        Assert.Equal(original.GetSimulationProperties().Value.Description, copied.GetSimulationProperties().Value.Description)

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
        original.ApiDefActionType <- ApiDefActionType.Push
        original.TxGuid <- Some (Guid.NewGuid())

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(original.ParentId, copied.ParentId)
        Assert.Equal(ApiDefActionType.Push, copied.ApiDefActionType)
        Assert.Equal(original.TxGuid, copied.TxGuid)

        // Properties가 독립적인지 확인
        copied.ApiDefActionType <- ApiDefActionType.Normal
        Assert.Equal(ApiDefActionType.Push, original.ApiDefActionType)
        Assert.Equal(ApiDefActionType.Normal, copied.ApiDefActionType)

    [<Fact>]
    let ``Multiple nested DeepCopy should maintain independence`` () =
        let original = DsSystem("OriginalSystem")
        let origProps = SimulationSystemProperties()
        original.SetSimulationProperties(origProps)

        let copy1 = original.DeepCopy()

        let copy2 = copy1.DeepCopy()


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
    let ``Work DeepCopy should copy FlowPrefix LocalName and ReferenceOf`` () =
        let flowId = Guid.NewGuid()
        let original = Work("MyFlow", "MyWork", flowId)
        original.Duration <- Some(TimeSpan.FromSeconds(3.0))
        original.Position <- Some(Xywh(10, 20, 100, 50))
        original.ReferenceOf <- Some(Guid.NewGuid())
        original.TokenRole <- TokenRole.Source

        let copied = original.DeepCopy()

        Assert.NotEqual(original.Id, copied.Id)
        Assert.Equal(flowId, copied.ParentId)
        Assert.Equal("MyFlow", copied.FlowPrefix)
        Assert.Equal("MyWork", copied.LocalName)
        Assert.Equal("MyFlow.MyWork", copied.Name)
        Assert.Equal(original.ReferenceOf, copied.ReferenceOf)
        Assert.Equal(original.TokenRole, copied.TokenRole)
        Assert.Equal(original.Duration, copied.Duration)
        Assert.True(copied.Position.IsSome)

        // 독립성 확인
        copied.Duration <- Some(TimeSpan.FromSeconds(9.0))
        Assert.Equal<TimeSpan option>(Some(TimeSpan.FromSeconds(3.0)), original.Duration)

    [<Fact>]
    let ``Work Name property combines FlowPrefix and LocalName`` () =
        let work = Work("Flow1", "WorkA", Guid.NewGuid())
        Assert.Equal("Flow1.WorkA", work.Name)

        // FlowPrefix가 비어있으면 LocalName만 반환
        let work2 = Work("", "OnlyLocal", Guid.NewGuid())
        Assert.Equal("OnlyLocal", work2.Name)

    [<Fact>]
    let ``Work Name setter splits on dot for migration`` () =
        let work = Work("", "", Guid.NewGuid())

        // dot이 있으면 분리
        work.Name <- "NewFlow.NewWork"
        Assert.Equal("NewFlow", work.FlowPrefix)
        Assert.Equal("NewWork", work.LocalName)

        // dot이 없으면 LocalName만 설정
        work.Name <- "PlainName"
        Assert.Equal("PlainName", work.LocalName)

    [<Fact>]
    let ``Call DeepCopy should copy ApiCalls and CallConditions independently`` () =
        let workId = Guid.NewGuid()
        let original = Call("DevAlias", "ApiName", workId)
        let props = SimulationCallProperties()
        props.Timeout <- Some (TimeSpan.FromSeconds(5.0))
        original.SetSimulationProperties(props)
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
        Assert.Equal(original.GetSimulationProperties().Value.Timeout, copied.GetSimulationProperties().Value.Timeout)
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
