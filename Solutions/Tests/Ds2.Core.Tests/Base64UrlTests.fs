module Ds2.Core.Tests.Base64UrlTests

open Ds2.Core.Encoding
open Xunit

// Phase 0 — ADR-009 URL/파일명 ID 인코딩 규약.

[<Fact>]
let ``encode of a known URN yields RFC 4648 unpadded form`` () =
    // urn:dualsoft:asset:cnc01 → dXJuOmR1YWxzb2Z0OmFzc2V0OmNuYzAx
    let result = Base64Url.encode "urn:dualsoft:asset:cnc01"
    Assert.Equal("dXJuOmR1YWxzb2Z0OmFzc2V0OmNuYzAx", result)

[<Fact>]
let ``encode does not emit padding`` () =
    let cases = [ "a"; "ab"; "abc"; "abcd"; "hello world" ]
    for c in cases do
        Assert.DoesNotContain("=", Base64Url.encode c)

[<Fact>]
let ``encode does not emit plus or slash`` () =
    // Values chosen to exercise + and / in raw base64.
    let raw = "\xff\xfe\xfd\xfc"
    let encoded = Base64Url.encode raw
    Assert.DoesNotContain("+", encoded)
    Assert.DoesNotContain("/", encoded)

[<Fact>]
let ``round-trip preserves ASCII`` () =
    let cases = [
        "urn:dualsoft:asset:cnc01"
        "https://example.com/x?y=z"
        "line1.pm03.active-power"
    ]
    for c in cases do
        Assert.Equal(c, Base64Url.decode (Base64Url.encode c))

[<Fact>]
let ``round-trip preserves Korean UTF-8`` () =
    let s = "듀얼소프트 자산 식별자 · 한글 테스트"
    Assert.Equal(s, Base64Url.decode (Base64Url.encode s))

[<Fact>]
let ``round-trip preserves special characters`` () =
    let s = "특수:문자/포함?u=v&x=y#fragment"
    Assert.Equal(s, Base64Url.decode (Base64Url.encode s))

[<Fact>]
let ``isRoundTrip returns true for arbitrary strings`` () =
    Assert.True(Base64Url.isRoundTrip "hello")
    Assert.True(Base64Url.isRoundTrip "urn:dualsoft:asset:cnc01")

[<Fact>]
let ``isValidChars accepts encoded output`` () =
    let encoded = Base64Url.encode "urn:dualsoft:asset:cnc01"
    Assert.True(Base64Url.isValidChars encoded)

[<Fact>]
let ``isValidChars rejects padding or slash`` () =
    Assert.False(Base64Url.isValidChars "abc=")
    Assert.False(Base64Url.isValidChars "ab/cd")
    Assert.False(Base64Url.isValidChars "ab+cd")
