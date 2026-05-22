namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open Microsoft.Data.Sqlite

/// **PR-H1/H2 (todo-lighthouse-index-summary.md §11)** — doc-level 1줄 summary 박제.
///
/// design (r5+): SummaryText 박제 path 단일화 = `lighthouse-cli summary-update` (subagent batch, Step 1-b).
/// **PR-H1 zero-cost fallback (firstSentence) 폐기** — PDF 표제지 layout (담당/날짜/RESTRICTED) 의 stale 박제
/// 결함이 결과적으로 design 의도 ("LLM 호출 없이 박제된 summary 박제") 를 fail 시킴. 명시적 placeholder 박제로
/// "박제 미완" 을 표면화하여 사용자 / Step 1-b dispatcher 가 즉시 인지.
///
/// 산출물 = `<source>/.lighthouse-kb/summary.md` (markdown table).
/// 용도 = (a) Human 검수 / (b) attachment_summary MCP tool 응답.
///
/// CLI hook 위치 = `runIndex` / `runUpload` / `runSummaryUpdate` 의 TextDumper.dumpAll 직후 (단일 connection 안).
[<RequireQualifiedAccess>]
module SummaryBuilder =

    /// `.lighthouse-kb/` 안 summary 파일명. createZip 의 자연 enumerate 가 흡수 (Packager.fs:181 정합).
    [<Literal>]
    let SummaryFileName = "summary.md"

    /// SummaryText NULL doc 의 placeholder. Step 1-b summary-fill 미진행 상태를 명시.
    /// fallback 박제 안 함 — firstSentence (표제지 stale 박제) 는 박제된 summary 와 시각적으로 구분 안 되어
    /// 혼동 trigger. placeholder 는 "박제 미완" 을 표면화 → 사용자 즉시 인지 + summary-update 흐름 진입 trigger.
    [<Literal>]
    let PendingPlaceholder = "(pending — summary-fill 미진행)"

    /// 단일 doc 의 summary metadata.
    type DocSummary = {
        DocId: int64
        OriginalPath: string       // 원본 파일 path (Documents.OriginalPath 그대로)
        TextDumpPath: string       // text/<docId>-<sanitized>.md (TextDumper sanitize 패턴 정합)
        Summary: string            // SummaryText 박제값 또는 PendingPlaceholder
    }

    /// `.lighthouse-kb/summary.md` 절대 경로.
    let summaryPath (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, SummaryFileName)

    /// markdown table cell escape — `|` 와 newline 만 손질. " 는 markdown table 안 자유.
    let private escapeMd (s: string) : string =
        if isNull s then ""
        else
            s.Replace("|", "\\|")
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim()

    /// DocSummary.TextDumpPath 박제용 (실제 file 존재 검사 X).
    /// **review F fix (r4)**: `TextDumper.sanitizedFilename` SSOT 재사용 (사본 박제 제거). drift 시 cross-tool
    /// inconsistency (attachment_summary 의 textDumpPath ↔ attachment_fulltext 의 docId 매칭 fail) 차단.
    let sanitizedTextDumpRel (docId: int64) (originalPath: string) : string =
        sprintf "%s/%s" TextDumper.TextSubDirName (TextDumper.sanitizedFilename docId originalPath)

    /// 모든 doc enumerate → DocSummary array. 빈 collection → `[||]`.
    /// SummaryText NULL → PendingPlaceholder 박제 (PR-H1 fallback 폐기, r5+).
    let build (conn: SqliteConnection) : DocSummary array =
        let results = ResizeArray<DocSummary>()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT d.Id, d.OriginalPath, d.SummaryText
            FROM Documents d
            ORDER BY d.Id
        """
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let docId = reader.GetInt64 0
            let origPath = reader.GetString 1
            let summary =
                if reader.IsDBNull 2 then PendingPlaceholder
                else
                    let s = reader.GetString 2
                    if String.IsNullOrWhiteSpace s then PendingPlaceholder else s.Trim()
            results.Add {
                DocId = docId
                OriginalPath = origPath
                TextDumpPath = sanitizedTextDumpRel docId origPath
                Summary = summary
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
