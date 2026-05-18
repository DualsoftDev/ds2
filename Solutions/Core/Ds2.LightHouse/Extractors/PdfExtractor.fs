namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Threading
open UglyToad.PdfPig
open Ds2.LightHouse

/// PDF extractor (PdfPig 0.1.14).
///
/// Phase 1 책임: 페이지 단위 raw text segment + 문서 제목 (Information.Title).
/// outline / bookmark TOC 는 Phase 2 강화 — PdfPig 의 `TryGetBookmarks` API 가 byref 라 F# 진입 복잡 +
/// 실제 한국어 산업 PDF 의 bookmark 누락률이 높아 Phase 1 우선순위 아래 (§3.16 fail-safe + Phase 1 환원, §4.3).
///
/// **Phase 2 task C2 (s6-r12-followup)**: `page.GetImages()` + `IPdfImage.TryGetPng(out bytes)` 로
/// 페이지 안 image 추출 후 PNG 통일 박제. 화이트리스트 외 image (JPX/JBIG2 등 decode 미지원) 는
/// TryGetPng false 분기에서 자연 skip (m6 결론 — extractor primary 가드).
///
/// per-image fail-safe (M2 결론, s6-r12-followup): 단일 image 추출 실패 (decode exception / null bytes /
/// empty bytes) 시 `logWarn` + skip — exception 재발생 안 함. 다른 image 와 chunks 색인 차단 안 됨.
/// orphan blob 방지는 호출자 (Indexer.ingestImagesIntoStore) 의 defensive 가드 + 다음 색인의 sha256
/// dedup + Phase 3 GC job (backlog) 으로 흡수.
///
/// fail-safe (§3.16 / §6.5): 손상 / 암호화 PDF 는 log + 빈 ExtractedDocument 반환 (skip).
/// 그 외 IO / 일반 exception 은 그대로 throw (fail-fast — debugging 우선).
type PdfExtractor() =

    interface IExtractor with
        member _.Supports kind =
            match kind with
            | Pdf -> true
            | _ -> false

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            let mutable doc: PdfDocument = null
            try
                try
                    doc <- PdfDocument.Open(path)
                with ex ->
                    // PdfDocumentEncryptedException / PdfDocumentFormatException 등 외부 환경 사유.
                    // PdfPig 가 던지는 exception 종류 안정성 보장 안 됨 — 모두 fail-safe (§3.16 정책 명문화).
                    Log.lighthouse.Warn(sprintf "PdfExtractor: PdfDocument.Open 실패 — path=%s, ex=%s" path ex.Message)

                if isNull doc then
                    { DocType = Pdf; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
                else
                    let pageCount = doc.NumberOfPages
                    let title =
                        let t = doc.Information.Title
                        if String.IsNullOrWhiteSpace t then None else Some (t.Trim())

                    let segments = ResizeArray<ExtractedSegment>()
                    let images = ResizeArray<ExtractedImage>()
                    for i in 1 .. pageCount do
                        ct.ThrowIfCancellationRequested()
                        let page = doc.GetPage(i)
                        let text =
                            let raw = page.Text
                            if isNull raw then "" else raw.Trim()
                        if text.Length > 0 then
                            segments.Add {
                                OutlineIndex = None
                                RefLocator = sprintf "p=%d" i
                                Text = text
                            }

                        // Phase 2 task C2: 페이지 안 image 추출.
                        // PdfPig 의 TryGetPng 은 화이트리스트 외 image (JPX / JBIG2 decode 미지원) 에 대해 false 반환 →
                        // 자연 skip (m6 결론 = extractor primary 가드, ExtractedDocument.Images 미포함).
                        // 성공 시 PNG 통일 박제 (ImageFormat.Png) — 원본이 JPEG 라도 PdfPig 가 PNG 로 re-encode.
                        let mutable ord = 1
                        for img in page.GetImages() do
                            try
                                let success, pngBytes = img.TryGetPng()
                                if success && not (isNull pngBytes) && pngBytes.Length > 0 then
                                    let w = if img.WidthInSamples > 0 then Some img.WidthInSamples else None
                                    let h = if img.HeightInSamples > 0 then Some img.HeightInSamples else None
                                    images.Add {
                                        Bytes = pngBytes
                                        Format = Png
                                        Width = w
                                        Height = h
                                        RefLocator = sprintf "p=%d#img=%d" i ord
                                        Ordinal = ord
                                    }
                                    ord <- ord + 1
                            with ex ->
                                // M2 결론 (per-image fail-safe): decode exception → log + skip, ordinal 증가 안 함.
                                Log.lighthouse.Warn(
                                    sprintf "PdfExtractor: page=%d 의 image (ord=%d) 추출 실패 — path=%s, ex=%s"
                                        i ord path ex.Message)

                    {
                        DocType = Pdf
                        PageOrSheetCnt = Some pageCount
                        Title = title
                        Outline = [||]
                        Segments = segments.ToArray()
                        Images = images.ToArray()
                    }
            finally
                if not (isNull doc) then doc.Dispose()

        member _.Dispose () = ()
