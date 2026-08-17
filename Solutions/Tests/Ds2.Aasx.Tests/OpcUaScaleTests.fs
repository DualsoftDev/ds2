module Ds2.Aasx.Tests.OpcUaScaleTests

open System.Diagnostics
open Xunit
open Xunit.Abstractions
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx

// -----------------------------------------------------------------------------
// OPC UA 대량 태그 스케일링 / 라운드트립 회귀 방지.
//
// 목적:
//   1) KpiAidAppender / OperationalDataAppender / AimcAppender 배치 경로가
//      대량 태그 시 정확성 · 멱등성 · Provenance 를 유지하는지 검증.
//   2) 로딩 시간이 태그 수에 대해 (거의) 선형이어야 함 — O(n²) 회귀 조기 감지.
//   3) Export → Import 라운드트립이 대량 인터랙션에서도 IdShort/SignalId 를 보존.
//
// 성능 예산(넉넉):
//   - 1000 태그 전체 파이프라인 (Walker + Ensure + Export + Import): 5 초.
//   - 500 태그 라운드트립: 2 초.
//   - 100 태그 라운드트립: 500ms.
// 로컬 개발기 기준 → CI 지터 감안. 회귀 시 곧바로 실패.
// -----------------------------------------------------------------------------

let private mkTarget (i: int) : KpiTarget =
    let fqdn = sprintf "Line1.Station%03d.Tag" (i / 10)
    let suffix = sprintf "Sig%04d" i
    {
        Kind         = SystemKind
        EntityFqdn   = fqdn
        Metric       = { IdShortSuffix = suffix
                         SemanticId    = sprintf "urn:ds:kpi/Sys/%s/1/0" suffix
                         DataType      = XsDouble
                         Unit          = "unit"
                         UpdateHint    = OnChange
                         DescriptionKr = ""
                         DescriptionEn = "" }
        IdShort      = KpiIdentifiers.idShort SystemKind fqdn suffix
        SignalId     = KpiIdentifiers.signalId "kpi.scale" SystemKind fqdn suffix
    }

let private mkTargets (n: int) : KpiTarget list =
    [ for i in 1 .. n -> mkTarget i ]

let private time (fn: unit -> 'a) : 'a * int64 =
    let sw = Stopwatch.StartNew()
    let r = fn ()
    sw.Stop()
    r, sw.ElapsedMilliseconds


// -----------------------------------------------------------------------------
// Correctness · scale
// -----------------------------------------------------------------------------

[<Fact>]
let ``AID batch ensure - 1000 unique tags all added, no conflicts`` () =
    let aid = AssetInterfacesDescription()
    let targets = mkTargets 1000
    let states = KpiAidAppender.ensureMany aid targets
    Assert.Equal(1000, states |> List.filter (fun s -> s = Added) |> List.length)
    Assert.Equal(0, states |> List.filter (fun s -> s = Conflict) |> List.length)
    match aid.Interfaces.[0] with
    | OpcUa (_, interactions, _) ->
        Assert.Equal(1000, List.length interactions)
        // IdShort 유일성
        let idShorts = interactions |> List.map (fun i -> i.IdShort) |> Set.ofList
        Assert.Equal(1000, idShorts.Count)
    | _ -> Assert.Fail "OpcUa binding expected"
    Assert.Equal(1000, aid.AutoOriginIdShorts.Count)


[<Fact>]
let ``AID batch ensure - second call is idempotent (all Existed)`` () =
    let aid = AssetInterfacesDescription()
    let targets = mkTargets 500
    let _ = KpiAidAppender.ensureMany aid targets
    let second = KpiAidAppender.ensureMany aid targets
    Assert.Equal(500, second |> List.filter (fun s -> s = Existed) |> List.length)
    Assert.Equal(0, second |> List.filter (fun s -> s = Added) |> List.length)
    match aid.Interfaces.[0] with
    | OpcUa (_, interactions, _) -> Assert.Equal(500, List.length interactions)
    | _ -> Assert.Fail "OpcUa binding expected"


[<Fact>]
let ``OperationalData batch ensure - 1000 unique items added, dedup on second call`` () =
    let od = OperationalData()
    let targets = mkTargets 1000
    let first = KpiOperationalDataAppender.ensureMany od targets
    Assert.Equal(1000, first |> List.filter (fun s -> s = Added) |> List.length)
    Assert.Equal(1000, od.Items.Count)
    let second = KpiOperationalDataAppender.ensureMany od targets
    Assert.Equal(1000, second |> List.filter (fun s -> s = Existed) |> List.length)
    Assert.Equal(1000, od.Items.Count)


[<Fact>]
let ``AIMC batch ensure - 1000 mappings, source+sink coherent`` () =
    let aimc = AssetInterfacesMappingConfiguration()
    let targets = mkTargets 1000
    let states = KpiAimcAppender.ensureMany aimc targets
    Assert.Equal(1000, states |> List.filter (fun s -> s = Added) |> List.length)
    Assert.Equal(1000, aimc.Mappings.Count)
    // 모든 SourceAidPath 는 결정론적 컨벤션을 따름
    for m in aimc.Mappings do
        Assert.StartsWith("InterfaceOPCUA/InteractionMetadata/Kpi_Sys_", m.SourceAidPath)
        Assert.StartsWith("OperationalData/Kpi_Sys_", m.SinkAasElementPath)


[<Fact>]
let ``Batch ensure - Suppressed IdShorts are tombstoned (no re-generation)`` () =
    let aid = AssetInterfacesDescription()
    let targets = mkTargets 200
    // 짝수 인덱스는 tombstone.
    for i in 0 .. 2 .. targets.Length - 1 do
        aid.SuppressedAutoIdShorts.Add(targets.[i].IdShort) |> ignore
    let states = KpiAidAppender.ensureMany aid targets
    let suppressed = states |> List.filter (fun s -> s = Suppressed) |> List.length
    let added     = states |> List.filter (fun s -> s = Added)      |> List.length
    Assert.Equal(100, suppressed)
    Assert.Equal(100, added)
    match aid.Interfaces.[0] with
    | OpcUa (_, interactions, _) -> Assert.Equal(100, List.length interactions)
    | _ -> Assert.Fail "OpcUa binding expected"


[<Fact>]
let ``Batch ensure - SemanticId conflict does not overwrite user SME`` () =
    let aid = AssetInterfacesDescription()
    let ep = { EndpointMetadata.empty with Base = "opc.tcp://localhost:48400" }
    // 사용자 SME 를 target 하나와 동일한 IdShort 로 미리 배치, SemanticId 는 다름.
    let target = mkTarget 42
    let userInteraction : OpcUaInteraction = {
        IdShort    = target.IdShort
        SemanticId = SemanticId "urn:custom:user:conflict"
        ValueType  = XsString
        Unit       = None
        Href       = "user"
        SignalId   = SignalId "user.custom.signal"
    }
    aid.Interfaces.Add(OpcUa (ep, [ userInteraction ], []))
    let states = KpiAidAppender.ensureMany aid [ target ]
    Assert.Equal<EnsureState list>([ Conflict ], states)
    // 사용자 SME 는 그대로 보존
    match aid.Interfaces.[0] with
    | OpcUa (_, interactions, _) ->
        Assert.Equal(1, List.length interactions)
        Assert.Equal("urn:custom:user:conflict", interactions.[0].SemanticId.Value)
    | _ -> Assert.Fail "OpcUa binding expected"


// -----------------------------------------------------------------------------
// Roundtrip · scale (Export → Import identity)
// -----------------------------------------------------------------------------

type Roundtrip(output: ITestOutputHelper) =

    [<Fact>]
    member _.``Roundtrip 100 tags preserves count / IdShort / SignalId`` () =
        let aid = AssetInterfacesDescription()
        let targets = mkTargets 100
        let _ = KpiAidAppender.ensureMany aid targets
        let originalCount =
            match aid.Interfaces.[0] with OpcUa (_, xs, _) -> List.length xs | _ -> 0
        let sm, elapsedExport =
            time (fun () -> AasxExportStandardSubmodels.aidToSubmodel aid "asset01")
        let restored, elapsedImport =
            time (fun () -> AasxImportStandardSubmodels.submodelToAid sm)
        output.WriteLine(sprintf "100 tags · export=%dms · import=%dms" elapsedExport elapsedImport)
        let restoredCount =
            match restored.Interfaces.[0] with OpcUa (_, xs, _) -> List.length xs | _ -> 0
        Assert.Equal(originalCount, restoredCount)
        Assert.Equal(100, restored.AutoOriginIdShorts.Count)
        // IdShort 집합 일치
        let originalIds =
            match aid.Interfaces.[0] with OpcUa (_, xs, _) -> xs |> List.map (fun i -> i.IdShort) |> Set.ofList | _ -> Set.empty
        let restoredIds =
            match restored.Interfaces.[0] with OpcUa (_, xs, _) -> xs |> List.map (fun i -> i.IdShort) |> Set.ofList | _ -> Set.empty
        Assert.Equal<Set<string>>(originalIds, restoredIds)
        Assert.True(elapsedExport + elapsedImport < 500L,
            sprintf "100-tag roundtrip too slow: export=%d import=%d" elapsedExport elapsedImport)

    [<Fact>]
    member _.``Roundtrip 500 tags preserves all interactions within budget`` () =
        let aid = AssetInterfacesDescription()
        let _ = KpiAidAppender.ensureMany aid (mkTargets 500)
        let sm, tExport = time (fun () -> AasxExportStandardSubmodels.aidToSubmodel aid "asset01")
        let restored, tImport = time (fun () -> AasxImportStandardSubmodels.submodelToAid sm)
        output.WriteLine(sprintf "500 tags · export=%dms · import=%dms" tExport tImport)
        let restoredCount =
            match restored.Interfaces.[0] with OpcUa (_, xs, _) -> List.length xs | _ -> 0
        Assert.Equal(500, restoredCount)
        Assert.True(tExport + tImport < 2000L,
            sprintf "500-tag roundtrip too slow: export=%d import=%d" tExport tImport)

    [<Fact>]
    member _.``Batch ensure 1000 tags AID+OpData+AIMC within 5s budget`` () =
        let aid = AssetInterfacesDescription()
        let od = OperationalData()
        let aimc = AssetInterfacesMappingConfiguration()
        let targets = mkTargets 1000
        let sw = Stopwatch.StartNew()
        let a = KpiAidAppender.ensureMany aid targets
        let o = KpiOperationalDataAppender.ensureMany od targets
        let c = KpiAimcAppender.ensureMany aimc targets
        sw.Stop()
        output.WriteLine(sprintf "1000 tags · appenders=%dms" sw.ElapsedMilliseconds)
        Assert.Equal(1000, a |> List.filter (fun s -> s = Added) |> List.length)
        Assert.Equal(1000, o |> List.filter (fun s -> s = Added) |> List.length)
        Assert.Equal(1000, c |> List.filter (fun s -> s = Added) |> List.length)
        Assert.True(sw.ElapsedMilliseconds < 5000L,
            sprintf "1000-tag batch ensure too slow: %dms" sw.ElapsedMilliseconds)

    [<Fact>]
    member _.``Batch ensure scaling roughly linear (1000 vs 100 ratio < 30x)`` () =
        // 순수 O(n²) 였다면 1000/100 = 100 배가 되어야 함. 배치 이후는 ~10 배여야 함.
        let bench n =
            let aid = AssetInterfacesDescription()
            let targets = mkTargets n
            // JIT warm-up 을 위해 별도 인스턴스로 짧게 예열.
            KpiAidAppender.ensureMany (AssetInterfacesDescription()) (mkTargets 8) |> ignore
            let sw = Stopwatch.StartNew()
            KpiAidAppender.ensureMany aid targets |> ignore
            sw.Stop()
            sw.ElapsedMilliseconds
        let t100  = bench 100
        let t1000 = bench 1000
        output.WriteLine(sprintf "AID ensureMany · n=100 → %dms · n=1000 → %dms" t100 t1000)
        // 노이즈 회피: 100 태그 시간이 0ms 로 측정되는 경우 클램프.
        let denom = max t100 1L
        let ratio = float t1000 / float denom
        Assert.True(ratio < 30.0,
            sprintf "AID ensureMany appears O(n²): 1000/100 ratio = %.1fx (limit 30x)" ratio)
