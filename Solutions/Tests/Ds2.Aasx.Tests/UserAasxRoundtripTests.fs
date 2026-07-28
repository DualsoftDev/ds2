module Ds2.Aasx.Tests.UserAasxRoundtripTests

open System
open System.IO
open Xunit
open Ds2.Core.Store
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics

/// 실제 사용자 파일 `우진_xgk.aasx` load → 저장 시 AID/AIMC/OperationalData 3 서브모델이 생성되는지 실측.
/// 파일 없으면 skip.
[<Fact>]
let ``real user aasx · KPI submodels appear after Promaker-style save`` () =
    let userPath = @"C:\Users\dual\Music\우진_xgk.aasx"
    if not (File.Exists userPath) then
        // CI 환경에는 이 파일이 없으므로 skip
        ()
    else
        // 1. Load
        let store = DsStore()
        AasxImporter.importIntoStoreOrRaise store userPath

        // 2. Save (KPI 자동생성 훅이 여기서 실행되어야 함)
        let tmpPath = Path.Combine(Path.GetTempPath(), sprintf "user_export_%s.aasx" (Guid.NewGuid().ToString("N")))
        try
            let ok = AasxExporter.exportFromStore store tmpPath "" false true
            Assert.True(ok, "exportFromStore returned false")

            // 3. 재로드해서 AID/AIMC/OperationalData 존재 확인
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
