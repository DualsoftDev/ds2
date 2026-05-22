namespace Ds2.LightHouse

open System
open Microsoft.Data.Sqlite

/// **PR-H2 (todo-lighthouse-index-summary.md §11)** — Documents.SummaryText UPSERT + pending enum.
///
/// `ImageStore.listPendingCaptions` / `updateCaptionBatch` (caption-fill, Step 2) 의 동형 패턴 —
/// SummaryText 가 NULL 인 doc 만 enumerate + subagent batch 결과 단일 transaction UPSERT.
///
/// CLI entry mapping (Step 2b "summary-fill"):
///   - `lighthouse-cli list-pending-summaries <folder>` → `listPendingSummaries` stdout JSON
///   - `lighthouse-cli summary-update <folder> <batch.json>` → `updateSummaryBatch` 단일 transaction
///
/// SummaryBuilder.build 가 본 module 의 UPSERT 결과 (Documents.SummaryText IS NOT NULL) 를 우선 분기 박제.
/// NULL 인 doc 은 P1 방법 3 (첫 chunk firstSentence) 으로 fallback.
[<RequireQualifiedAccess>]
module SummaryStore =

    /// Step 2b pending record — `list-pending-summaries` 의 stdout JSON 단위.
    /// subagent prompt 에 박제될 hint — text dump file path 우선 전달 (subagent 가 Read 도구로 본문 흡수).
    type SummaryPendingRecord = {
        DocId: int64
        OriginalPath: string
        TextDumpPath: string       // text/<docId>-<sanitized>.md (SummaryBuilder.sanitizedTextDumpRel 정합)
    }

    /// `Documents.SummaryText IS NULL` 인 doc 만 enumerate. 이미 박제된 row 는 자연 제외 (idempotent retry).
    /// invariant: hash 당 1 row 보장 (Documents.FileHash UNIQUE — SqliteStore schema §147~158).
    let listPendingSummaries (conn: SqliteConnection) : SummaryPendingRecord seq =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT Id, OriginalPath
            FROM Documents
            WHERE SummaryText IS NULL
            ORDER BY Id
        """
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<SummaryPendingRecord>()
        while reader.Read() do
            let docId = reader.GetInt64 0
            let origPath = reader.GetString 1
            acc.Add {
                DocId = docId
                OriginalPath = origPath
                TextDumpPath = SummaryBuilder.sanitizedTextDumpRel docId origPath
            }
        acc :> SummaryPendingRecord seq

    /// `summary-update` entry — subagent 가 return 한 (docId, summary) batch 를 단일 transaction 안 N 회 UPDATE.
    /// 빈 batch → no-op (transaction 미생성, exit 0). 반환 = update 적용된 row 수.
    ///
    /// caller 측에서 BeginTransaction 박제 누락 시 Microsoft.Data.Sqlite 의 Transaction mismatch 회피 위해
    /// 본 함수가 transaction lifecycle 흡수 (ImageStore.updateCaptionBatch §211 정합).
    let updateSummaryBatch
        (conn: SqliteConnection)
        (rows: (int64 * string) seq)
        : int =
        let arr = rows |> Seq.toArray
        if arr.Length = 0 then 0
        else
            use tx = conn.BeginTransaction()
            let mutable n = 0
            for (docId, summary) in arr do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- """
                    UPDATE Documents
                    SET SummaryText = $text
                    WHERE Id = $id
                """
                cmd.Parameters.AddWithValue("$id",   docId)   |> ignore
                cmd.Parameters.AddWithValue("$text", summary) |> ignore
                // **review B fix**: 시도 횟수 (n + 1) 가 아닌 실제 affected row 수 반환. 환각 docId
                // (DB 에 없는 Id) 입력 시 silent 0 update — 호출자가 진단할 수 있도록 정확 보고.
                n <- n + cmd.ExecuteNonQuery()
            tx.Commit()
            n

    /// subagent prompt SSOT — `print-summary-prompt` entry (Step 2b §11 — 사본 박제 없이 매 진입 시 fetch).
    /// caption-prompt 와 동형. 다음 turn 의 subagent dispatch 가 본 문자열을 그대로 prompt 에 박제.
    [<Literal>]
    let SummaryPrompt = """다음 markdown 본문은 한 document 의 전체 본문 dump 입니다.
이 문서의 *영역 / 주제* 를 한 문장 (한국어, 80~120자) 으로 요약해주세요.
- 자질구레한 표제지 정보 (담당자명 / 날짜 / 도장 / RESTRICTED 등) 는 무시.
- 본문의 *주된 내용* (어떤 시스템 / 어떤 사양 / 어떤 분야) 만 압축.
- 출력 형식 — **마지막 줄은 단일 JSON line**:
{"docId":<int>,"summary":"<요약 문장>"}
"""
