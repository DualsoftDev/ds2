module Ds2.Aasx.Tests.AasxSemanticsTests

open Ds2.Aasx.AasxSemantics
open Xunit

// Phase 1 · 신규 IDTA / DualSoft semanticId 상수 계약 회귀.
// 상수 값이 표준 · ADR 과 일치하는지 확인.

[<Fact>]
let ``AID semanticId matches IDTA 02017 v1.1`` () =
    Assert.Equal(
        "https://admin-shell.io/idta/AssetInterfacesDescription/1/1/Submodel",
        AidSubmodelSemanticId)
    Assert.Equal("AssetInterfacesDescription", AidSubmodelIdShort)

[<Fact>]
let ``AIMC semanticId matches IDTA 02027 v2.0`` () =
    Assert.Equal(
        "https://admin-shell.io/idta/AssetInterfacesMappingConfiguration/2/0/Submodel",
        AimcSubmodelSemanticId)

[<Fact>]
let ``OperationalData semanticId uses CdBaseUrl (사내 발행)`` () =
    Assert.StartsWith(CdBaseUrl, OperationalDataSubmodelSemanticId)
    Assert.EndsWith("/sm/OperationalData/1/0", OperationalDataSubmodelSemanticId)

[<Fact>]
let ``OperationalData type default SemanticId matches AasxSemantics constant`` () =
    // Ds2.Core.OperationalData 기본값 은 반드시 Ds2.Aasx.AasxSemantics 의 상수와 일치.
    // Ds2.Core 는 Ds2.Aasx 를 참조할 수 없어 문자열 하드코딩 방식 — 이 테스트가 안전망.
    let op = Ds2.Core.StandardSubmodels.OperationalDataTypes.OperationalData()
    Assert.Equal(OperationalDataSubmodelSemanticId, op.SemanticId.Value)

[<Fact>]
let ``SignalId extension semanticId uses cd namespace`` () =
    Assert.EndsWith("/cd/ext.signal-id/1/0", SignalIdExtensionSemanticId)

[<Fact>]
let ``VaultReference extension semanticId defined`` () =
    Assert.EndsWith("/cd/ext.vault-reference/1/0", VaultReferenceExtensionSemanticId)

[<Fact>]
let ``SignalPoliciesCollection semanticId nested under SequenceLogging`` () =
    // CollectionPolicy 흡수 · Logging SM 안의 SMC.
    Assert.Contains("sm/SequenceLogging/SignalPoliciesCollection", SignalPoliciesCollectionSemanticId)

[<Fact>]
let ``SubmodelType AllDomains keeps legacy sequence-only contract`` () =
    let idShorts = Ds2.Core.SubmodelType.AllDomains |> List.map (fun sm -> sm.IdShort)
    Assert.Equal(8, idShorts.Length)
    Assert.DoesNotContain(AidSubmodelIdShort, idShorts)
    Assert.DoesNotContain(AimcSubmodelIdShort, idShorts)
    Assert.DoesNotContain(OperationalDataSubmodelIdShort, idShorts)
