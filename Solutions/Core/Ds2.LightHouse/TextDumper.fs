namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open Microsoft.Data.Sqlite

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

    /// truncate 시 박제 footer (LLM 안내 + attachment_search escalation 권장).
    [<Literal>]
    let TruncateFooter = "\n\n---\n\n[text dump truncated at 512KB — use attachment_search for specific ref]\n"

    /// `<source>/.lighthouse-kb/text/` 절대 경로.
    let textDir (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, TextSubDirName)

    /// docId + originalPath → filename (path traversal 차단 + 의심 char 제거).
    /// 결과 = `<docId>-<basename>.md` (basename 은 의심 char `/\:*?"<>|` 등 replace `_`).
    /// **review F fix (r4)**: public 격상 — SummaryBuilder + SummaryStore 가 사본 박제 회피 위해 직접 참조.
    /// drift 시 attachment_summary 의 textDumpPath ↔ attachment_fulltext 의 docId 매칭 fail 차단.
    let sanitizedFilename (docId: int64) (originalPath: string) : string =
        let basename =
            if String.IsNullOrEmpty originalPath then "untitled"
            else Path.GetFileNameWithoutExtension originalPath
        let sb = StringBuilder(basename.Length)
        for ch in basename do
            if Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.' then sb.Append ch |> ignore
            else sb.Append '_' |> ignore
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

        // chunks streaming — RefLocator 변화 detection 으로 heading 박제
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT RefLocator, Text FROM Chunks WHERE DocumentId = $doc ORDER BY Ordinal, Id"
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
                    | None -> "(caption 미생성)"
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
