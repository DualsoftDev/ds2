module ClaudeStreamJsonInputTests

open System
open System.Text.Json
open Xunit
open Ds2.LlmAgent

/// commit-6b — Claude CLI `--input-format stream-json` stdin 인코더 골든 테스트.
/// JSON Lines envelope (Anthropic multipart content block) 형식 변경 시 즉시 회귀 실패.

let private parseRoot (json: string) =
    use doc = JsonDocument.Parse(json)
    // doc.RootElement clone 후 반환 (using 외부에서 안전 사용 위해 string 검증으로 대체)
    json

[<Fact>]
let ``첨부 없을 때 text only block 1개 + role=user`` () =
    let encoded = ClaudeStreamJsonInput.encode "hello" Seq.empty
    use doc = JsonDocument.Parse(encoded)
    let root = doc.RootElement
    Assert.Equal("user", root.GetProperty("type").GetString())
    let msg = root.GetProperty("message")
    Assert.Equal("user", msg.GetProperty("role").GetString())
    let content = msg.GetProperty("content")
    Assert.Equal(1, content.GetArrayLength())
    let first = content.[0]
    Assert.Equal("text", first.GetProperty("type").GetString())
    Assert.Equal("hello", first.GetProperty("text").GetString())

[<Fact>]
let ``이미지 1개 첨부 시 text + image 2 block + base64 source`` () =
    let bytes = [| 1uy; 2uy; 3uy |]
    let img = Image("a.png", bytes, "image/png")
    let encoded = ClaudeStreamJsonInput.encode "see this" (seq { img })
    use doc = JsonDocument.Parse(encoded)
    let content = doc.RootElement.GetProperty("message").GetProperty("content")
    Assert.Equal(2, content.GetArrayLength())
    let imgBlock = content.[1]
    Assert.Equal("image", imgBlock.GetProperty("type").GetString())
    let src = imgBlock.GetProperty("source")
    Assert.Equal("base64", src.GetProperty("type").GetString())
    Assert.Equal("image/png", src.GetProperty("media_type").GetString())
    Assert.Equal(Convert.ToBase64String(bytes), src.GetProperty("data").GetString())

[<Fact>]
let ``PDF 첨부 시 document block + media_type application/pdf`` () =
    let bytes = [| 0x25uy; 0x50uy; 0x44uy; 0x46uy |]  // %PDF
    let pdf = Pdf("spec.pdf", bytes)
    let encoded = ClaudeStreamJsonInput.encode "review" (seq { pdf })
    use doc = JsonDocument.Parse(encoded)
    let content = doc.RootElement.GetProperty("message").GetProperty("content")
    let docBlock = content.[1]
    Assert.Equal("document", docBlock.GetProperty("type").GetString())
    let src = docBlock.GetProperty("source")
    Assert.Equal("application/pdf", src.GetProperty("media_type").GetString())

[<Fact>]
let ``TextFile 첨부는 invalidOp — prompt 본문 inline 강제`` () =
    let txt = TextFile("notes.md", "# hi")
    Assert.Throws<InvalidOperationException>(fun () ->
        ClaudeStreamJsonInput.encode "x" (seq { txt }) |> ignore)

[<Fact>]
let ``인코딩 결과는 trailing \n 단일 — Claude CLI line-based parser 종료 토큰 필수`` () =
    let encoded = ClaudeStreamJsonInput.encode "hello" Seq.empty
    Assert.EndsWith("\n", encoded)
    // body 안에는 newline 없음 (단일 라인 JSON Lines envelope)
    let body = encoded.TrimEnd('\n')
    Assert.DoesNotContain("\n", body)
    Assert.DoesNotContain("\r", body)

[<Fact>]
let ``빈 prompt 도 명시적 빈 text block 으로 wire`` () =
    let img = Image("a.png", [|0uy|], "image/png")
    let encoded = ClaudeStreamJsonInput.encode "" (seq { img })
    use doc = JsonDocument.Parse(encoded)
    let content = doc.RootElement.GetProperty("message").GetProperty("content")
    Assert.Equal(2, content.GetArrayLength())
    Assert.Equal("text", content.[0].GetProperty("type").GetString())
    Assert.Equal("", content.[0].GetProperty("text").GetString())
