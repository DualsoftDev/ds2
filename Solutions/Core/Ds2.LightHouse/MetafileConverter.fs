namespace Ds2.LightHouse

open System.IO

/// **Task 7 (standalone image 색인) + Plan 3 (OOXML EMF/WMF 변환)** — Metafile (EMF/WMF) → PNG bytes.
///
/// System.Drawing.Imaging.Metafile + Bitmap 사용. Windows 전용 — Linux/macOS 시 PlatformNotSupportedException.
/// OoxmlExtractor.ConvertMetafileToPng 와 ImageExtractor (Task 7) 양쪽이 동일 helper 호출. SSOT 단일 박제.
///
/// caller 가 PlatformNotSupportedException / OutOfMemoryException / ExternalException (GDI+) /
/// ArgumentException 등 catch 의무 (per-image fail-safe).
[<RequireQualifiedAccess>]
module MetafileConverter =

    /// 변환된 PNG 의 max pixel dimension cap (산업 도면 vector 의 raster 해상도 폭주 차단).
    /// caller 가 별도 cap 의도 시 인자로 override. config 화는 Phase 3 backlog.
    let [<Literal>] DefaultMaxDim = 2048

    /// stream 의 EMF/WMF metafile → PNG byte array. 비례 축소 cap 적용.
    /// raw stream 은 호출 도중 dispose (caller 가 stream 위치 재사용 의도하지 말 것).
    let convertToPng (stream: Stream) (maxDim: int) : byte[] =
        use mf = new System.Drawing.Imaging.Metafile(stream)
        let mutable w = mf.Width
        let mutable h = mf.Height
        if w > maxDim || h > maxDim then
            let scale = min (float maxDim / float w) (float maxDim / float h)
            w <- max 1 (int (float w * scale))
            h <- max 1 (int (float h * scale))
        use bmp = new System.Drawing.Bitmap(w, h)
        use g = System.Drawing.Graphics.FromImage(bmp)
        g.Clear(System.Drawing.Color.White)   // EMF 의 투명 배경 백색 박제 (PDF 도면 정합)
        g.DrawImage(mf, 0, 0, w, h)
        use ms = new MemoryStream()
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png)
        ms.ToArray()
