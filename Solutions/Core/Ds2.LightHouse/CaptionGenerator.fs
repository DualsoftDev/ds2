namespace Ds2.LightHouse

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Core

/// Phase 2 task D (s6-r19) — VLM caption 생성 결과 DU.
///
/// `Captioned` = VLM 응답 성공, caption text + 사용 모델 동봉 (ImageCache.CaptionText / CaptionModel 박제).
/// `SkippedCaption` = caller (cost gate / explicit off) 가 호출 자체 안 함 결정. CaptionText NULL 유지, 재색인 시 재시도.
/// `FailedCaption` = VLM 호출 throw 또는 응답 결함 (network / 4xx / 5xx / parse 실패). 같은 처리 — NULL 유지.
type CaptionResult =
    | Captioned of text: string * model: string
    | SkippedCaption of reason: string
    | FailedCaption of reason: string

/// Phase 2 task D (s6-r19) — Anthropic vision API 호출 helper (D-2-1 / D-2-5 / D-2-6 정합).
///
/// 본 module 은 lib 측에서 "vision call 의 wire 만" 박제 — cost gate / API key 관리 / model 선택은 caller 책임.
/// caller 가 `callAnthropic apiKey model bytes fmt` 호출하여 CaptionResult 얻고, 그 결과를 그대로
/// `CaptionGenerator` 함수 시그니처로 wrap 하여 `Indexer.ingestImagesIntoStore` 에 전달.
///
/// **D-2-6** = HttpClient + System.Text.Json 자체 wire (외부 SDK 의존 0). messages API 형식:
///   POST https://api.anthropic.com/v1/messages
///   headers: x-api-key, anthropic-version: 2023-06-01, content-type: application/json
///   body: { model, max_tokens, messages:[{role:"user", content:[
///            {type:"image", source:{type:"base64", media_type, data}},
///            {type:"text", text:<caption prompt>}
///         ]}]}
///
/// **D-2-4** = per-image fail-safe — exception (HttpRequestException / TaskCanceledException /
/// JsonException) catch 후 FailedCaption 반환. caller (Indexer) 가 NULL 유지 + 다음 image 진행.
[<RequireQualifiedAccess>]
module CaptionGenerator =

    /// caption prompt — 한국어 산업도면 가정. 1~2 문장 + 도면 안 라벨/번호 우선 추출.
    /// 변경 시 ImageCache.CaptionModel SSOT 정합 (MR3 invalidation 는 model tier 기준이지만 prompt 변경도 의미 영향).
    [<Literal>]
    let private CaptionPrompt =
        "이 이미지를 한국어로 1~2문장으로 설명해주세요. 도면/표/그래프인 경우 안에 보이는 라벨/번호/태그(예: CV01, DI12)를 우선 인용해주세요."

    /// Anthropic messages API endpoint (v1).
    [<Literal>]
    let private MessagesEndpoint = "https://api.anthropic.com/v1/messages"

    /// API version header — 안정성 SSOT (변경 시 wire 정합 깨질 risk).
    [<Literal>]
    let private AnthropicVersion = "2023-06-01"

    /// 응답 max_tokens — caption 1~2 문장 한정. token 절약.
    [<Literal>]
    let private DefaultMaxTokens = 256

    /// 항상 SkippedCaption 반환하는 default — Phase 1 / 비활성 환경. signature 는
    /// Indexer.ingestImagesIntoStore 의 captionGen 인자와 동일 (`byte[] -> ImageFormat -> CaptionResult`).
    ///
    /// **FSharpFunc value 명시 (s6-r19)** — C# caller (Promaker) 의 method group 변환 실패 (CS1503) 회피.
    /// F# 의 `let noop a b = ...` 패턴은 C# 에 static method 로 lift 되어 인자 위치 인자로 전달 시 method group
    /// 으로 인식 → FSharpFunc 변환 실패. FuncConvert.FromFunc 로 FSharpFunc instance 명시.
    let noop : FSharpFunc<byte[], FSharpFunc<ImageFormat, CaptionResult>> =
        FuncConvert.FromFunc<byte[], ImageFormat, CaptionResult>(
            System.Func<byte[], ImageFormat, CaptionResult>(fun _ _ -> SkippedCaption "no caption gen"))

    /// vision API 호출 가능한 image 인지 사전 검증 (Anthropic 5MB/image 한도, D-2-3 정합).
    /// 한도 초과 시 SkippedCaption — caller 가 별도 강등 경로 책임 안 짐.
    [<Literal>]
    let MaxImageBytes = 5 * 1024 * 1024

    /// JSON request body 구성. System.Text.Json 자체 직렬화 — 외부 의존 0.
    let private buildRequestJson (model: string) (mediaType: string) (base64: string) : string =
        let opts = JsonWriterOptions(Indented = false)
        use stream = new IO.MemoryStream()
        do
            use writer = new Utf8JsonWriter(stream, opts)
            writer.WriteStartObject()
            writer.WriteString("model", model)
            writer.WriteNumber("max_tokens", DefaultMaxTokens)
            writer.WriteStartArray("messages")
            writer.WriteStartObject()
            writer.WriteString("role", "user")
            writer.WriteStartArray("content")
            // image block
            writer.WriteStartObject()
            writer.WriteString("type", "image")
            writer.WriteStartObject("source")
            writer.WriteString("type", "base64")
            writer.WriteString("media_type", mediaType)
            writer.WriteString("data", base64)
            writer.WriteEndObject()
            writer.WriteEndObject()
            // text block (prompt)
            writer.WriteStartObject()
            writer.WriteString("type", "text")
            writer.WriteString("text", CaptionPrompt)
            writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    /// 응답 JSON 에서 caption text 추출. Anthropic 응답 형식:
    /// `{ "content":[{"type":"text","text":"..."}], "model":"...", ... }`
    let private extractCaptionText (json: string) : string option =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let mutable contentEl = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("content", &contentEl) && contentEl.ValueKind = JsonValueKind.Array then
                let mutable found = None
                for el in contentEl.EnumerateArray() do
                    if found.IsNone then
                        let mutable typEl = Unchecked.defaultof<JsonElement>
                        let mutable textEl = Unchecked.defaultof<JsonElement>
                        if el.TryGetProperty("type", &typEl)
                           && typEl.ValueKind = JsonValueKind.String
                           && typEl.GetString() = "text"
                           && el.TryGetProperty("text", &textEl)
                           && textEl.ValueKind = JsonValueKind.String then
                            let t = textEl.GetString()
                            if not (String.IsNullOrWhiteSpace t) then
                                found <- Some (t.Trim())
                found
            else None
        with _ -> None

    /// Anthropic messages API 호출 (1회, 재시도 없음 — caller 가 cost-aware retry 책임).
    ///
    /// **D-2-1** = model = "claude-sonnet-4-6" default (caller 가 결정).
    /// **D-2-4** = exception catch → FailedCaption. 5MB 초과 image → SkippedCaption (pre-validate).
    /// **D-2-6** = HttpClient 자체 wire. caller 가 HttpClient lifecycle (singleton 권장) 관리.
    let callAnthropic
        (http: HttpClient)
        (apiKey: string)
        (model: string)
        (bytes: byte[])
        (fmt: ImageFormat)
        (ct: CancellationToken)
        : CaptionResult =
        if isNull bytes || bytes.Length = 0 then
            SkippedCaption "empty bytes"
        elif bytes.Length > MaxImageBytes then
            SkippedCaption (sprintf "image too large (%d bytes > %d limit)" bytes.Length MaxImageBytes)
        elif String.IsNullOrWhiteSpace apiKey then
            SkippedCaption "no api key"
        elif String.IsNullOrWhiteSpace model then
            SkippedCaption "no model"
        else
            try
                let mediaType = ImageStore.mimeOf fmt
                let base64 = Convert.ToBase64String(bytes)
                let json = buildRequestJson model mediaType base64
                use req = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
                req.Headers.Add("x-api-key", apiKey)
                req.Headers.Add("anthropic-version", AnthropicVersion)
                req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                let resp = http.Send(req, HttpCompletionOption.ResponseContentRead, ct)
                let body =
                    use reader = new IO.StreamReader(resp.Content.ReadAsStream(ct), Encoding.UTF8)
                    reader.ReadToEnd()
                if not resp.IsSuccessStatusCode then
                    FailedCaption (sprintf "HTTP %d: %s" (int resp.StatusCode) (body.Substring(0, min 200 body.Length)))
                else
                    match extractCaptionText body with
                    | Some text -> Captioned (text, model)
                    | None -> FailedCaption "response has no text content"
            with
            | :? OperationCanceledException -> reraise ()
            | ex -> FailedCaption (sprintf "%s: %s" (ex.GetType().Name) ex.Message)
