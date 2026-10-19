module Ds2.Aasx.Tests.KpiExportIntegrationTests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Core.StandardSubmodels
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.Tests.KpiFixtures

/// End-to-end 검증: Promaker save 경로 (`exportToAasxFile`) 실행 시
/// AID / AIMC / OperationalData 3 서브모델이 실제 파일에 포함되는지 확인.

[<Fact>]
let ``exportToAasxFile · KPI 3 submodels are emitted for a sequence project`` () =
    let store, _ = buildSmallProject ()

    let tmpPath = Path.Combine(Path.GetTempPath(), sprintf "kpi_export_%s.aasx" (Guid.NewGuid().ToString("N")))
    try
        let ok = AasxExporter.exportFromStore store tmpPath "" false true
        Assert.True(ok, "exportFromStore returned false")

        // 파일 재로드 후 SM idShort 목록 검사
        let env = AasxFileIO.readEnvironmentOrRaise tmpPath
        let idShorts =
            env.Submodels
            |> Seq.map (fun sm -> sm.IdShort)
            |> Set.ofSeq

        Assert.Contains(AidSubmodelIdShort, idShorts)
        Assert.Contains(AimcSubmodelIdShort, idShorts)
        Assert.Contains(OperationalDataSubmodelIdShort, idShorts)
        // Data API 미설정이면 TimeSeries 는 쓰지 않는다(2026-10-13).
        Assert.DoesNotContain(TimeSeriesSubmodelIdShort, idShorts)
    finally
        if File.Exists tmpPath then File.Delete tmpPath

[<Fact>]
let ``exportFromStore split path (SplitDeviceAasx=true) also emits KPI submodels`` () =
    let store, _ = buildSmallProject ()
    let tmpPath = Path.Combine(Path.GetTempPath(), sprintf "kpi_split_%s.aasx" (Guid.NewGuid().ToString("N")))
    let expectedDevicesDir = Path.Combine(Path.GetDirectoryName(tmpPath), Path.GetFileNameWithoutExtension(tmpPath) + "_devices")
    try
        let ok = AasxExporter.exportFromStore store tmpPath "" true true
        Assert.True(ok, "exportFromStore split=true returned false")

        let env = AasxFileIO.readEnvironmentOrRaise tmpPath
        let idShorts = env.Submodels |> Seq.map (fun sm -> sm.IdShort) |> Set.ofSeq
        Assert.Contains(AidSubmodelIdShort, idShorts)
        Assert.Contains(AimcSubmodelIdShort, idShorts)
        Assert.Contains(OperationalDataSubmodelIdShort, idShorts)
        // Data API 미설정이면 TimeSeries 는 쓰지 않는다(2026-10-13).
        Assert.DoesNotContain(TimeSeriesSubmodelIdShort, idShorts)
    finally
        if File.Exists tmpPath then File.Delete tmpPath
        if Directory.Exists expectedDevicesDir then Directory.Delete(expectedDevicesDir, true)

[<Fact>]
let ``exportToAasxFile · idempotent re-save keeps single AID with all KPI interactions`` () =
    let store, _ = buildSmallProject ()
    let tmpPath1 = Path.Combine(Path.GetTempPath(), sprintf "kpi_export_a_%s.aasx" (Guid.NewGuid().ToString("N")))
    let tmpPath2 = Path.Combine(Path.GetTempPath(), sprintf "kpi_export_b_%s.aasx" (Guid.NewGuid().ToString("N")))
    try
        AasxExporter.exportFromStore store tmpPath1 "" false true |> ignore
        AasxExporter.exportFromStore store tmpPath2 "" false true |> ignore

        // 두 저장 파일에서 AID/AIMC/OpData idShort 는 정확히 1 개씩
        for path in [ tmpPath1; tmpPath2 ] do
            let env = AasxFileIO.readEnvironmentOrRaise path
            let aids = env.Submodels |> Seq.filter (fun sm -> sm.IdShort = AidSubmodelIdShort) |> Seq.length
            let aimcs = env.Submodels |> Seq.filter (fun sm -> sm.IdShort = AimcSubmodelIdShort) |> Seq.length
            let opds = env.Submodels |> Seq.filter (fun sm -> sm.IdShort = OperationalDataSubmodelIdShort) |> Seq.length
            let series = env.Submodels |> Seq.filter (fun sm -> sm.IdShort = TimeSeriesSubmodelIdShort) |> Seq.length
            Assert.Equal(1, aids)
            Assert.Equal(1, aimcs)
            Assert.Equal(1, opds)
            Assert.Equal(0, series)
    finally
        for p in [ tmpPath1; tmpPath2 ] do
            if File.Exists p then File.Delete p

// ── 2026-10-13: TimeSeries 는 Data API 가 설정된 경우에만, Auto 신호는 제외 ─────────────────────
// 같은 클래스(모듈) 안에 두어 위의 "TimeSeries 없음" 테스트와 환경변수가 겹치지 않게 순차 실행된다.

[<Fact>]
let ``TimeSeries is emitted only when DS2_DATA_API_PUBLIC_URL is configured, and skips Auto signals`` () =
    let store, _ = buildSmallProject ()
    let tmpPath = Path.Combine(Path.GetTempPath(), sprintf "kpi_ts_%s.aasx" (Guid.NewGuid().ToString("N")))
    let envName = "DS2_DATA_API_PUBLIC_URL"
    let previous = Environment.GetEnvironmentVariable envName
    try
        Environment.SetEnvironmentVariable(envName, "http://127.0.0.1:62542")
        let ok = AasxExporter.exportFromStore store tmpPath "" false true
        Assert.True(ok, "exportFromStore returned false")
        let env = AasxFileIO.readEnvironmentOrRaise tmpPath
        let series =
            env.Submodels
            |> Seq.filter (fun sm -> sm.IdShort = TimeSeriesSubmodelIdShort)
            |> Seq.toList
        Assert.Single(series) |> ignore
        let sm = series.Head :?> AasCore.Aas3_1.Submodel
        let rec descend (e: AasCore.Aas3_1.ISubmodelElement) : seq<AasCore.Aas3_1.ISubmodelElement> =
            seq {
                yield e
                match e with
                | :? AasCore.Aas3_1.SubmodelElementCollection as smc when not (isNull smc.Value) ->
                    for c in smc.Value do yield! descend c
                | :? AasCore.Aas3_1.SubmodelElementList as sml when not (isNull sml.Value) ->
                    for c in sml.Value do yield! descend c
                | _ -> ()
            }
        // 내보낸 AID 에서 Auto(dualsoft:origin=Auto) 로 표시된 interaction idShort 집합
        let aidSm =
            env.Submodels |> Seq.find (fun x -> x.IdShort = AidSubmodelIdShort) :?> AasCore.Aas3_1.Submodel
        let autoIdShorts =
            aidSm.SubmodelElements
            |> Seq.collect descend
            |> Seq.filter (fun e ->
                not (isNull e.Qualifiers)
                && e.Qualifiers |> Seq.exists (fun q -> q.Value = "Auto"))
            |> Seq.map (fun e -> e.IdShort)
            |> Set.ofSeq
        Assert.NotEmpty(autoIdShorts)
        // Segments 의 각 LinkedSegment 는 Name(mlp) 에 신호 idShort 를 담는다 — Auto 는 하나도 없어야 한다.
        let segments =
            sm.SubmodelElements
            |> Seq.pick (fun e ->
                match e with
                | :? AasCore.Aas3_1.SubmodelElementCollection as smc when smc.IdShort = "Segments" -> Some smc
                | _ -> None)
        let segmentNames =
            (if isNull segments.Value then Seq.empty else segments.Value :> seq<_>)
            |> Seq.choose (fun seg ->
                match seg with
                | :? AasCore.Aas3_1.SubmodelElementCollection as smc when not (isNull smc.Value) ->
                    smc.Value
                    |> Seq.tryPick (fun c ->
                        match c with
                        | :? AasCore.Aas3_1.MultiLanguageProperty as mlp when mlp.IdShort = "Name" ->
                            Some (mlp.Value.[0].Text)
                        | _ -> None)
                | _ -> None)
            |> Seq.toList
        for name in segmentNames do
            Assert.DoesNotContain(name, autoIdShorts)
    finally
        Environment.SetEnvironmentVariable(envName, previous)
        if File.Exists tmpPath then File.Delete tmpPath

[<Fact>]
let ``AidBindingLifecycle drops bindings whose System is gone and keeps unowned ones`` () =
    let live = Guid.NewGuid()
    let dead = Guid.NewGuid()
    let aid = AssetInterfacesDescription()
    aid.Interfaces.Add(Xgt ({ XgtEndpointMetadata.empty with SystemId = Some live }, []))
    aid.Interfaces.Add(Xgt ({ XgtEndpointMetadata.empty with SystemId = Some dead }, []))
    aid.Interfaces.Add(Xgt ({ XgtEndpointMetadata.empty with SystemId = None }, []))
    aid.Interfaces.Add(OpcUa ({ EndpointMetadata.empty with SystemId = Some dead }, [], []))
    aid.AutoOriginIdShorts.Add("Kpi_X") |> ignore

    let pruned, removed = AidBindingLifecycle.withoutDeadBindings (aid, [ live ])

    Assert.Equal(2, removed)
    Assert.Equal(2, pruned.Interfaces.Count)
    Assert.Equal(4, aid.Interfaces.Count) // 원본은 그대로
    Assert.Contains("Kpi_X", pruned.AutoOriginIdShorts)
    let owners = pruned.Interfaces |> Seq.map AidBindingLifecycle.bindingSystemId |> Seq.toList
    Assert.Contains(Some live, owners)
    Assert.Contains(None, owners)

    // 뺄 것이 없으면 원본 인스턴스를 그대로 돌려준다.
    let same, none = AidBindingLifecycle.withoutDeadBindings (pruned, [ live ])
    Assert.Equal(0, none)
    Assert.Same(pruned, same)

/// 잘못 설정된 DS2_DATA_API_PUBLIC_URL 하나가 AASX 저장 전체를 막았다(Uri 파싱·규칙 검사의 예외가 export 를
/// 타고 올라옴). 환경변수는 TimeSeries 의 선택 사항일 뿐이니 저장은 되고 TimeSeries 만 빠진다.
[<Theory>]
[<InlineData("garbage value")>]
[<InlineData("ftp://data-api.example.com/v1")>]
[<InlineData("http://data-api.example.com")>]
let ``a malformed DS2_DATA_API_PUBLIC_URL does not block saving — only TimeSeries is skipped`` (value: string) =
    let store, _ = buildSmallProject ()
    let envName = "DS2_DATA_API_PUBLIC_URL"
    let previous = Environment.GetEnvironmentVariable envName
    let tmpPath = Path.Combine(Path.GetTempPath(), sprintf "kpi_bad_env_%s.aasx" (Guid.NewGuid().ToString("N")))
    try
        Environment.SetEnvironmentVariable(envName, value)

        Assert.True(AasxExporter.exportFromStore store tmpPath "" false true, "잘못된 환경변수로 저장이 실패했다")

        let env = AasxFileIO.readEnvironmentOrRaise tmpPath
        let idShorts = env.Submodels |> Seq.map (fun sm -> sm.IdShort) |> Set.ofSeq
        Assert.Contains(AidSubmodelIdShort, idShorts)
        Assert.DoesNotContain(TimeSeriesSubmodelIdShort, idShorts)
    finally
        Environment.SetEnvironmentVariable(envName, previous)
        if File.Exists tmpPath then File.Delete tmpPath
