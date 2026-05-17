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
                    { DocType = Pdf; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||] }
                else
                    let pageCount = doc.NumberOfPages
                    let title =
                        let t = doc.Information.Title
                        if String.IsNullOrWhiteSpace t then None else Some (t.Trim())

                    let segments = ResizeArray<ExtractedSegment>()
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

                    {
                        DocType = Pdf
                        PageOrSheetCnt = Some pageCount
                        Title = title
                        Outline = [||]
                        Segments = segments.ToArray()
                    }
            finally
                if not (isNull doc) then doc.Dispose()

        member _.Dispose () = ()
