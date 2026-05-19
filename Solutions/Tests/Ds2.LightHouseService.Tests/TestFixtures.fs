namespace Ds2.LightHouseService.Tests

open Xunit

/// **s6-r22 mn7** — Test fixture SSOT (sibling module of `Ds2.LightHouse.Tests.SamplePng`).
///
/// 별 project 의 동일 byte literal — ProjectReference 부담 회피.
/// byte literal drift 시 두 module 의 `ImageStore.computeSha256` 산출 hash 가 갈리며 e2e fact 깨짐 → 회귀 detection.
///
/// **s6-r23 m1 (자가 검열 적용)** — `ExpectedSha256` 상수 박제 + drift detection fact. lib sibling module 과
/// 동일 hash 박제 의무 — 둘 중 하나라도 byte literal 변경 시 sha256 fact 양쪽 모두 fail (drift 명시).
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

    /// **s6-r23 m1** — lib sibling 의 `Ds2.LightHouse.Tests.SamplePng.ExpectedSha256` 와 동일 hash.
    /// drift 시 양쪽 fact 모두 fail (cross-project drift 명시 detection).
    [<Literal>]
    let ExpectedSha256 = "ebf4f635a17d10d6eb46ba680b70142419aa3220f228001a036d311a22ee9d2a"


module SamplePngFacts =

    [<Fact>]
    let ``SamplePng.bytes — sha256 결정성 + cross-project drift detection (s6-r23 m1)`` () =
        Assert.Equal(SamplePng.ExpectedSha256, Ds2.LightHouse.ImageStore.computeSha256 SamplePng.bytes)
