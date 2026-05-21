namespace Ds2.LightHouse

open System
open System.Text

/// 텍스트 파일 인코딩 추정 (BOM 우선 → strict UTF-8 → CP949 → UTF-8 replacement fallback).
///
/// done-lighthouse-kb-index.md §3.4 — chat 측 (Ds2.LlmAgent.AttachmentClassifier) 와
/// KB ingest (Phase 1 TextExtractor) 가 모두 본 module 사용. 두 경로의 인코딩 추정 SSOT.
///
/// CP949 (Windows-949) fallback 은 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
/// 사전 등록 필요. Promaker 는 `App.OnStartup` 에서 1회 등록 (App.xaml.cs CodePagesEncodingProvider.Instance).
/// 미등록 환경은 ArgumentException → UTF-8 replacement 로 graceful fallback.
module TextEncoding =

    /// 텍스트 인코딩 추정 결과. ConfidenceHigh = BOM 또는 strict UTF-8 통과.
    type TextEncodingDetect = {
        Encoding: Encoding
        ConfidenceHigh: bool
    }

    /// CP949 (Windows-949) lazy probe. CodePagesEncodingProvider 등록 안 된 환경이면 None.
    /// 명칭 "cp949" 는 .NET 미인식 — code page 번호 949 사용.
    /// `EncoderExceptionFallback` 인자는 GetEncoding signature 필수 (decode 흐름에 무영향).
    let private tryCp949 () : Encoding option =
        try Some (Encoding.GetEncoding(949, EncoderExceptionFallback(), DecoderExceptionFallback()))
        with :? ArgumentException -> None

    /// bytes 가 strict 모드의 enc 로 디코딩 가능한지. invalid sequence 발견 시 false.
    let private isStrictDecodable (enc: Encoding) (bytes: byte[]) : bool =
        try
            enc.GetCharCount(bytes, 0, bytes.Length) |> ignore
            true
        with :? DecoderFallbackException -> false

    /// bytes 의 텍스트 인코딩 추정. BOM 우선, 다음 strict UTF-8, 다음 strict CP949 (한국어 환경),
    /// 마지막 UTF-8 replacement fallback. ASCII 부분은 살아남고 비-UTF-8 부분만 U+FFFD 로 대체
    /// → 사용자 측 mojibake 인지 가능 + LLM 입력 손상 최소화.
    let detectEncoding (bytes: byte[]) : TextEncodingDetect =
        if isNull bytes then
            { Encoding = Encoding.UTF8; ConfidenceHigh = false }
        elif bytes.Length >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy then
            { Encoding = Encoding.UTF8; ConfidenceHigh = true }
        elif bytes.Length >= 4 && bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy && bytes.[2] = 0x00uy && bytes.[3] = 0x00uy then
            { Encoding = Encoding.UTF32; ConfidenceHigh = true }
        elif bytes.Length >= 2 && bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy then
            { Encoding = Encoding.Unicode; ConfidenceHigh = true }
        elif bytes.Length >= 2 && bytes.[0] = 0xFEuy && bytes.[1] = 0xFFuy then
            { Encoding = Encoding.BigEndianUnicode; ConfidenceHigh = true }
        else
            let utf8Strict = Encoding.GetEncoding("utf-8", EncoderExceptionFallback(), DecoderExceptionFallback())
            if isStrictDecodable utf8Strict bytes then
                { Encoding = Encoding.UTF8; ConfidenceHigh = false }
            else
                match tryCp949 () with
                | Some cp949 when isStrictDecodable cp949 bytes ->
                    // 한국어 Windows 의 .txt / .log 흔한 케이스. confidence low — invalid UTF-8 가 우연히 CP949 로
                    // 통과한 random binary 도 가능. UI 측 안내 문구로 보강.
                    { Encoding = cp949; ConfidenceHigh = false }
                | _ ->
                    // 모든 strict 시도 실패 → UTF-8 replacement 로 ASCII 부분만 살리고 나머지는 U+FFFD.
                    Log.textEncoding.Warn("Ds2.LightHouse.TextEncoding.detectEncoding — strict UTF-8/CP949 모두 실패, UTF-8 replacement fallback")
                    { Encoding = Encoding.UTF8; ConfidenceHigh = false }
