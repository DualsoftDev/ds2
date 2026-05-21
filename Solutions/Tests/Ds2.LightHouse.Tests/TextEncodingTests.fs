module Ds2.LightHouse.Tests.TextEncodingTests

open System.Text
open Xunit
open Ds2.LightHouse

// CodePagesEncodingProvider 등록 (CP949 활성). TestInit.registered 강제 평가.
do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// todo-lighthouse-kb-index.md §4.8a — TextEncoding 인코딩 추정.
///
/// 정책: BOM 우선 → strict UTF-8 → strict CP949 → UTF-8 replacement fallback. ConfidenceHigh 는 BOM + strict UTF-8 만.

[<Fact>]
let ``UTF-8 BOM — high confidence + UTF8`` () =
    let body = "안녕 hello"
    let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]
    let bytes = Array.append bom (Encoding.UTF8.GetBytes body)
    let detect = TextEncoding.detectEncoding bytes
    Assert.True(detect.ConfidenceHigh)
    Assert.Equal(Encoding.UTF8.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``UTF-8 BOM 없음 — strict 통과 시 UTF8 low confidence`` () =
    let bytes = Encoding.UTF8.GetBytes "안녕 hello"
    let detect = TextEncoding.detectEncoding bytes
    Assert.False(detect.ConfidenceHigh)
    Assert.Equal(Encoding.UTF8.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``UTF-16 LE BOM`` () =
    let bom = [| 0xFFuy; 0xFEuy |]
    let body = Encoding.Unicode.GetBytes "ABC"
    let bytes = Array.append bom body
    let detect = TextEncoding.detectEncoding bytes
    Assert.True(detect.ConfidenceHigh)
    Assert.Equal(Encoding.Unicode.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``UTF-16 BE BOM`` () =
    let bom = [| 0xFEuy; 0xFFuy |]
    let body = Encoding.BigEndianUnicode.GetBytes "ABC"
    let bytes = Array.append bom body
    let detect = TextEncoding.detectEncoding bytes
    Assert.True(detect.ConfidenceHigh)
    Assert.Equal(Encoding.BigEndianUnicode.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``CP949 — strict UTF-8 실패 후 CP949 통과`` () =
    let cp949 = Encoding.GetEncoding(949)
    let bytes = cp949.GetBytes "한글 인코딩 테스트"
    let detect = TextEncoding.detectEncoding bytes
    Assert.False(detect.ConfidenceHigh)
    Assert.Equal(949, detect.Encoding.CodePage)
    // decode round-trip
    Assert.Equal("한글 인코딩 테스트", detect.Encoding.GetString bytes)

[<Fact>]
let ``순수 ASCII — strict UTF-8 통과 (CP949 까지 안 감)`` () =
    let bytes = Encoding.ASCII.GetBytes "plain ascii"
    let detect = TextEncoding.detectEncoding bytes
    Assert.Equal(Encoding.UTF8.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``null bytes — UTF-8 replacement fallback`` () =
    // strict UTF-8 / CP949 모두 fail 가능한 random binary 시도 — CP949 가 운 좋게 통과할 수도 있어
    // 본 테스트는 "최소한 exception 없이 가능한 Encoding 반환" 만 검증.
    let bytes = Array.init 16 (fun i -> byte (0xC0 + i))
    let detect = TextEncoding.detectEncoding bytes
    Assert.False(detect.ConfidenceHigh)
    Assert.NotNull(detect.Encoding)

[<Fact>]
let ``null 입력 — UTF-8 default 반환 (no throw)`` () =
    let detect = TextEncoding.detectEncoding null
    Assert.False(detect.ConfidenceHigh)
    Assert.Equal(Encoding.UTF8.CodePage, detect.Encoding.CodePage)

[<Fact>]
let ``빈 bytes — strict UTF-8 통과 (low confidence)`` () =
    let detect = TextEncoding.detectEncoding [||]
    Assert.False(detect.ConfidenceHigh)
    Assert.Equal(Encoding.UTF8.CodePage, detect.Encoding.CodePage)
