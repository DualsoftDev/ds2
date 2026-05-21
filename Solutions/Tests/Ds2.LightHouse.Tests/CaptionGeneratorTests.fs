module Ds2.LightHouse.Tests.CaptionGeneratorTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Ds2.LightHouse

/// **s6-r56 ⑭ (외부 --review) — CaptionGenerator HTTP wire fact**.
///
/// 기존 fact (IndexerTests) 는 mock `captionGen: byte[] -> ImageFormat -> CaptionResult` 만 검증.
/// 본 fixture 는 `CaptionGenerator.callAnthropic` 의 실 HTTP wire (Anthropic messages API 정합) 검증:
/// - request: POST https://api.anthropic.com/v1/messages
/// - headers: x-api-key / anthropic-version: 2023-06-01 / Content-Type: application/json
/// - body schema: { model, max_tokens, messages:[{role,content:[image{source{base64}}, text{prompt}]}]}
/// - response: 200 OK content[0].text → Captioned / 4xx → FailedCaption / no-text → FailedCaption

/// mock HttpMessageHandler — request 박제 + 고정 response.
/// `callAnthropic` 가 `use req` 박제 → 함수 종료 시 Content dispose. body 검증을 위해 captured 시점에 string 캐싱.
/// `callAnthropic` 가 `http.Send` (synchronous) 사용 → `Send` override 의무 (default = NotSupportedException).
type private CapturedRequest = {
    Method: HttpMethod
    Uri: Uri
    Headers: (string * string) array
    BodyJson: string
}
type private MockHandler(respondWith: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let captured = ResizeArray<CapturedRequest>()
    let snapshot (req: HttpRequestMessage) : CapturedRequest =
        let body =
            if isNull req.Content then ""
            else req.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        let headers =
            req.Headers
            |> Seq.collect (fun kv -> kv.Value |> Seq.map (fun v -> kv.Key, v))
            |> Seq.toArray
        { Method = req.Method; Uri = req.RequestUri; Headers = headers; BodyJson = body }
    member _.Captured = captured :> seq<_>
    override _.SendAsync(req: HttpRequestMessage, _ct: CancellationToken) =
        captured.Add (snapshot req)
        respondWith req
    override _.Send(req: HttpRequestMessage, _ct: CancellationToken) =
        captured.Add (snapshot req)
        (respondWith req).GetAwaiter().GetResult()

/// fixed response factory.
let private respondOk (body: string) : HttpRequestMessage -> Task<HttpResponseMessage> =
    fun _ ->
        let resp = new HttpResponseMessage(HttpStatusCode.OK)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult resp

let private respondStatus (status: HttpStatusCode) (body: string) : HttpRequestMessage -> Task<HttpResponseMessage> =
    fun _ ->
        let resp = new HttpResponseMessage(status)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult resp

/// Anthropic 응답 mock — content[0].type=text + text 박제.
let private anthropicSuccessBody (caption: string) =
    sprintf """{"id":"msg_x","type":"message","model":"claude-sonnet-4-6","content":[{"type":"text","text":"%s"}]}"""
        (caption.Replace("\"", "\\\""))

let private samplePng () : byte[] =
    // 5 byte PNG signature prefix 만 (실 PNG decode 불요, callAnthropic 의 wire 검증 한정).
    [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy |]

[<Fact>]
let ``callAnthropic — 200 OK content[0].text → Captioned`` () =
    let body = anthropicSuccessBody "테스트 캡션"
    let handler = new MockHandler(respondOk body)
    use http = new HttpClient(handler)
    let bytes = samplePng()
    let result = CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6" bytes ImageFormat.Png CancellationToken.None
    match result with
    | CaptionResult.Captioned(text, model) ->
        Assert.Equal("테스트 캡션", text)
        Assert.Equal("claude-sonnet-4-6", model)
    | _ -> Assert.Fail "Captioned 기대"

[<Fact>]
let ``callAnthropic — HTTP 4xx → FailedCaption (status + body 부분 박제)`` () =
    let handler = new MockHandler(respondStatus HttpStatusCode.BadRequest """{"error":{"message":"bad"}}""")
    use http = new HttpClient(handler)
    let result =
        CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6"
            (samplePng()) ImageFormat.Png CancellationToken.None
    match result with
    | CaptionResult.FailedCaption reason ->
        Assert.Contains("HTTP 400", reason)
        Assert.Contains("bad", reason)
    | _ -> Assert.Fail "FailedCaption 기대"

[<Fact>]
let ``callAnthropic — content 배열에 text 없음 → FailedCaption "no text content"`` () =
    // image only (text block 없음)
    let body = """{"id":"x","content":[{"type":"image","source":"x"}]}"""
    let handler = new MockHandler(respondOk body)
    use http = new HttpClient(handler)
    let result =
        CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6"
            (samplePng()) ImageFormat.Png CancellationToken.None
    match result with
    | CaptionResult.FailedCaption reason ->
        Assert.Contains("no text content", reason)
    | _ -> Assert.Fail "FailedCaption 기대"

[<Fact>]
let ``callAnthropic — request body schema (model / max_tokens / messages / image base64 / prompt)`` () =
    let handler = new MockHandler(respondOk (anthropicSuccessBody "ok"))
    use http = new HttpClient(handler)
    let bytes = samplePng()
    let _ =
        CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6" bytes ImageFormat.Png CancellationToken.None
    let req = handler.Captured |> Seq.exactlyOne
    Assert.Equal(HttpMethod.Post, req.Method)
    Assert.Equal("https://api.anthropic.com/v1/messages", req.Uri.ToString())
    // headers (case-insensitive lookup)
    let hdr name =
        req.Headers
        |> Array.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd
    Assert.Equal(Some "sk-test", hdr "x-api-key")
    Assert.Equal(Some "2023-06-01", hdr "anthropic-version")
    // body schema
    use doc = JsonDocument.Parse req.BodyJson
    let root = doc.RootElement
    Assert.Equal("claude-sonnet-4-6", root.GetProperty("model").GetString())
    Assert.True(root.GetProperty("max_tokens").GetInt32() > 0)
    let messages = root.GetProperty("messages")
    Assert.Equal(1, messages.GetArrayLength())
    let msg0 = messages.[0]
    Assert.Equal("user", msg0.GetProperty("role").GetString())
    let content = msg0.GetProperty("content")
    Assert.Equal(2, content.GetArrayLength())
    // content[0] = image block + base64
    Assert.Equal("image", content.[0].GetProperty("type").GetString())
    let source = content.[0].GetProperty("source")
    Assert.Equal("base64", source.GetProperty("type").GetString())
    Assert.Equal("image/png", source.GetProperty("media_type").GetString())
    let dataBase64 = source.GetProperty("data").GetString()
    Assert.Equal(Convert.ToBase64String bytes, dataBase64)
    // content[1] = text block (prompt)
    Assert.Equal("text", content.[1].GetProperty("type").GetString())
    let promptText = content.[1].GetProperty("text").GetString()
    Assert.False(String.IsNullOrWhiteSpace promptText)

[<Fact>]
let ``callAnthropic — empty bytes → SkippedCaption (네트워크 호출 0)`` () =
    let handler = new MockHandler(respondOk (anthropicSuccessBody "x"))
    use http = new HttpClient(handler)
    let result =
        CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6"
            [||] ImageFormat.Png CancellationToken.None
    Assert.True(match result with CaptionResult.SkippedCaption _ -> true | _ -> false)
    Assert.Empty(handler.Captured)

[<Fact>]
let ``callAnthropic — MaxImageBytes 초과 → SkippedCaption "too large"`` () =
    let handler = new MockHandler(respondOk (anthropicSuccessBody "x"))
    use http = new HttpClient(handler)
    // MaxImageBytes + 1
    let big = Array.zeroCreate<byte> (CaptionGenerator.MaxImageBytes + 1)
    let result =
        CaptionGenerator.callAnthropic http "sk-test" "claude-sonnet-4-6"
            big ImageFormat.Png CancellationToken.None
    match result with
    | CaptionResult.SkippedCaption reason -> Assert.Contains("too large", reason)
    | _ -> Assert.Fail "SkippedCaption 기대"
    Assert.Empty(handler.Captured)

[<Fact>]
let ``callAnthropic — apiKey 빈 값 → SkippedCaption "no api key" (네트워크 호출 0)`` () =
    let handler = new MockHandler(respondOk (anthropicSuccessBody "x"))
    use http = new HttpClient(handler)
    let result =
        CaptionGenerator.callAnthropic http "" "claude-sonnet-4-6"
            (samplePng()) ImageFormat.Png CancellationToken.None
    Assert.True(match result with CaptionResult.SkippedCaption _ -> true | _ -> false)
    Assert.Empty(handler.Captured)
