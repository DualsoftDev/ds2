module Ds2.Core.Tests.V10ValidationTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.V10Validation
open Xunit

let private makeWork name =
    Work("Flow", name, Guid.NewGuid())

// v12 abnormal timing range: Work.MinDuration / MaxDuration
[<Fact>]
let ``ABN-R1 — negative Work abnormal duration bounds emit Error`` () =
    let work = makeWork "Clamp"
    work.MinDuration <- Some(TimeSpan.FromMilliseconds(-1.0))
    work.MaxDuration <- Some(TimeSpan.FromMilliseconds(-2.0))

    let issues = validateWorkAbnormalDurationRange work

    Assert.Equal(2, issues.Length)
    Assert.All(issues, fun issue ->
        Assert.Equal("ABN-R1", issue.Rule)
        Assert.Equal(Error, issue.Severity))

[<Fact>]
let ``ABN-R2 — MaxDuration below MinDuration emits Error`` () =
    let work = makeWork "Clamp"
    work.MinDuration <- Some(TimeSpan.FromMilliseconds(500.0))
    work.MaxDuration <- Some(TimeSpan.FromMilliseconds(300.0))

    let issues = validateWorkAbnormalDurationRange work

    Assert.Single(issues) |> ignore
    Assert.Equal("ABN-R2", issues.[0].Rule)
    Assert.Equal(Error, issues.[0].Severity)

[<Fact>]
let ``ABN-R3 — nominal Duration outside abnormal range emits Warning`` () =
    let work = makeWork "Clamp"
    work.Duration <- Some(TimeSpan.FromMilliseconds(1000.0))
    work.MinDuration <- Some(TimeSpan.FromMilliseconds(100.0))
    work.MaxDuration <- Some(TimeSpan.FromMilliseconds(900.0))

    let issues = validateWorkAbnormalDurationRange work

    Assert.Single(issues) |> ignore
    Assert.Equal("ABN-R3", issues.[0].Rule)
    Assert.Equal(Warning, issues.[0].Severity)

[<Fact>]
let ``ABN range — valid Work abnormal duration range is OK`` () =
    let work = makeWork "Clamp"
    work.Duration <- Some(TimeSpan.FromMilliseconds(500.0))
    work.MinDuration <- Some(TimeSpan.FromMilliseconds(100.0))
    work.MaxDuration <- Some(TimeSpan.FromMilliseconds(900.0))

    Assert.Empty(validateWorkAbnormalDurationRange work)

[<Fact>]
let ``V10ValidationBatch includes Work abnormal duration range issues`` () =
    let store = DsStore()
    let work = makeWork "Clamp"
    work.MinDuration <- Some(TimeSpan.FromMilliseconds(800.0))
    work.MaxDuration <- Some(TimeSpan.FromMilliseconds(100.0))
    store.Works.[work.Id] <- work

    let issues = V10ValidationBatch.validateStore store

    Assert.True(issues |> List.exists (fun issue -> issue.Rule = "ABN-R2" && issue.Severity = Error))

// 새 매트릭스 기준 V1/V2/V3 — 불법 조합은 타입이 차단하므로 태그/T값 검증만 남는다.
[<Fact>]
let ``V1 — non-Virtual ActionType without OutTag emits Error`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.ActionType <- ActionType.Pulse None
    let ac = ApiCall("Cyl1.ADV")
    let issue = V10Validation.validateApiCallV1 ad ac
    Assert.True(issue.IsSome)
    Assert.Equal("V1", issue.Value.Rule)

[<Fact>]
let ``V1 — Virtual ActionType without OutTag is OK`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.ActionType <- ActionType.Virtual
    Assert.True((V10Validation.validateApiCallV1 ad (ApiCall("Cyl1.ADV"))).IsNone)

[<Fact>]
let ``V2 — non-Virtual SensingType without InTag emits Error`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.SensingType <- SensingType.Latch 50
    let issue = V10Validation.validateApiCallV2 ad (ApiCall("Cyl1.ADV"))
    Assert.True(issue.IsSome)
    Assert.Equal("V2", issue.Value.Rule)

[<Fact>]
let ``V2 — Virtual SensingType without InTag is OK`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.SensingType <- SensingType.Virtual 500
    Assert.True((V10Validation.validateApiCallV2 ad (ApiCall("Cyl1.ADV"))).IsNone)

[<Fact>]
let ``V3 — non-positive TimeOption emits Error`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.ActionType <- ActionType.Normal (Some 0)
    ad.SensingType <- SensingType.Latch -10
    let issues = V10Validation.validateApiDefV3 ad
    Assert.Equal(2, issues.Length)
    Assert.All(issues, fun i -> Assert.Equal("V3", i.Rule))

[<Fact>]
let ``V3 — positive TimeOption is OK`` () =
    let ad = ApiDef("ADV", Guid.NewGuid())
    ad.ActionType <- ActionType.Pulse (Some 200)
    ad.SensingType <- SensingType.Virtual 500
    Assert.Empty(V10Validation.validateApiDefV3 ad)
