namespace Ds2.LightHouse.Cli

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// CLI 진입용 F# LightHouseClient — Promaker C# `LightHouseClient` 의 wire 정합 subset.
/// 본 turn = `UploadCollectionAsync` 만 도입 (Phase S6 P1 scope).
/// 추가 endpoint (List/Delete/Session) 는 follow-up.

exception LightHouseAuthError of message: string * status: HttpStatusCode
exception LightHouseProtocolError of message: string * status: HttpStatusCode option

[<RequireQualifiedAccess>]
module LightHouseClient =

    /// 응답 body 의 `id` 필드만 추출하는 minimal DTO.
    /// non-private — JsonSerializer reflection 이 mutable property 인식해야 deserialize 성공.
    [<CLIMutable>]
    type UploadResponse = { id: string }

    let private jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// HTTPS-only 검증 + base address 정규화 (trailing slash 보장).
    /// `allowInvalidCerts` = dev only (self-signed cert 신뢰 우회).
    let createHttpClient (baseUrl: string) (allowInvalidCerts: bool) : HttpClient =
        if String.IsNullOrWhiteSpace baseUrl then
            invalidArg (nameof baseUrl) "baseUrl 필수."
        let uri = Uri baseUrl
        if uri.Scheme <> Uri.UriSchemeHttps then
            invalidArg (nameof baseUrl)
                (sprintf "baseUrl HTTPS-only — %s (plain HTTP 거부, §3.7)." baseUrl)
        let handler = new HttpClientHandler()
        if allowInvalidCerts then
            // dev only — self-signed cert 신뢰 우회. production 에서는 절대 사용 금지.
            handler.ServerCertificateCustomValidationCallback <-
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- Uri(if baseUrl.EndsWith '/' then baseUrl else baseUrl + "/")
        client.Timeout <- TimeSpan.FromMinutes 10.0  // 큰 zip upload 대비 (C# client 와 동일)
        client

    /// status code → exception 분기 (C# `EnsureSuccessOrThrow` 정합).
    /// 401/403 → AuthError, 그 외 비-2xx → ProtocolError.
    let private ensureSuccessOrThrow (resp: HttpResponseMessage) (operation: string) (ct: CancellationToken) =
        task {
            if resp.IsSuccessStatusCode then return ()
            else
                let! body =
                    task {
                        try return! resp.Content.ReadAsStringAsync(ct)
                        with _ -> return ""
                    }
                let truncated = if body.Length > 200 then body.Substring(0, 200) + "…" else body
                let msg =
                    sprintf "%s 실패 — HTTP %d %s. body=%s"
                        operation (int resp.StatusCode) resp.ReasonPhrase truncated
                match resp.StatusCode with
                | HttpStatusCode.Unauthorized
                | HttpStatusCode.Forbidden ->
                    return raise (LightHouseAuthError(msg, resp.StatusCode))
                | _ ->
                    return raise (LightHouseProtocolError(msg, Some resp.StatusCode))
        }

    /// `POST /collections` — multipart (title + zip) → server 발급 guid 반환.
    /// `zipStream` 처분 = `MultipartFormDataContent.Dispose` 가 child `StreamContent` 까지 dispose →
    /// 본 함수 반환 시점에 stream 은 *이미 dispose 됨*. 자가 검열 M1 — `FileStream.Dispose` 는 idempotent
    /// 이므로 caller 가 `use stream = File.OpenRead` 로 또 dispose 해도 안전 (runtime exception 없음).
    /// caller 는 reuse 금지 (multi-step upload 시 매번 새 stream open).
    let uploadCollection
        (client: HttpClient)
        (psk: string)
        (userIdentity: string)
        (title: string)
        (zipStream: Stream)
        (ct: CancellationToken)
        : Task<string> =
        task {
            if String.IsNullOrWhiteSpace title then invalidArg (nameof title) "title 필수."
            if isNull zipStream then nullArg (nameof zipStream)

            use content = new MultipartFormDataContent()
            content.Add(new StringContent(title), "title")
            let zipContent = new StreamContent(zipStream)
            zipContent.Headers.ContentType <- MediaTypeHeaderValue.Parse "application/zip"
            content.Add(zipContent, "zip", "payload.zip")

            use req = new HttpRequestMessage(HttpMethod.Post, "collections")
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", psk)
            req.Headers.Add("X-User-Identity", userIdentity)
            req.Content <- content

            use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            do! ensureSuccessOrThrow resp "POST /collections" ct
            let! body = resp.Content.ReadAsStringAsync(ct)
            if String.IsNullOrWhiteSpace body then
                return raise (LightHouseProtocolError("POST /collections response body 빈 값.", Some resp.StatusCode))
            else
                let parsed = JsonSerializer.Deserialize<UploadResponse>(body, jsonOptions)
                if String.IsNullOrEmpty parsed.id then
                    return raise (LightHouseProtocolError("POST /collections response 의 id 누락.", Some resp.StatusCode))
                else
                    return parsed.id
        }
