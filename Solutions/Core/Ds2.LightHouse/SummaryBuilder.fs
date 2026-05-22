namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open Microsoft.Data.Sqlite

/// **PR-H1 (todo-lighthouse-index-summary.md §11)** — doc-level 1줄 summary 박제.
///
/// P1 = 방법 3 (zero-cost): Title 우선, fallback = 첫 chunk 의 첫 sentence (≤ 120 char).
/// P2 진입 시 방법 5 (subagent batch) 흡수 — SummaryStore 신설 + listPendingSummaries CLI entry.
///
/// 산출물 = `<source>/.lighthouse-kb/summary.md` (markdown table).
/// 용도 = (a) Human 검수 — text dump 가 본문 그대로면 summary 는 영역 인지용 / (b) P2 의 attachment_summary MCP tool 응답.
///
/// CLI hook 위치 = `runIndex` / `runUpload` 의 TextDumper.dumpAll 직후 (단일 read-only connection 안).
[<RequireQualifiedAccess>]
module SummaryBuilder =

    /// `.lighthouse-kb/` 안 summary 파일명. createZip 의 자연 enumerate 가 흡수 (Packager.fs:181 정합).
    [<Literal>]
    let SummaryFileName = "summary.md"

    /// 첫 sentence 잘림 cap (char) — LLM 한 줄 요약 가독 boundary.
    [<Literal>]
    let MaxSentenceChars = 120

    /// 단일 doc 의 summary metadata. P2 진입 시 CaptionModel 같은 필드 추가 가능.
    type DocSummary = {
        DocId: int64
        OriginalPath: string       // 원본 파일 path (Documents.OriginalPath 그대로)
        TextDumpPath: string       // text/<docId>-<sanitized>.md (relative, TextDumper sanitize 패턴 정합)
        Summary: string            // 1줄 요약 — P1 방법 3 (Title 또는 첫 sentence)
    }

    /// `.lighthouse-kb/summary.md` 절대 경로.
    let summaryPath (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, SummaryFileName)

    /// 의미 단위 prefix 의 최소 길이 — 첫 boundary 가 이보다 이른 위치면 *다음* boundary 까지 cascade.
    /// 짧은 token 단위 (예: "자동화기술실 설비제어기술2팀 2022.") 의 의미 손실 trigger 보호.
    [<Literal>]
    let private MinSentenceChars = 40

    /// 첫 chunk 에서 의미 단위 1줄 추출.
    /// 1. whitespace 정규화 — `\r\n` / `\n` / `\t` → single space, multi-space → single. 줄바꿈을 boundary 로
    ///    *치지 않음* — PDF layout 의 짧은 줄 단위 박제 ("자동화기술실" / "설비제어기술2팀" 등) 가 의미 단위 손실 trigger.
    /// 2. sentence boundary (마침표/물음표/느낌표 + CJK 변형) 만 cut. MinSentenceChars 이전 boundary 는 skip.
    /// 3. boundary 미발견 시 MaxSentenceChars 에서 truncate.
    let private firstSentence (chunkText: string) : string =
        if String.IsNullOrWhiteSpace chunkText then ""
        else
            // whitespace 정규화 — newline / tab 모두 single space, multi-space collapse
            let normalized =
                let raw = chunkText.Replace("\r\n", " ").Replace("\n", " ").Replace("\t", " ").Trim()
                let sb = StringBuilder(raw.Length)
                let mutable prevSpace = false
                for ch in raw do
                    if ch = ' ' then
                        if not prevSpace then sb.Append ch |> ignore
                        prevSpace <- true
                    else
                        sb.Append ch |> ignore
                        prevSpace <- false
                sb.ToString()
            // sentence boundary 검색 — MinSentenceChars 이전 boundary 는 skip (cascade).
            // digit 직후 마침표 (`2022.` / `12.` / `1.`) 는 boundary 아님 — 날짜 / 목차 번호 false-positive 차단.
            let boundaries = [| '.'; '?'; '!'; '。'; '？'; '！' |]
            let scanLen = min normalized.Length MaxSentenceChars
            let mutable cut = scanLen
            let mutable i = MinSentenceChars
            while i < scanLen && cut = scanLen do
                if Array.contains normalized.[i] boundaries then
                    let prevIsDigit = i > 0 && Char.IsDigit normalized.[i - 1]
                    if not prevIsDigit then cut <- i + 1
                i <- i + 1
            normalized.Substring(0, cut).Trim()

    /// summary 1줄 = 첫 chunk 의 의미 단위 prefix, 최종 fallback = basename (확장자 제거).
    /// **Title 무시** — PDF Information.Title 은 PowerPoint default ("슬라이드 1"), Word default ("Microsoft Word - ..."),
    /// "Untitled" 등 무의미 default 가 대부분. P2 진입 시 subagent 가 Title + Summary 결합 박제 검토.
    /// title 인자는 미사용 (signature 보존 — P2 진입 시 활용 path).
    let private buildSummary (_title: string option) (firstChunkText: string) (originalPath: string) : string =
        let s = firstSentence firstChunkText
        if s.Length > 0 then s
        elif not (String.IsNullOrEmpty originalPath) then Path.GetFileNameWithoutExtension originalPath
        else "(no summary)"

    /// markdown table cell escape — `|` 와 newline 만 손질. " 는 markdown table 안 자유.
    let private escapeMd (s: string) : string =
        if isNull s then ""
        else
            s.Replace("|", "\\|")
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim()

    /// TextDumper.sanitizedFilename 과 동등한 sanitize — DocSummary.TextDumpPath 박제용 (실제 file 존재 검사 X).
    let private sanitizedTextDumpRel (docId: int64) (originalPath: string) : string =
        let basename =
            if String.IsNullOrEmpty originalPath then "untitled"
            else Path.GetFileNameWithoutExtension originalPath
        let sb = StringBuilder(basename.Length)
        for ch in basename do
            if Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.' then sb.Append ch |> ignore
            else sb.Append '_' |> ignore
        let safe = if sb.Length = 0 then "untitled" else sb.ToString()
        sprintf "%s/%d-%s.md" TextDumper.TextSubDirName docId safe

    /// 모든 doc enumerate → DocSummary array. 빈 collection → `[||]`.
    let build (conn: SqliteConnection) : DocSummary array =
        let results = ResizeArray<DocSummary>()
        use cmd = conn.CreateCommand()
        // 단일 query 로 doc + 첫 chunk text 결합 — N+1 query 회피 (대형 collection 의 read-only cost 최소).
        cmd.CommandText <- """
            SELECT d.Id, d.OriginalPath, d.Title,
                   (SELECT Text FROM Chunks WHERE DocumentId = d.Id ORDER BY Ordinal, Id LIMIT 1) AS FirstChunk
            FROM Documents d
            ORDER BY d.Id
        """
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let docId = reader.GetInt64 0
            let origPath = reader.GetString 1
            let title = if reader.IsDBNull 2 then None else Some (reader.GetString 2)
            let firstChunk = if reader.IsDBNull 3 then "" else reader.GetString 3
            results.Add {
                DocId = docId
                OriginalPath = origPath
                TextDumpPath = sanitizedTextDumpRel docId origPath
                Summary = buildSummary title firstChunk origPath
            }
        results.ToArray()

    /// `summary.md` 박제. 빈 array → header + 0-row 표 (정보 보존 의미 — collection 존재는 알리되 doc 0건 명시).
    /// 반환 = 박제된 file path.
    let write (collectionRoot: string) (summaries: DocSummary array) : string =
        let dir = SqliteStore.kbDir collectionRoot
        Directory.CreateDirectory dir |> ignore
        let outPath = summaryPath collectionRoot
        // text dump 의 실제 byte 합 — 검수 도움 (text dump 가 cap 걸렸는지 가늠).
        let totalDumpBytes =
            summaries
            |> Array.sumBy (fun s ->
                let full = Path.Combine(dir, s.TextDumpPath)
                if File.Exists full then (FileInfo full).Length else 0L)
        let sb = StringBuilder()
        sb.AppendLine "# Collection Summary" |> ignore
        sb.AppendLine(sprintf "_생성: %s UTC | docs: %d | text dump 합계: %s byte_"
            (DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"))
            summaries.Length
            (totalDumpBytes.ToString("N0"))) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine "| 원본 | text dump | 요약 |" |> ignore
        sb.AppendLine "|---|---|---|" |> ignore
        for s in summaries do
            sb.AppendLine(sprintf "| %s | %s | %s |"
                (escapeMd (Path.GetFileName s.OriginalPath))
                (escapeMd s.TextDumpPath)
                (escapeMd s.Summary)) |> ignore
        File.WriteAllText(outPath, sb.ToString(), UTF8Encoding(false))
        outPath
