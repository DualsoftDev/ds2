module Ds2.Aasx.Tests.SequenceKpiGeneratorTests

open Xunit
open Xunit.Abstractions
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx.Tests.KpiFixtures

// -----------------------------------------------------------------------------
// Convention layer
// -----------------------------------------------------------------------------

[<Fact>]
let ``KpiKits · all 5 kits present`` () =
    let kinds = KpiKits.all |> List.map (fun k -> k.Kind)
    Assert.Contains(SystemKind, kinds)
    Assert.Contains(WorkKind, kinds)
    Assert.Contains(CallKind, kinds)
    Assert.Contains(ArrowWorkKind, kinds)
    Assert.Contains(UserTagKind, kinds)

[<Fact>]
let ``KpiKits · systemKit contains OEE + Availability + Performance + Quality + MTBF + MTTR`` () =
    let names = KpiKits.systemKit.Metrics |> List.map (fun m -> m.IdShortSuffix) |> Set.ofList
    Assert.True(names.Contains "OEE")
    Assert.True(names.Contains "Availability")
    Assert.True(names.Contains "Performance")
    Assert.True(names.Contains "Quality")
    Assert.True(names.Contains "MTBF")
    Assert.True(names.Contains "MTTR")

[<Fact>]
let ``KpiKits · workKit contains CT + MT + IdleTime + DowntimeCount`` () =
    let names = KpiKits.workKit.Metrics |> List.map (fun m -> m.IdShortSuffix) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ["CT"; "MT"; "IdleTime"; "DowntimeCount"], names)

[<Fact>]
let ``KpiKits · SemanticId format follows urn:ds:kpi convention`` () =
    for kit in KpiKits.all do
        for m in kit.Metrics do
            Assert.StartsWith("urn:ds:kpi/", m.SemanticId)
            Assert.EndsWith("/1/0", m.SemanticId)

// -----------------------------------------------------------------------------
// Identifiers
// -----------------------------------------------------------------------------

[<Fact>]
let ``KpiIdentifiers · hash8 is deterministic and 8 chars`` () =
    let h1 = KpiIdentifiers.entityHash8 "Line1.MainFlow.PickUp"
    let h2 = KpiIdentifiers.entityHash8 "Line1.MainFlow.PickUp"
    Assert.Equal(h1, h2)
    Assert.Equal(8, h1.Length)
    Assert.All(h1, fun c -> Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

[<Fact>]
let ``KpiIdentifiers · idShort under 128 chars (AAS limit) + starts with Kpi_ prefix`` () =
    let idShort = KpiIdentifiers.idShort SystemKind "Line1.MainFlow.SomeWork" "OEE"
    Assert.True(idShort.Length < 128)
    Assert.StartsWith("Kpi_Sys_", idShort)
    Assert.Contains("OEE", idShort)
    // AAS idShort 규격 검증: 오직 [A-Za-z0-9_] 만
    Assert.All(idShort, fun c ->
        Assert.True((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'))

[<Fact>]
let ``KpiIdentifiers · idShort sanitizes special chars (real data resilience)`` () =
    // Korean + '#' + spaces + dots + hyphens — 실전 Fuji PLC 사용자 태그 케이스
    let idShort = KpiIdentifiers.idShort UserTagKind "Line1.something" "#100 포지션.리프트-하강_I"
    Assert.All(idShort, fun c ->
        Assert.True((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'))
    Assert.True(idShort.Length < 128)

[<Fact>]
let ``KpiIdentifiers · signalId is valid Ds2.Core.SignalId`` () =
    let sid = KpiIdentifiers.signalId "kpi.line1" WorkKind "Line1.Flow.PickUp" "CT"
    Assert.True(sid.Value = sid.Value.ToLowerInvariant())
    Assert.DoesNotContain(' ', sid.Value)
    // SignalId 규격 [a-z0-9-_.] 확인
    Assert.All(sid.Value, fun c ->
        Assert.True((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_' || c = '.'))
    let sid2 = KpiIdentifiers.signalId "kpi.p1" CallKind "Sys.Flow.Work.Call" "LastDurationMs"
    Assert.Contains("last-duration-ms", sid2.Value)

[<Fact>]
let ``KpiIdentifiers · signalId sanitizes special chars (real data resilience)`` () =
    let sid = KpiIdentifiers.signalId "kpi.foo" UserTagKind "Sys.Flow.Work.Call" "#100 포지션.리프트-하강_I"
    Assert.All(sid.Value, fun c ->
        Assert.True((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_' || c = '.'))

// -----------------------------------------------------------------------------
// Walker · Kit-별 KPI 수 검증
// -----------------------------------------------------------------------------

/// Walker 정책: Work/Call KPI 는 생성하지 않음 (KpiWalker.fs).
/// System=6 + Arrow×1×2 + Tag×2×1 = 6+2+2 = 10
[<Fact>]
let ``KpiWalker · small project produces expected KPI count`` () =
    let store, project = buildSmallProject ()
    let targets = KpiWalker.walk store project

    let count kind = targets |> List.filter (fun t -> t.Kind = kind) |> List.length

    // 각 카테고리를 개별 검증하여 회귀 원인 파악 용이하게.
    Assert.Equal(6, count SystemKind)     // 1 sys × 6 metrics
    Assert.Equal(0, count WorkKind)       // Work KPI skip 정책
    Assert.Equal(0, count CallKind)       // Call KPI skip 정책
    Assert.Equal(2, count ArrowWorkKind)  // 1 arrow × 2 metrics
    Assert.Equal(2, count UserTagKind)    // 2 tags × 1 metric
    Assert.Equal(10, targets.Length)

[<Fact>]
let ``KpiWalker · System + ArrowWork + UserTag kinds produce targets (Work/Call skipped)`` () =
    let store, project = buildSmallProject ()
    let targets = KpiWalker.walk store project
    let kinds = targets |> List.map (fun t -> t.Kind) |> Set.ofList
    Assert.True(kinds.Contains SystemKind)
    Assert.False(kinds.Contains WorkKind)    // Walker skip 정책
    Assert.False(kinds.Contains CallKind)    // Walker skip 정책
    Assert.True(kinds.Contains ArrowWorkKind)
    Assert.True(kinds.Contains UserTagKind)

[<Fact>]
let ``KpiWalker · signalId uniqueness — no duplicate signals across targets`` () =
    let store, project = buildSmallProject ()
    let targets = KpiWalker.walk store project
    let signals = targets |> List.map (fun t -> t.SignalId.Value)
    let unique = signals |> Set.ofList
    Assert.Equal(signals.Length, unique.Count)

[<Fact>]
let ``SequenceKpiGenerator · passive-only legacy project stays untouched`` () =
    let store = Ds2.Core.Store.DsStore()
    let project = Project("LegacyPassiveOnly")
    store.Projects.[project.Id] <- project
    let passive = DsSystem("PassiveDevice")
    store.Systems.[passive.Id] <- passive
    project.PassiveSystemIds.Add(passive.Id)

    let stats = SequenceKpiGenerator.appendForProject store project

    Assert.Equal(0, stats.Walked)
    Assert.True(project.AssetInterfaces.IsNone)
    Assert.True(project.AssetInterfacesMapping.IsNone)
    Assert.True(project.OperationalDataDef.IsNone)

// -----------------------------------------------------------------------------
// Generator (end-to-end append)
// -----------------------------------------------------------------------------

[<Fact>]
let ``SequenceKpiGenerator · appendForProject populates 3 submodels`` () =
    let store, project = buildSmallProject ()

    Assert.True(project.AssetInterfaces.IsNone)
    Assert.True(project.OperationalDataDef.IsNone)
    Assert.True(project.AssetInterfacesMapping.IsNone)

    let stats = SequenceKpiGenerator.appendForProject store project

    Assert.True(project.AssetInterfaces.IsSome)
    Assert.True(project.OperationalDataDef.IsSome)
    Assert.True(project.AssetInterfacesMapping.IsSome)

    Assert.Equal(10, stats.Walked)
    Assert.Equal(10, stats.AidAdded)
    Assert.Equal(10, stats.OpDataAdded)
    Assert.Equal(10, stats.AimcAdded)
    Assert.Equal(0, stats.Conflicts)

[<Fact>]
let ``SequenceKpiGenerator · idempotent — second run skips all`` () =
    let store, project = buildSmallProject ()
    let first  = SequenceKpiGenerator.appendForProject store project
    let second = SequenceKpiGenerator.appendForProject store project

    Assert.Equal(10, first.AidAdded)

    Assert.Equal(0, second.AidAdded)
    Assert.Equal(10, second.AidExisted)
    Assert.Equal(0, second.OpDataAdded)
    Assert.Equal(10, second.OpDataExisted)
    Assert.Equal(0, second.AimcAdded)
    Assert.Equal(10, second.AimcExisted)
    Assert.Equal(0, second.Conflicts)

[<Fact>]
let ``SequenceKpiGenerator · preserves existing user SME (append-only)`` () =
    let store, project = buildSmallProject ()

    let userItem = OperationalDataItem()
    userItem.IdShort <- "UserCustomSignal"
    userItem.SemanticId <- SemanticId "urn:custom:user:1/0"
    userItem.ValueType <- XsDouble
    let od = OperationalData()
    od.Items.Add(userItem)
    project.OperationalDataDef <- Some od

    let _stats = SequenceKpiGenerator.appendForProject store project

    let userSurvived =
        (project.OperationalDataDef.Value.Items
         |> Seq.exists (fun i -> i.IdShort = "UserCustomSignal"))
    Assert.True(userSurvived)
    Assert.Equal(11, project.OperationalDataDef.Value.Items.Count)

[<Fact>]
let ``SequenceKpiGenerator · AIMC mapping source and sink coherence`` () =
    let store, project = buildSmallProject ()
    let _ = SequenceKpiGenerator.appendForProject store project

    let aidItems =
        match project.AssetInterfaces with
        | Some aid ->
            aid.Interfaces
            |> Seq.collect (fun b ->
                match b with
                | OpcUa (_, xs, _) -> xs |> Seq.map (fun i -> i.IdShort)
                | _ -> Seq.empty)
            |> Set.ofSeq
        | None -> Set.empty

    let opDataItems =
        match project.OperationalDataDef with
        | Some od -> od.Items |> Seq.map (fun i -> i.IdShort) |> Set.ofSeq
        | None -> Set.empty

    // KPI 항목 개수 일치
    let aidCount = aidItems |> Set.filter (fun s -> s.StartsWith "Kpi_") |> Set.count
    let opCount  = opDataItems |> Set.filter (fun s -> s.StartsWith "Kpi_") |> Set.count
    Assert.Equal(10, aidCount)
    Assert.Equal(10, opCount)

    match project.AssetInterfacesMapping with
    | Some aimc ->
        for m in aimc.Mappings do
            let srcId = m.SourceAidPath.Substring(m.SourceAidPath.LastIndexOf('/') + 1)
            let sinkId = m.SinkAasElementPath.Substring(m.SinkAasElementPath.LastIndexOf('/') + 1)
            Assert.True(aidItems.Contains srcId, sprintf "AID interaction missing: %s" srcId)
            Assert.True(opDataItems.Contains sinkId, sprintf "OpData item missing: %s" sinkId)
    | None -> Assert.True(false, "AIMC missing")


// -----------------------------------------------------------------------------
// Debug helper — 실패 시 어떤 카테고리가 문제인지 즉시 파악하기 위한 진단 테스트
// -----------------------------------------------------------------------------

type DiagOutput(output: ITestOutputHelper) =
    [<Fact>]
    member _.``DIAG · dump walker targets by kind`` () =
        let store, project = buildSmallProject ()
        let targets = KpiWalker.walk store project
        for kind in [ SystemKind; WorkKind; CallKind; ArrowWorkKind; UserTagKind ] do
            let ts = targets |> List.filter (fun t -> t.Kind = kind)
            output.WriteLine(sprintf "%A · %d targets" kind ts.Length)
            for t in ts do
                output.WriteLine(sprintf "  %s / %s (%s)" t.EntityFqdn t.Metric.IdShortSuffix t.SignalId.Value)
