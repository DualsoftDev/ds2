/// U-ModelBuilder — DsStore mutation helpers 단위 테스트.
module Ds2.Reverse.Tests.Unit.ModelBuilderUnitTests

open Xunit
open Ds2.Core
open Ds2.Reverse.Core
open Ds2.Core.Store

[<Fact>]
let ``sanitizeCallName: 단순 'A.B' → ('A', 'B')`` () =
    let alias, api = ModelBuilder.sanitizeCallName "A.B"
    Assert.Equal("A", alias)
    Assert.Equal("B", api)

[<Fact>]
let ``sanitizeCallName: dot 여러개 'A.B.C' → ('A', 'B_C')`` () =
    let alias, api = ModelBuilder.sanitizeCallName "A.B.C"
    Assert.Equal("A", alias)
    Assert.Equal("B_C", api)

[<Fact>]
let ``sanitizeCallName: dot 없음 → ('A', 'VAL')`` () =
    let alias, api = ModelBuilder.sanitizeCallName "A"
    Assert.Equal("A", alias)
    Assert.Equal("VAL", api)

[<Fact>]
let ``normalizeFullName: 'A.B.C' → 'A.B_C'`` () =
    let n = ModelBuilder.normalizeFullName "A.B.C"
    Assert.Equal("A.B_C", n)

[<Fact>]
let ``normalizeFullName: 이미 normalized → 그대로`` () =
    let n = ModelBuilder.normalizeFullName "A.B"
    Assert.Equal("A.B", n)

[<Fact>]
let ``emptyStore: project + system 자동 추가`` () =
    let store, projId, sysId = ModelBuilder.emptyStore "TestProj" "TestSys"
    Assert.Equal(1, store.Projects.Count)
    Assert.Equal(1, store.Systems.Count)
    Assert.True(store.Projects.ContainsKey projId)
    Assert.True(store.Systems.ContainsKey sysId)

[<Fact>]
let ``addFlow: flow 추가 + Guid 반환`` () =
    let store, _, sysId = ModelBuilder.emptyStore "P" "S"
    let flowId = ModelBuilder.addFlow store sysId "Line"
    Assert.True(store.Flows.ContainsKey flowId)
    Assert.Equal("Line", store.Flows.[flowId].Name)

[<Fact>]
let ``addWork: work 추가 + FlowPrefix.LocalName`` () =
    let store, _, sysId = ModelBuilder.emptyStore "P" "S"
    let flowId = ModelBuilder.addFlow store sysId "Line"
    let workId = ModelBuilder.addWork store flowId "Line" "W1"
    Assert.True(store.Works.ContainsKey workId)
    Assert.Equal("Line", store.Works.[workId].FlowPrefix)
    Assert.Equal("W1", store.Works.[workId].LocalName)

[<Fact>]
let ``addCallWithApi: call + apiDef + apiCall 3개 추가`` () =
    let store, _, sysId = ModelBuilder.emptyStore "P" "S"
    let flowId = ModelBuilder.addFlow store sysId "Line"
    let workId = ModelBuilder.addWork store flowId "Line" "W1"
    let callId = ModelBuilder.addCallWithApi store workId flowId "S1.ADV" ""
    Assert.True(store.Calls.ContainsKey callId)
    Assert.Equal(1, store.ApiDefs.Count)

[<Fact>]
let ``addArrowCall: arrow 추가`` () =
    let store, _, sysId = ModelBuilder.emptyStore "P" "S"
    let flowId = ModelBuilder.addFlow store sysId "Line"
    let workId = ModelBuilder.addWork store flowId "Line" "W1"
    let c1 = ModelBuilder.addCallWithApi store workId flowId "S1.ADV" ""
    let c2 = ModelBuilder.addCallWithApi store workId flowId "S1.RET" ""
    let arrowId = ModelBuilder.addArrowCall store workId c1 c2 ArrowType.Start
    Assert.True(store.ArrowCalls.ContainsKey arrowId)
    let a = store.ArrowCalls.[arrowId]
    Assert.Equal(c1, a.SourceId)
    Assert.Equal(c2, a.TargetId)
    Assert.Equal(ArrowType.Start, a.ArrowType)
