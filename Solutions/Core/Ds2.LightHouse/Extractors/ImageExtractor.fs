namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Threading
open Ds2.LightHouse

/// **Task 7 (r6) — standalone image 색인** — 단일 image 파일 추출기.
///
/// 활성 6 종 (Classifier.fs `supportedExtensions` SSOT):
///   - **raw 보존** (`Format` = 원본): PNG / JPEG / GIF / WEBP — magic byte 검증 후 raw bytes 박제.
///   - **Metafile→PNG 변환** (`Format` = Png): EMF / WMF — `MetafileConverter.convertToPng` 호출.
///
/// `Format` 가 `Bytes` 내용과 정확 일치 의무 (`ImageStore.saveBlob` 의 extension 분기 정합). 변환 case 도 동일.
///
/// `Width` / `Height` = `System.Drawing.Image.FromStream` header parse (Windows 의존). 실패 시 None
/// (Linux/macOS / 깨진 header / EMF 변환 실패 등 자연 fail-safe).
///
/// `RefLocator = "image=1"` literal (N=1 고정 — standalone 파일 1장). 향후 multi-frame / animated image 의
/// embedded 분할 활성 시 `image=N` 자연 호환 (parent RefLocator.fs `RefUnit.Image` 박제).
///
/// per-image fail-safe — magic byte mismatch / EMF 변환 실패 / read 실패 모두 `Log.lighthouse.Warn` + 빈
/// `Images` 반환 (다른 파일 색인 차단 안 됨). icon size 가드 (`Indexer.MinImageBytesForIndex = 8192`) 는
/// caller (`Indexer.ingestImagesIntoStore`) 가 적용 — 본 extractor 변경 0.
type ImageExtractor() =

    /// magic byte 검증 — extension ↔ bytes 정합 (renamed file 차단).
    ///   - PNG : 89 50 4E 47 0D 0A 1A 0A
    ///   - JPEG: FF D8 FF
    ///   - GIF : 47 49 46 38 ("GIF8" — 87a/89a 공통 prefix)
    ///   - WEBP: 52 49 46 46 (RIFF) … 57 45 42 50 (WEBP)
    static member private VerifyMagicBytes (bytes: byte[]) (expected: ImageFormat) : bool =
        if isNull bytes then false
        else
            match expected with
            | Png ->
                bytes.Length >= 8
                && bytes.[0] = 0x89uy && bytes.[1] = 0x50uy
                && bytes.[2] = 0x4Euy && bytes.[3] = 0x47uy
            | Jpeg ->
                bytes.Length >= 3
                && bytes.[0] = 0xFFuy && bytes.[1] = 0xD8uy && bytes.[2] = 0xFFuy
            | Gif ->
                bytes.Length >= 4
                && bytes.[0] = 0x47uy && bytes.[1] = 0x49uy
                && bytes.[2] = 0x46uy && bytes.[3] = 0x38uy
            | Webp ->
                bytes.Length >= 12
                && bytes.[0] = 0x52uy && bytes.[1] = 0x49uy
                && bytes.[2] = 0x46uy && bytes.[3] = 0x46uy
                && bytes.[8] = 0x57uy && bytes.[9] = 0x45uy
                && bytes.[10] = 0x42uy && bytes.[11] = 0x50uy

    /// 확장자 → (`ImageFormat option`, isMetafile). Classifier 의 7 매핑과 1:1 정합.
    /// raw 보존 case = `(Some fmt, false)`. EMF/WMF case = `(None, true)`.
    static member private FormatOfExtension (ext: string) : (ImageFormat option * bool) =
        match ext.ToLowerInvariant() with
        | ".png" -> (Some Png, false)
        | ".jpg" | ".jpeg" -> (Some Jpeg, false)
        | ".gif" -> (Some Gif, false)
        | ".webp" -> (Some Webp, false)
        | ".emf" | ".wmf" -> (None, true)
        | _ -> (None, false)

    /// raw / 변환 후 PNG bytes 에서 Width / Height parse (Windows 의존).
    /// per-image fail-safe — exception 시 (None, None) (Log.lighthouse.Debug — 광범위 catch 의도).
    static member private TryParseDim (bytes: byte[]) : (int option * int option) =
        try
            use ms = new MemoryStream(bytes)
            use img = System.Drawing.Image.FromStream(ms, false, false)
            (Some img.Width, Some img.Height)
        with _ ->
            (None, None)

    static member private MakeImageDocument (bytes: byte[]) (fmt: ImageFormat) (titleOpt: string option) : ExtractedDocument =
        let (w, h) = ImageExtractor.TryParseDim bytes
        let img : ExtractedImage = {
            Bytes = bytes
            Format = fmt
            Width = w
            Height = h
            RefLocator = "image=1"
            Ordinal = 1
        }
        { DocType = FileKind.Image
          PageOrSheetCnt = None
          Title = titleOpt
          Outline = [||]
          Segments = [||]
          Images = [| img |] }

    static member private EmptyDocument (titleOpt: string option) : ExtractedDocument =
        { DocType = FileKind.Image
          PageOrSheetCnt = None
          Title = titleOpt
          Outline = [||]
          Segments = [||]
          Images = [||] }

    interface IExtractor with
        member _.Supports kind =
            // qualified case `FileKind.Image` — RefUnit.Image 동명 disambiguation (SqliteStore docTypeToString 정합).
            match kind with
            | FileKind.Image -> true
            | _ -> false

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            let ext = Path.GetExtension(path).ToLowerInvariant()
            let titleRaw = Path.GetFileNameWithoutExtension path
            let titleOpt = if String.IsNullOrWhiteSpace titleRaw then None else Some titleRaw
            let bytes =
                try File.ReadAllBytes path
                with ex ->
                    Log.lighthouse.Warn(
                        sprintf "ImageExtractor: 파일 읽기 실패 (skip) — path=%s ex=%s: %s"
                            path (ex.GetType().Name) ex.Message)
                    [||]
            if bytes.Length = 0 then ImageExtractor.EmptyDocument titleOpt
            else
                match ImageExtractor.FormatOfExtension ext with
                | (Some fmt, false) ->
                    if not (ImageExtractor.VerifyMagicBytes bytes fmt) then
                        Log.lighthouse.Warn(
                            sprintf "ImageExtractor: magic byte mismatch (skip) — ext=%s path=%s" ext path)
                        ImageExtractor.EmptyDocument titleOpt
                    else
                        ImageExtractor.MakeImageDocument bytes fmt titleOpt
                | (None, true) ->
                    try
                        use ms = new MemoryStream(bytes)
                        let pngBytes = MetafileConverter.convertToPng ms MetafileConverter.DefaultMaxDim
                        if pngBytes.Length = 0 then
                            ImageExtractor.EmptyDocument titleOpt
                        else
                            ImageExtractor.MakeImageDocument pngBytes Png titleOpt
                    with
                    | :? System.PlatformNotSupportedException as ex ->
                        Log.lighthouse.Warn(
                            sprintf "ImageExtractor: Metafile 변환 미지원 platform — ext=%s path=%s ex=%s"
                                ext path ex.Message)
                        ImageExtractor.EmptyDocument titleOpt
                    | ex ->
                        Log.lighthouse.Warn(
                            sprintf "ImageExtractor: Metafile 변환 실패 (skip) — ext=%s path=%s ex=%s: %s"
                                ext path (ex.GetType().Name) ex.Message)
                        ImageExtractor.EmptyDocument titleOpt
                | _ ->
                    Log.lighthouse.Warn(
                        sprintf "ImageExtractor: 매핑 안 된 확장자 (Classifier invariant 위반?) — ext=%s path=%s"
                            ext path)
                    ImageExtractor.EmptyDocument titleOpt

        member _.Dispose () = ()
