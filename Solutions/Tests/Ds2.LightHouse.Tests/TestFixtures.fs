namespace Ds2.LightHouse.Tests

open Xunit

/// **s6-r22 mn7** — Test fixture SSOT. 동일 byte literal 의 3 caller 중복 회피.
///
/// 도입 이유: `samplePngBytes` 가 `IndexerTests.fs` / `OoxmlExtractorTests.fs` / `ImageStoreTests.fs` (그리고
/// 별 project `Ds2.LightHouseService.Tests` 의 `AttachmentToolsTests.fs`) 4곳에 동일 byte literal 로 복제됨.
/// drift 시 sha256 회귀 detection 회피 위험.
///
/// SSOT: 본 module 의 `SamplePng.bytes` 가 lib test project 의 단일 진입점. 별 project (Service.Tests) 는
/// 동일 byte literal 의 sibling module (`Ds2.LightHouseService.Tests.TestFixtures`) 을 유지 — ProjectReference
/// 부담 회피, byte literal drift 시 컴파일러 회귀 차단 (`computeSha256` 결과가 두 module 에서 동일해야 e2e 정합).
///
/// **s6-r23 m1 (자가 검열 적용)** — `ExpectedSha256` 상수 박제 + `byte literal drift detection` fact.
/// 별 project sibling module 과 동일 hash 상수를 박제하므로, lib/service 측 어느 한쪽 byte literal 이라도
/// 변경되면 sha256 fact 가 expected 와 mismatch 로 fail → cross-project silent drift 명시 detection.
[<RequireQualifiedAccess>]
module SamplePng =

    /// 1×1 px PNG deterministic bytes — sha256 결정성 회귀 차단 의도.
    /// 8-byte signature + IHDR + IDAT + IEND. zlib-compressed 1 pixel.
    let bytes : byte[] =
        [|
            0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy
            0x00uy; 0x00uy; 0x00uy; 0x0Duy; 0x49uy; 0x48uy; 0x44uy; 0x52uy
            0x00uy; 0x00uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x00uy; 0x01uy
            0x08uy; 0x06uy; 0x00uy; 0x00uy; 0x00uy
            0x1Fuy; 0x15uy; 0xC4uy; 0x89uy
            0x00uy; 0x00uy; 0x00uy; 0x0Auy; 0x49uy; 0x44uy; 0x41uy; 0x54uy
            0x78uy; 0x9Cuy; 0x63uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x05uy; 0x00uy; 0x01uy
            0x0Duy; 0x0Auy; 0x2Duy; 0xB4uy
            0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x49uy; 0x45uy; 0x4Euy; 0x44uy
            0xAEuy; 0x42uy; 0x60uy; 0x82uy
        |]

    /// **s6-r23 m1** — `Ds2.LightHouse.ImageStore.computeSha256 bytes` 의 결정적 결과 SSOT.
    /// 두 sibling module (lib / service) 이 동일 값 박제. byte literal drift 시 명시 fail.
    /// 변경 시: 새 hash 를 lib + service 양쪽 박제 후 ImageStore-기반 e2e fact 도 정합 갱신.
    [<Literal>]
    let ExpectedSha256 = "ebf4f635a17d10d6eb46ba680b70142419aa3220f228001a036d311a22ee9d2a"


module SamplePngFacts =

    [<Fact>]
    let ``SamplePng.bytes — sha256 결정성 + cross-project drift detection (s6-r23 m1)`` () =
        Assert.Equal(SamplePng.ExpectedSha256, Ds2.LightHouse.ImageStore.computeSha256 SamplePng.bytes)
