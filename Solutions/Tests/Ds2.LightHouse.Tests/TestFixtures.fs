namespace Ds2.LightHouse.Tests

/// **s6-r22 mn7** — Test fixture SSOT. 동일 byte literal 의 3 caller 중복 회피.
///
/// 도입 이유: `samplePngBytes` 가 `IndexerTests.fs` / `OoxmlExtractorTests.fs` / `ImageStoreTests.fs` (그리고
/// 별 project `Ds2.LightHouseService.Tests` 의 `AttachmentToolsTests.fs`) 4곳에 동일 byte literal 로 복제됨.
/// drift 시 sha256 회귀 detection 회피 위험.
///
/// SSOT: 본 module 의 `SamplePng.bytes` 가 lib test project 의 단일 진입점. 별 project (Service.Tests) 는
/// 동일 byte literal 의 sibling module (`Ds2.LightHouseService.Tests.TestFixtures`) 을 유지 — ProjectReference
/// 부담 회피, byte literal drift 시 컴파일러 회귀 차단 (`computeSha256` 결과가 두 module 에서 동일해야 e2e 정합).
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
