module Ds2.Aasx.Tests.KpiExportIntegrationTests

open System
open System.IO
open Xunit
open Ds2.Core
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
            Assert.Equal(1, aids)
            Assert.Equal(1, aimcs)
            Assert.Equal(1, opds)
    finally
        for p in [ tmpPath1; tmpPath2 ] do
            if File.Exists p then File.Delete p
