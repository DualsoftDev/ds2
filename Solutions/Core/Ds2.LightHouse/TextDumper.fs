namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open System.Threading
open Microsoft.Data.Sqlite
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies
open Ds2.LightHouse.PdfStrategies

/// **PR-C (todo-lighthouse-index-summary.md §3.2)** — collection 의 색인된 모든 Document 를 markdown
/// text dump 파일로 변환하여 `<root>/.lighthouse-kb/text/<docId>-<filename>.md` 에 저장.
///
/// 용도 = LLM 이 *전체 본문* 인식이 필요할 때 `attachment_fulltext(fileId)` MCP tool 호출 (PR-D)
/// → server 가 본 파일 stream 응답 → 단일 호출로 전체 내용 흡수. system prompt inline 아닌 tool 호출 path.
///
/// 알고리즘 (Phase 1 단순):
/// 1. `SELECT Id, OriginalPath, DocType FROM Documents` 전체 enumerate
/// 2. 각 Document 별로 chunks 합쳐 markdown 생성 (heading = RefLocator 단위)
/// 3. ImageReferences + ImageCache caption inline (section 끝에 image gallery)
/// 4. 512KB cap (markdown byte size) — 초과 시 truncate + footer
/// 5. `text/<docId>-<sanitized-filename>.md` write (UTF-8 BOM 없음)
///
/// Phase 1 무관 image inline 매칭 (chunk 사이 정확 위치) 은 별 PR backlog —
/// 본 phase 는 doc 끝 image gallery section 만 (단순 구현, LLM 가독성 충분).
[<RequireQualifiedAccess>]
module TextDumper =

    /// **PR-C 잠정 default (todo §4 미결정 10)** — 단일 doc text dump 최대 byte (UTF-8 markdown).
    /// 100~150K tokens 정합 (산업 사양서 통상 미초과). 초과 시 truncate + footer 안내.
    [<Literal>]
    let MaxDumpBytes = 524288  // 512 KB

    /// text dump 저장 sub-directory 이름 (`.lighthouse-kb/text/`).
    [<Literal>]
    let TextSubDirName = "text"

    /// **PR-I3 (todo-documents-based-gfm.md §2 PR-I3 + §8.5.5)** — strategy 의 정제 markdown 박제
    /// sub-directory 이름 (`.lighthouse-kb/summary/`). SpecializedDigestBuilder (PR-I4) 가
    /// 본 디렉토리의 모든 `*.md` 를 합쳐 system prompt 의 cache breakpoint 3 에 inject.
    [<Literal>]
    let SummarySubDirName = "summary"

    /// **PR-I3 (todo §6.1 N1 default)** — strategy signature 매치 후 변환 실패한 케이스의 누적 박제 파일.
    /// Newtonsoft.Json serialize 된 `RejectedEntry list`. 동일 collection 재색인 시 overwrite (in-place update).
    [<Literal>]
    let RejectedJsonFileName = "rejected.json"

    /// **PR-I3 (todo §6.1 N4 default)** — strategy score > 0 이나 threshold 미달 (거의 매치) 의 누적 박제 파일.
    /// Newtonsoft.Json serialize 된 `NearMissEntry list`. 동일 collection 재색인 시 overwrite.
    [<Literal>]
    let NearMissJsonFileName = "near-miss.json"

    /// truncate 시 박제 footer (LLM 안내 + attachment_search escalation 권장).
    [<Literal>]
    let TruncateFooter = "\n\n---\n\n[text dump truncated at 512KB — use attachment_search for specific ref]\n"

    /// `<source>/.lighthouse-kb/text/` 절대 경로.
    let textDir (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, TextSubDirName)

    /// `<source>/.lighthouse-kb/summary/` 절대 경로 (PR-I3 strategy 정제 markdown 박제 위치).
    let summaryDir (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, SummarySubDirName)

    /// `<source>/.lighthouse-kb/rejected.json` 절대 경로.
    let rejectedJsonPath (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, RejectedJsonFileName)

    /// `<source>/.lighthouse-kb/near-miss.json` 절대 경로.
    let nearMissJsonPath (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, NearMissJsonFileName)

    /// **A·m1 (Outlier/Minor 묶음 1)** — sanitize helper SSOT.
    /// 종전 `summaryFilename` / `summaryPartFilename` 의 동일 char policy (`Char.IsLetterOrDigit ||
    /// '-' || '_' || '.'`) 박제 loop 가 양쪽에 복붙되어 있던 것을 단일 helper 로 추출. drift 방지.
    let internal appendSanitized (sb: StringBuilder) (s: string) : unit =
        for ch in s do
            if Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.' then sb.Append ch |> ignore
            else sb.Append '_' |> ignore

    /// strategy 산출물 파일명 — `{strategyName}-{docId}-{basename}.md`.
    /// docId = `StrategyMarkdown.computeDocId` (source bytes SHA256 의 앞 4 byte = 8 hex char).
    /// strategy 다중 매치 / source 동일 시에도 unique. sanitizedFilename 와 동일 char policy.
    let summaryFilename (strategyName: string) (docId: string) (originalPath: string) : string =
        let basename =
            if String.IsNullOrEmpty originalPath then "untitled"
            else Path.GetFileNameWithoutExtension originalPath
        let sb = StringBuilder(strategyName.Length + docId.Length + basename.Length + 8)
        appendSanitized sb strategyName
        sb.Append '-' |> ignore
        appendSanitized sb docId
        sb.Append '-' |> ignore
        appendSanitized sb basename
        sb.Append ".md" |> ignore
        sb.ToString()

    /// **Backlog I (todo-documents-based-gfm.md §6.1 + documents-based-gfm.md §8.5.5)** —
    /// Stage 3 (Split) 진입 시 multi-part 박제 파일명 — `{strategyName}-{docId}-{basename}.{partIndex}.md`.
    /// `partIndex` = 1-based. Stage 3 미진입 (SplitParts=None) 시 `summaryFilename` 사용 (1-part 만 박제).
    /// caller 가 `MarkdownCapPolicy.CapResult.SplitParts` 의 Some 분기에서 본 helper 호출.
    let summaryPartFilename (strategyName: string) (docId: string) (originalPath: string) (partIndex: int) : string =
        let basename =
            if String.IsNullOrEmpty originalPath then "untitled"
            else Path.GetFileNameWithoutExtension originalPath
        let sb = StringBuilder(strategyName.Length + docId.Length + basename.Length + 16)
        appendSanitized sb strategyName
        sb.Append '-' |> ignore
        appendSanitized sb docId
        sb.Append '-' |> ignore
        appendSanitized sb basename
        sb.Append '.' |> ignore
        sb.Append (string partIndex) |> ignore
        sb.Append ".md" |> ignore
        sb.ToString()

    /// docId + originalPath → filename (path traversal 차단 + 의심 char 제거).
    /// 결과 = `<docId>-<basename>.md` (basename 은 의심 char `/\:*?"<>|` 등 replace `_`).
    /// **review F fix (r4)**: public 격상 — SummaryBuilder + SummaryStore 가 사본 박제 회피 위해 직접 참조.
    /// drift 시 attachment_summary 의 textDumpPath ↔ attachment_fulltext 의 docId 매칭 fail 차단.
    let sanitizedFilename (docId: int64) (originalPath: string) : string =
        let basename =
            if String.IsNullOrEmpty originalPath then "untitled"
            else Path.GetFileNameWithoutExtension originalPath
        let sb = StringBuilder(basename.Length)
        // A·m1 — SSOT helper 재사용 (appendSanitized).
        appendSanitized sb basename
        let safe = sb.ToString()
        let safe = if String.IsNullOrEmpty safe then "untitled" else safe
        sprintf "%d-%s.md" docId safe

    /// DocType → markdown heading 접두 (page / slide / sheet / heading 통일).
    /// SqliteStore.fs §3.13 의 RefLocator 저장형 (`p=14` / `slide=5` / `sheet=BOM`) → 표시형 변환.
    let private formatHeading (refLocator: string) : string =
        if String.IsNullOrEmpty refLocator then "## (no ref)"
        elif refLocator.StartsWith("p=") then sprintf "## p.%s" (refLocator.Substring 2)
        elif refLocator.StartsWith("slide=") then sprintf "## 슬라이드 %s" (refLocator.Substring 6)
        elif refLocator.StartsWith("sheet=") then sprintf "## 시트 %s" (refLocator.Substring 6)
        else sprintf "## %s" refLocator

    /// 한 Document → markdown string (cap 적용 전).
    let private buildDocumentMarkdown
        (conn: SqliteConnection)
        (docId: int64)
        (originalPath: string)
        (docType: string)
        : string =
        let sb = StringBuilder()
        let basename = if String.IsNullOrEmpty originalPath then "(unnamed)" else Path.GetFileName originalPath
        sb.AppendLine(sprintf "# %s" basename) |> ignore
        sb.AppendLine(sprintf "_DocType: %s | DocId: %d_" docType docId) |> ignore
        sb.AppendLine() |> ignore

        // chunks streaming — RefLocator 변화 detection 으로 heading 박제.
        // **PR-Img-Chunk**: ORDER BY 를 (RefLocator 첫 등장 Id, Ordinal, Id) 로 변경 — page 자연 정렬 보장.
        // 종전 `ORDER BY Ordinal, Id` 결함: 페이지당 본문 chunk 가 2개 이상이면 ord 0 (전 페이지) → ord 1 (전 페이지)
        // 순서로 출력되어 페이지 grouping 깨짐 (실측은 페이지당 1 chunk 가 dominant 라 보이지 않았음). caption-chunk
        // 박제로 본문 chunk 와 같은 RefLocator 안 ordinal >= 1 row 등장 빈도 증가 → 결함 노출.
        // window function `MIN(Id) OVER (PARTITION BY RefLocator)` 가 RefLocator 의 첫 INSERT 순서 (=
        // 본문 chunks INSERT 순서 = page 자연 순서) 를 sort key 로 사용 → page grouping 정합.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT RefLocator, Text FROM Chunks
            WHERE DocumentId = $doc
            ORDER BY MIN(Id) OVER (PARTITION BY RefLocator), Ordinal, Id
        """
        cmd.Parameters.AddWithValue("$doc", docId) |> ignore
        use reader = cmd.ExecuteReader()
        let mutable lastRef = ""
        while reader.Read() do
            let refLoc = reader.GetString 0
            let text = reader.GetString 1
            if refLoc <> lastRef then
                if sb.Length > 0 then sb.AppendLine() |> ignore
                sb.AppendLine(formatHeading refLoc) |> ignore
                sb.AppendLine() |> ignore
                lastRef <- refLoc
            sb.AppendLine text |> ignore

        // image gallery section — ImageReferences + ImageCache caption
        let imgRefs = ImageStore.lookupReferencesByDocument conn docId
        if imgRefs.Length > 0 then
            sb.AppendLine() |> ignore
            sb.AppendLine("---") |> ignore
            sb.AppendLine() |> ignore
            sb.AppendLine(sprintf "## Images (%d)" imgRefs.Length) |> ignore
            sb.AppendLine() |> ignore
            for (hash, refLoc, ord, _) in imgRefs do
                let caption =
                    match ImageStore.getCaption conn hash with
                    | Some (text, _model) -> text
                    | None -> ImageStore.CaptionPlaceholderText
                sb.AppendLine(sprintf "### %s #img=%d" refLoc ord) |> ignore
                sb.AppendLine(sprintf "_hash=%s_" (hash.Substring(0, min 12 hash.Length))) |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine caption |> ignore
                sb.AppendLine() |> ignore
        sb.ToString()

    /// **PR-C** — markdown byte size 검사 + cap 적용 + footer 박제.
    /// UTF-8 byte 기준 — 한글 1자 = 3 byte 이므로 char count 보다 보수적.
    let private applySizeCap (markdown: string) : string =
        let bytes = Encoding.UTF8.GetByteCount markdown
        if bytes <= MaxDumpBytes then markdown
        else
            // UTF-8 boundary safe truncate — char 단위 binary search 대신 단순 truncate
            // (char count 가 byte 보다 작으므로 cap 안 박제 안전).
            let footerBytes = Encoding.UTF8.GetByteCount TruncateFooter
            let availableBytes = MaxDumpBytes - footerBytes
            // markdown 의 prefix byte 가 availableBytes 이하 되는 최대 char position 탐색
            let utf8 = Encoding.UTF8
            let mutable cut = markdown.Length
            while cut > 0 && utf8.GetByteCount(markdown.Substring(0, cut)) > availableBytes do
                cut <- cut - 1
            markdown.Substring(0, cut) + TruncateFooter

    /// **PR-C** — collection 의 모든 Document 에 대해 text dump 생성 + 파일 write.
    /// 반환 = (생성된 file path, 적용된 byte size) array. 빈 collection → `[||]`.
    /// 호출 위치: CLI `runUpload` 의 색인 완료 후 + `Packager.createZip` 직전.
    let dumpAll (conn: SqliteConnection) (collectionRoot: string) : (string * int) array =
        let dir = textDir collectionRoot
        Directory.CreateDirectory dir |> ignore
        let results = ResizeArray<string * int>()

        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Id, OriginalPath, DocType FROM Documents ORDER BY Id"
        use reader = cmd.ExecuteReader()
        let docs = ResizeArray<int64 * string * string>()
        while reader.Read() do
            let id = reader.GetInt64 0
            let path = reader.GetString 1
            let dt = reader.GetString 2
            docs.Add (id, path, dt)
        reader.Dispose()  // 명시 dispose — 다음 cmd (buildDocumentMarkdown 안 reader) 와 race 차단

        for (docId, origPath, docType) in docs do
            let raw = buildDocumentMarkdown conn docId origPath docType
            let final = applySizeCap raw
            let filename = sanitizedFilename docId origPath
            let outPath = Path.Combine(dir, filename)
            File.WriteAllText(outPath, final, UTF8Encoding(false))
            results.Add (outPath, Encoding.UTF8.GetByteCount final)
        results.ToArray()

    // ── PR-I3 (todo §2 / §2.1 / §8.5.5~§8.5.8) — strategy 정제 markdown 박제 hook ───────────

    /// strategy 박제 결과 — caller (CLI / 테스트) 진단용.
    [<NoComparison; NoEquality>]
    type StrategyDumpSummary = {
        /// 생성된 `summary/*.md` 파일 절대경로.
        SummaryFiles: string array
        /// rejected.json 박제 entry 수 (0 이면 파일 미생성).
        RejectedCount: int
        /// near-miss.json 박제 entry 수 (0 이면 파일 미생성).
        NearMissCount: int
    }

    /// **PR-I3 (todo §6.1 N1 + N4)** — diagnostic JSON 누적 박제. entry list 비어 있으면 파일 생성 0
    /// (직전 산출물이 있으면 wipe — in-place update). caller 가 file-level 동의어 catch 0 (write 실패 throw).
    let private writeDiagnosticJson<'T> (path: string) (entries: 'T list) : unit =
        if List.isEmpty entries then
            if File.Exists path then File.Delete path
        else
            let json = DiagnosticJson.serialize entries
            File.WriteAllText(path, json, UTF8Encoding(false))

    /// 단일 파일에 strategy 적용 — DocType 별 OoxmlExtractor / PdfExtractor 호출 후 classifier dispatch.
    /// 반환 = (Built markdown, source path) option + reject/near-miss accumulator append.
    let private applyStrategiesToFile
        (path: string)
        (extractors: IExtractor list)
        (ct: CancellationToken)
        (rejected: ResizeArray<RejectedEntry>)
        (nearMisses: ResizeArray<NearMissEntry>)
        : (string * string * string) option =  // (strategyName, docId, markdown)
        let kind = Classifier.classifyForKb path
        let extractorOpt = extractors |> List.tryFind (fun ex -> ex.Supports kind)
        match extractorOpt with
        | None -> None
        | Some extractor ->
            let extracted = extractor.Extract(path, ct)
            match extracted.DocType with
            | FileKind.Xlsx ->
                match XlsxSignatureClassifier.classify path extracted with
                | ClassificationResult.Matched (strategyName, markdown) ->
                    let docId = StrategyMarkdown.computeDocId path
                    Some (strategyName, docId, markdown)
                | ClassificationResult.RejectedByStrategy entry ->
                    rejected.Add entry
                    None
                | ClassificationResult.NearMiss entries ->
                    for e in entries do nearMisses.Add e
                    None
                | ClassificationResult.Unmatched -> None
            | FileKind.Pdf ->
                match PdfSignatureClassifier.classify path extracted with
                | ClassificationResult.Matched (strategyName, markdown) ->
                    let docId = StrategyMarkdown.computeDocId path
                    Some (strategyName, docId, markdown)
                | ClassificationResult.RejectedByStrategy entry ->
                    rejected.Add entry
                    None
                | ClassificationResult.NearMiss entries ->
                    for e in entries do nearMisses.Add e
                    None
                | ClassificationResult.Unmatched -> None
            | _ -> None  // strategy 미정의 DocType (Docx/Pptx/Text/Markdown/Image/Unsupported) — fallback 0

    /// `.lighthouse-kb/` skip + collection root 재귀 enumerate (Packager.createZip 의 동형 SSOT).
    let private enumerateSourceFiles (collectionRoot: string) : string array =
        let kbPrefix =
            (Path.GetFullPath(SqliteStore.kbDir collectionRoot)).TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar
        Directory.EnumerateFiles(collectionRoot, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun p ->
            let pNorm = Path.GetFullPath p
            not (pNorm.StartsWith(kbPrefix, StringComparison.OrdinalIgnoreCase)))
        |> Seq.toArray

    /// **PR-I3** — collection root 안 모든 파일에 strategy 분기 적용 + `summary/` 박제 + rejected/near-miss 누적.
    ///
    /// 흐름:
    ///   1. `.lighthouse-kb/` 제외 모든 파일 enumerate.
    ///   2. DocType 별 extractor (OoxmlExtractor / PdfExtractor) 호출 후 classifier dispatch.
    ///   3. Matched → `summary/{strategyName}-{docId}-{basename}.md` 박제.
    ///   4. Rejected → `rejected.json` 누적 (N1 default).
    ///   5. NearMiss → `near-miss.json` 누적 (N4 default).
    ///
    /// 멱등 — 동일 collection 재호출 시 `summary/` wipe 후 재생성, rejected/near-miss 도 overwrite.
    /// caller 의 catch 0 — extract / IO 실패는 그대로 throw (fail-fast 정합, CLAUDE.md).
    /// `extractors` = caller 책임 lifecycle (TextExtractor / PdfExtractor / OoxmlExtractor / ImageExtractor).
    let dumpStrategySummaries
        (collectionRoot: string)
        (extractors: IExtractor list)
        (ct: CancellationToken)
        : StrategyDumpSummary =
        let dir = summaryDir collectionRoot
        // 멱등 박제 — 기존 `summary/` 통째 wipe 후 재생성 (strategy 결과 stale 방어).
        if Directory.Exists dir then Directory.Delete(dir, true)
        Directory.CreateDirectory dir |> ignore

        let summaryPaths = ResizeArray<string>()
        let rejected = ResizeArray<RejectedEntry>()
        let nearMisses = ResizeArray<NearMissEntry>()

        for path in enumerateSourceFiles collectionRoot do
            ct.ThrowIfCancellationRequested()
            match applyStrategiesToFile path extractors ct rejected nearMisses with
            | Some (strategyName, docId, rawMarkdown) ->
                // **Backlog I (todo-documents-based-gfm.md §6.1 + documents-based-gfm.md §8.5.5)** —
                // strategy 의 raw markdown 에 MarkdownCapPolicy 3-stage escalation 적용. Stage 3
                // (Split) 진입 시 SplitParts 의 N 개 part 를 `{basename}.1.md` / `{basename}.2.md` ...
                // 다중 박제 (silent data loss 제거). 단일 part (Stage 0/1/2) 는 기존 파일명 유지.
                let capResult = MarkdownCapPolicy.applyCap rawMarkdown
                match capResult.SplitParts with
                | Some parts ->
                    // Stage 3 multi-part 박제.
                    parts
                    |> List.iteri (fun idx part ->
                        let filename = summaryPartFilename strategyName docId path (idx + 1)
                        let outPath = Path.Combine(dir, filename)
                        File.WriteAllText(outPath, part, UTF8Encoding(false))
                        summaryPaths.Add outPath)
                | None ->
                    // Stage 0/1/2 단일 박제 — 기존 파일명 패턴 유지.
                    let filename = summaryFilename strategyName docId path
                    let outPath = Path.Combine(dir, filename)
                    File.WriteAllText(outPath, capResult.Markdown, UTF8Encoding(false))
                    summaryPaths.Add outPath
            | None -> ()

        writeDiagnosticJson (rejectedJsonPath collectionRoot) (List.ofSeq rejected)
        writeDiagnosticJson (nearMissJsonPath collectionRoot) (List.ofSeq nearMisses)

        { SummaryFiles = summaryPaths.ToArray()
          RejectedCount = rejected.Count
          NearMissCount = nearMisses.Count }
