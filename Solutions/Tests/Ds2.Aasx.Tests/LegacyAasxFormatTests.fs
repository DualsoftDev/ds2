module Ds2.Aasx.Tests.LegacyAasxFormatTests

open System
open System.IO
open Xunit
open AasCore.Aas3_1
open Ds2.Aasx
open Ds2.Aasx.AasxSemantics
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder

let private tempAasx (tag: string) =
    Path.Combine(Path.GetTempPath(), $"legacy-format-{tag}-{Guid.NewGuid():N}.aasx")

/// 2026-01 이전 저장본은 요소 idShort 에 고유 번호 접미사가 붙었다(Name1690). 지금 저장본의 프로젝트 SMC 에서
/// 정확한 "Name" Property 를 그렇게 바꿔 구포맷을 재현한다. 바꾼 개수를 돌려줘 시험 자체가 성립했는지 확인한다.
let private suffixProjectNameIdShort (env: AasCore.Aas3_1.Environment) : int =
    let sequenceModel = env.Submodels |> Seq.find (fun sm -> sm.IdShort = SubmodelModelIdShort)
    let projectSmc =
        sequenceModel.SubmodelElements
        |> Seq.pick (function :? SubmodelElementCollection as smc -> Some smc | _ -> None)
    let mutable renamed = 0
    for element in projectSmc.Value do
        match element with
        | :? Property as p when p.IdShort = "Name" ->
            p.IdShort <- "Name1690"
            renamed <- renamed + 1
        | _ -> ()
    renamed

/// 구포맷 파일은 지금 임포터가 값을 하나도 못 읽어 "이름 없는 빈 프로젝트" 로 조용히 열렸다 — 현장에서
/// "AASX 로딩이 안 된다" 로 보고된 증상. 거절 메시지를 돌려주고 대상 store 는 손대지 않아야 한다.
[<Fact>]
let ``an AASX with suffixed element idShorts is rejected instead of opening as an empty project`` () =
    let store = DsStore()
    let projectId = (addProject store "LegacyLine").Id
    let systemId = (addSystem store "Press" projectId true).Id
    (addFlow store "Main" systemId).Id |> ignore

    let modernPath = tempAasx "modern"
    let legacyPath = tempAasx "legacy"
    try
        Assert.True(AasxExporter.exportFromStore store modernPath "" false false)
        let env = AasxFileIO.readEnvironmentOrRaise modernPath
        Assert.True(suffixProjectNameIdShort env > 0, "프로젝트 SMC 에 Name Property 가 있어야 구포맷을 재현할 수 있다")
        AasxFileIO.writeEnvironment env legacyPath None None

        // 대조군 — 같은 모델의 현행 저장본은 이름까지 그대로 열린다.
        let fromModern = DsStore.empty()
        match AasxImporter.importIntoStoreWithError fromModern modernPath with
        | Ok () -> ()
        | Error error -> Assert.Fail error
        Assert.Equal("LegacyLine", (fromModern.Projects.Values |> Seq.head).Name)

        let target = DsStore.empty()
        match AasxImporter.importIntoStoreWithError target legacyPath with
        | Ok () -> Assert.Fail "구포맷 AASX 가 거절되지 않고 열렸다"
        | Error message -> Assert.Contains("구 포맷", message)
        Assert.Empty target.Projects
    finally
        for path in [ modernPath; legacyPath ] do
            if File.Exists path then File.Delete path
