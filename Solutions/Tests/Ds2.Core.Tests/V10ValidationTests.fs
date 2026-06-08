module Ds2.Core.Tests.V10ValidationTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.V10Validation
open Xunit

let private makeApiDef name systemId actionType sensingType =
    let ad = ApiDef(name, systemId)
    ad.ActionType <- actionType
    ad.SensingType <- sensingType
    ad

let private makeApiCall name =
    ApiCall(name)

let private makeWork name =
    Work("Flow", name, Guid.NewGuid())

// V1: ActionType=Real ⇒ OutTag required (Error)
[<Fact>]
let ``V1 — Real ActionType + no OutTag emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (Level, None))
    let ac = makeApiCall "Cyl1.ADV"
    let issue = validateApiCallV1 ad ac
    Assert.True(issue.IsSome)
    Assert.Equal("V1", issue.Value.Rule)
    Assert.Equal(Error, issue.Value.Severity)

[<Fact>]
let ``V1 — Real ActionType + OutTag set is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (Level, None))
    let ac = makeApiCall "Cyl1.ADV"
    ac.OutTag <- Some (IOTag("ADV", "Y10", ""))
    Assert.True((validateApiCallV1 ad ac).IsNone)

[<Fact>]
let ``V1 — Virtual ActionType + no OutTag is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Virtual None) (SensingType.Real (Level, None))
    let ac = makeApiCall "Cyl1.ADV"
    Assert.True((validateApiCallV1 ad ac).IsNone)

// V2: SensingType=Real ⇒ InTag required (Error)
[<Fact>]
let ``V2 — Real SensingType + no InTag emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (Level, None))
    let ac = makeApiCall "Cyl1.ADV"
    let issue = validateApiCallV2 ad ac
    Assert.True(issue.IsSome)
    Assert.Equal("V2", issue.Value.Rule)
    Assert.Equal(Error, issue.Value.Severity)

[<Fact>]
let ``V2 — Virtual SensingType + no InTag is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Virtual None)
    let ac = makeApiCall "Cyl1.ADV"
    Assert.True((validateApiCallV2 ad ac).IsNone)

// V3: TimePolicy.Append ms > 0
[<Fact>]
let ``V3 — Append ms = 0 emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, Some (Append 0))) (SensingType.Real (Level, None))
    let issues = validateApiDefV3 ad
    Assert.NotEmpty(issues)
    Assert.Equal("V3", issues.[0].Rule)
    Assert.Equal(Error, issues.[0].Severity)

[<Fact>]
let ``V3 — Append ms negative emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (OneShot, Some (Append -10)))
    let issues = validateApiDefV3 ad
    Assert.NotEmpty(issues)

[<Fact>]
let ``V3 — Append ms positive is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, Some (Append 200))) (SensingType.Real (Level, Some (Append 50)))
    Assert.Empty(validateApiDefV3 ad)

// V4: Virtual ⇒ Work.Duration defined
[<Fact>]
let ``V4 — Virtual ApiDef without Work.Duration emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Virtual None) (SensingType.Virtual None)
    let issues = validateApiDefV4 ad None None
    Assert.NotEmpty(issues)
    Assert.Equal("V4", issues.[0].Rule)

[<Fact>]
let ``V4 — Virtual ApiDef with Work.Duration is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Virtual None) (SensingType.Virtual None)
    Assert.Empty(validateApiDefV4 ad (Some 1000) None)

// V7 — Latched + TimePolicy(Append) 는 spec 정의 외 → reject (raw/import 방어).
[<Fact>]
let ``V7 — Latched + Append ActionType emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Latched, Some (Append 100))) (SensingType.Real (Level, None))
    let issues = validateApiDefV7 ad
    Assert.NotEmpty(issues)
    Assert.Equal("V7", issues.Head.Rule)

[<Fact>]
let ``V7 — Latched + Append SensingType emits Error`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (Latched, Some (Append 50)))
    Assert.NotEmpty(validateApiDefV7 ad)

[<Fact>]
let ``V7 — Latched without TimePolicy is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Latched, None)) (SensingType.Real (Latched, None))
    Assert.Empty(validateApiDefV7 ad)

[<Fact>]
let ``V4 — Real ApiDef without Work.Duration is OK`` () =
    let systemId = Guid.NewGuid()
    let ad = makeApiDef "ADV" systemId (ActionType.Real (Level, None)) (SensingType.Real (Level, None))
    Assert.Empty(validateApiDefV4 ad None None)

// V5: ValueSpec type ≡ IOTag.DataType (Warning)
[<Fact>]
let ``V5 — Bool spec vs DINT tag emits Warning`` () =
    let ac = makeApiCall "Cyl1.ADV"
    ac.InputSpec <- BoolValue (Single true)
    let tag = IOTag("X", "X10", "")
    tag.DataType <- IOTagDataType.DINT
    ac.InTag <- Some tag
    let issues = validateApiCallV5 ac
    Assert.NotEmpty(issues)
    Assert.Equal("V5", issues.[0].Rule)
    Assert.Equal(Warning, issues.[0].Severity)

[<Fact>]
let ``V5 — matching types are OK`` () =
    let ac = makeApiCall "Cyl1.ADV"
    ac.InputSpec <- BoolValue (Single true)
    let tag = IOTag("X", "X10", "")
    tag.DataType <- IOTagDataType.BOOL
    ac.InTag <- Some tag
    Assert.Empty(validateApiCallV5 ac)

[<Fact>]
let ``V5 — Undefined spec is OK`` () =
    let ac = makeApiCall "Cyl1.ADV"
    ac.InputSpec <- UndefinedValue
    let tag = IOTag("X", "X10", "")
    tag.DataType <- IOTagDataType.DINT
    ac.InTag <- Some tag
    Assert.Empty(validateApiCallV5 ac)

// V6: Latched ApiCall collision on same Device (Warning)
[<Fact>]
let ``V6 — multiple Latched ApiDefs on same Device emits Warning`` () =
    let deviceId = Guid.NewGuid()
    let ad1 = makeApiDef "ADV" deviceId (ActionType.Real (Latched, None)) (SensingType.Real (Level, None))
    let ad2 = makeApiDef "RET" deviceId (ActionType.Real (Latched, None)) (SensingType.Real (Level, None))
    let issues = validateDeviceV6 deviceId [ ad1; ad2 ]
    Assert.NotEmpty(issues)
    Assert.Equal("V6", issues.[0].Rule)
    Assert.Equal(Warning, issues.[0].Severity)

[<Fact>]
let ``V6 — single Latched ApiDef is OK`` () =
    let deviceId = Guid.NewGuid()
    let ad1 = makeApiDef "ADV" deviceId (ActionType.Real (Latched, None)) (SensingType.Real (Level, None))
    let ad2 = makeApiDef "RET" deviceId (ActionType.Real (Level, None))   (SensingType.Real (Level, None))
    Assert.Empty(validateDeviceV6 deviceId [ ad1; ad2 ])

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
