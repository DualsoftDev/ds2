module Ds2.LightHouseService.IntegrationTests.ScaffoldSentinelTests

open Xunit

/// Phase S5a scaffold sentinel — project 자체가 build / test runner 진입 가능한지 confirm.
///
/// 본격 e2e (LightHouseClient ↔ service Program 부팅 round-trip) 는 Phase S5c 진입 시 추가.
/// 본 Fact 는 project structure SSOT 의 single-source 검증 — Phase S5c 진입 시 제거 또는
/// 본격 e2e suite 의 첫 Fact 로 흡수.
[<Fact>]
let ``S5a scaffold — IntegrationTests project 빌드 + xunit 진입 OK`` () =
    Assert.True(true)
