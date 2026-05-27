namespace Ds2.LightHouse

open System
open System.Text

/// **Backlog D (todo-documents-based-gfm.md §6.1 N5 + documents-based-gfm.md §8.5.5)** —
/// strategy summary markdown 의 256KB cap 정책 SSOT.
///
/// **배경**: 자료 A (광명2 SIDE OUTER SV IO LIST STD MAP.xlsx) 의 IoListStrategy 산출 markdown 이
/// 약 454KB 로 §8.5.5 의 단일 doc summary 256KB cap 초과. SSOT 의 3 단계 압축 정책을 helper module
/// 로 박제하여 strategy 호출 site (현재 IoListStrategy) + 후속 strategy 가 공용.
///
/// **3 단계 압축** (`documents-based-gfm.md` §8.5.5 SSOT):
///   1. **컬럼 truncate** — 표 data row 안 각 cell 의 문자열 길이 `MaxCellChars` 이하로 truncate
///      (`...` 박제). 표 header row / alignment row / H1/H2/quote/comment 행은 보존.
///   2. **sample (head/tail + elide)** — 표 data row 중 처음 `SampleHeadCount` 개 + 마지막
///      `SampleTailCount` 개만 keep, 중간은 단일 elide placeholder row 박제 (예:
///      `| ... | (N rows elided) | ... | ... | ... | ... |`). SSOT (`§8.5.5`) 의
///      "N개 중 처음 5 + 마지막 5 + ...N개 생략" 정책 정합. 표 무결성 유지 (header + alignment + 박제 행 +
///      elide row + tail 행). drop 발생 시 안내 comment 박제.
///   3. **split** — 위 두 단계 후도 초과 시 표 data row 를 N 등분하여 part 분할
///      (각 part 가 cap 안 들어갈 때까지 N 증가). header/footer 는 part 별 공유.
///
/// 각 단계는 markdown 의 표 무결성 유지 (alignment row / header row 보존). 표 외 (H1/H2/quote/
/// comment) 행은 변경하지 않음 — strategy header 5행 / footer 7행 / H1 / H2 / quote 박제 그대로.
///
/// **fail-safe**: cell 안 multi-byte 문자 (한글 = UTF-8 3 byte) 정합 — truncate 는 char 단위
/// 이지만 byte cap 측정은 UTF-8 byte count. char 단위 truncate 보수적이므로 byte cap 안 안전.
[<RequireQualifiedAccess>]
module MarkdownCapPolicy =

    /// **§8.5.5 SSOT** — 단일 doc summary markdown 최대 byte (UTF-8). 256 KB.
    /// 초과 시 3 단계 압축 자동 escalation.
    [<Literal>]
    let MaxMarkdownBytes = 262144  // 256 * 1024

    /// Stage 1 — 표 data cell 의 최대 char 길이. 초과 시 substring + `...` suffix.
    /// 80 char ≈ 한글 ~40 자 / 영문 ~80 자. PLC tag/address 는 통상 < 40 char 라 영향 0.
    /// 영향 받는 cell = description / symbol comment 등 긴 자유 텍스트.
    [<Literal>]
    let MaxCellChars = 80

    /// Stage 1 의 truncate suffix.
    [<Literal>]
    let TruncateSuffix = "..."

    /// Stage 2 — head/tail sampling 의 head 행 수 (표 시작부 보존).
    /// SSOT (`documents-based-gfm.md` §8.5.5) — "처음 5 + 마지막 5 + ...N개 생략".
    [<Literal>]
    let SampleHeadCount = 5

    /// Stage 2 — head/tail sampling 의 tail 행 수 (표 끝부 보존).
    [<Literal>]
    let SampleTailCount = 5

    /// Stage 3 split 의 최대 분할 개수. 이 N 넘으면 fail-fast (자료 비정상).
    [<Literal>]
    let SplitMaxParts = 32

    /// 단계별 결과 식별자.
    type CapStage =
        /// cap 안 들어가 압축 0.
        | Original
        /// Stage 1 적용 — 컬럼 truncate (`maxCellChars` 적용).
        | ColumnTruncated of maxCellChars: int
        /// Stage 2 적용 — head/tail sampling (`headCount` + `tailCount` 행 keep, 중간 elide).
        | Sampled of headCount: int * tailCount: int
        /// Stage 3 적용 — split (`partIndex` of `partCount`).
        | Split of partIndex: int * partCount: int

    /// cap 적용 결과. `SplitParts` 는 Stage 3 일 때만 Some — 각 part markdown.
    [<NoComparison; NoEquality>]
    type CapResult = {
        /// Stage 3 미적용 시 단일 markdown (Original / ColumnTruncated / Sampled).
        /// Stage 3 적용 시 `SplitParts` 의 part 1 markdown 동일 (caller 가 Stage 3 인지로 SplitParts 사용).
        Markdown: string
        /// 어느 단계에서 cap 안 들어갔는지.
        Stage: CapStage
        /// 최종 결과 UTF-8 byte size. Stage 3 일 때는 part 1 의 size.
        SizeBytes: int
        /// Stage 3 일 때만 Some — 각 part 의 markdown list.
        SplitParts: string list option
    }

    // ── markdown line 분류 helper ──────────────────────────────────────────

    /// 표 alignment row 식별 — `|:---|:---:|---:|` 패턴 (cell 안 모두 `-` + `:` 만).
    let private isAlignmentRow (line: string) : bool =
        if String.IsNullOrEmpty line then false
        elif not (line.StartsWith "|") then false
        else
            let trimmed = line.Trim()
            // alignment row 는 `|` 와 `:` 와 `-` 와 space 만 포함.
            trimmed
            |> Seq.forall (fun ch -> ch = '|' || ch = ':' || ch = '-' || ch = ' ')

    /// 표 row 식별 — `|` 로 시작 + cell 구분자 `|` 2개 이상.
    let private isTableRow (line: string) : bool =
        if String.IsNullOrEmpty line then false
        elif not (line.StartsWith "|") then false
        else
            // 최소 2개 cell separator → 3 개 `|` 이상.
            line |> Seq.filter (fun ch -> ch = '|') |> Seq.length >= 3

    /// 표 row 의 cell 분해. `|` 로 split 후 leading/trailing empty 제거.
    /// `\|` (escape) 는 cell 안 char 로 보존.
    let private splitCells (line: string) : string array =
        // **B·m4 (Outlier/Minor 묶음 1)** — sentinel 을 자연 발생 가능성 있는 0x01 (control char)
        // 나 빈 문자열에서 PUA (Private Use Area, U+E000~U+F8FF) 로 전환. 분석 대상 markdown 안
        // PUA 등장 일반 문서에서 의료 이루며 거의 없음 → `\|` escape 복구의 collision risk 0.
        // `\|` 를 임시 sentinel 로 치환 → `|` split → sentinel 복구.
        let sentinel = ""  // PUA U+E000
        let safe = line.Replace(@"\|", sentinel)
        let cells = safe.Split('|')
        // leading/trailing empty 제거 (행이 `|` 로 시작/끝).
        let trimmedStart =
            if cells.Length > 0 && cells.[0] = "" then cells.[1..] else cells
        let trimmedEnd =
            if trimmedStart.Length > 0 && trimmedStart.[trimmedStart.Length - 1] = "" then
                trimmedStart.[.. trimmedStart.Length - 2]
            else trimmedStart
        trimmedEnd |> Array.map (fun c -> c.Replace(sentinel, @"\|"))

    /// cell 배열 → `| c1 | c2 | ... |` 형식 복원.
    let private joinCells (cells: string array) : string =
        let sb = StringBuilder()
        sb.Append('|') |> ignore
        for c in cells do
            sb.Append(' ') |> ignore
            sb.Append(c) |> ignore
            sb.Append(' ') |> ignore
            sb.Append('|') |> ignore
        sb.ToString()

    /// **B·M1 / F·M4 (Outlier/Minor 묶음 1)** — 표 영역 종료 신호 식별. 종전 구현은 blank line
    /// 만 종료 신호로 보아 직접 H2 / quote (`>`) / comment (`<!--`) 또는 다음 표의 새 header
    /// 가 등장해도 표 영역이 계속이라고 잘못 판정 → Stage 1/2/3 의 표 무결성 깨짐 결함.
    /// 본 helper 는 "현재 line 이 표 row / alignment row 가 아니면서 표 영역을 종료하는
    /// content (blank / H1 / H2 / quote / comment) 인가" 를 판정.
    let private isSectionBoundary (line: string) : bool =
        if String.IsNullOrWhiteSpace line then true
        else
            let t = line.TrimStart()
            t.StartsWith("# ")
            || t.StartsWith("## ")
            || t.StartsWith("### ")
            || t.StartsWith("> ")
            || t.StartsWith("<!--")
            || t.StartsWith("---")

    /// 한 cell 의 char 길이를 `maxCellChars` 로 truncate. 초과 시 substring + suffix.
    /// alignment row cell (`:---:` 등) 은 그대로 (alignment row 자체가 별 path).
    let private truncateCell (maxCellChars: int) (cell: string) : string =
        let trimmed = cell.Trim()
        if trimmed.Length <= maxCellChars then cell
        else
            let cut = max 0 (maxCellChars - TruncateSuffix.Length)
            // leading whitespace 유지 (cell 의 space padding 정합).
            let leadingWs =
                cell |> Seq.takeWhile Char.IsWhiteSpace |> Seq.length
            let prefix = cell.Substring(0, leadingWs)
            prefix + trimmed.Substring(0, min cut trimmed.Length) + TruncateSuffix

    // ── markdown body / data row 구조 분리 ──────────────────────────────

    /// markdown 을 line 단위로 분해. line ending 은 `\n` 단일 (입력이 `\r\n` 이면 `\r` strip).
    let private splitLines (markdown: string) : string array =
        markdown.Split([| '\n' |])
        |> Array.map (fun l -> l.TrimEnd('\r'))

    /// line 배열 → markdown 복원 (LF 단일). 마지막 trailing newline 보존 (입력 정합).
    let private joinLines (lines: string array) (trailingNewline: bool) : string =
        let body = String.concat "\n" lines
        if trailingNewline then body + "\n" else body

    /// markdown body 에 trailing newline 이 있는지.
    let private hasTrailingNewline (markdown: string) : bool =
        markdown.Length > 0 && markdown.[markdown.Length - 1] = '\n'

    // ── Stage 1 — 컬럼 truncate ────────────────────────────────────────

    /// 표 data row 의 각 cell 을 `maxCellChars` 로 truncate.
    /// header row + alignment row 는 그대로 (header 다음 alignment row 다음 부터 data row).
    let private applyColumnTruncate (maxCellChars: int) (markdown: string) : string =
        let trailing = hasTrailingNewline markdown
        let lines = splitLines markdown
        // 표 영역 tracker — header row 등장 후 alignment row 통과 이후 data row 들 truncate.
        let mutable inDataSection = false
        let mutable prevWasTableRow = false
        let result =
            lines
            |> Array.map (fun line ->
                if isAlignmentRow line then
                    inDataSection <- true
                    prevWasTableRow <- true
                    line
                elif isTableRow line then
                    if inDataSection then
                        // data row — cell truncate.
                        let cells = splitCells line
                        let truncated = cells |> Array.map (truncateCell maxCellChars)
                        prevWasTableRow <- true
                        joinCells truncated
                    else
                        // header row (alignment row 이전) — 그대로.
                        prevWasTableRow <- true
                        line
                else
                    // 표 외 line — H1/H2/quote/comment/blank. 표 영역 종료 신호.
                    // **B·M1 / F·M4 fix** — blank line 만이 아니라 H2/quote/comment 등 모든
                    // section boundary 도 종료 신호 (다음 표가 직접 등장해도 종료 정합).
                    if prevWasTableRow && isSectionBoundary line then
                        inDataSection <- false
                    prevWasTableRow <- false
                    line)
        joinLines result trailing

    // ── Stage 2 — head/tail sampling + 중간 elide ──────────────────────

    /// 표 한 section 의 data row 갯수를 미리 count.
    /// alignment row 다음부터 다음 비-표 line (또는 blank) 까지의 data row count.
    let private countSectionDataRows (lines: string array) (sectionStartIdx: int) : int =
        // sectionStartIdx = alignment row 의 index. 그 다음부터 count.
        let mutable cnt = 0
        let mutable i = sectionStartIdx + 1
        let mutable stop = false
        while not stop && i < lines.Length do
            let line = lines.[i]
            if isTableRow line && not (isAlignmentRow line) then
                cnt <- cnt + 1
                i <- i + 1
            else
                stop <- true
        cnt

    /// alignment row 의 cell 개수 추출 — elide row 의 cell 개수 정합.
    let private alignmentCellCount (line: string) : int =
        splitCells line |> Array.length

    /// 표 data row 를 head 5 + tail 5 + 중간 elide single row 로 sampling.
    /// elide row 형식: `| ... | ... | (N rows elided) | ... | ... | ... |` (cell 개수 = alignment row 정합).
    /// drop 발생 시 표 끝에 `<!-- sampled: head N + tail M, elided E -->` 안내 박제.
    let private applySampling (headCount: int) (tailCount: int) (markdown: string) : string =
        if headCount < 0 || tailCount < 0 then markdown
        else
            let trailing = hasTrailingNewline markdown
            let lines = splitLines markdown
            let result = ResizeArray<string>()
            let mutable i = 0
            let mutable inDataSection = false
            let mutable prevWasTableRow = false
            // 현재 section 의 alignment row cell 개수 + total data row count.
            let mutable sectionCellCount = 0
            let mutable sectionTotalRows = 0
            let mutable sectionDataIdx = 0
            // B·m2 (Outlier/Minor 묶음 1) — sectionKept mutable 변수 제거 (dead increment 0 사용처).
            let mutable sectionDropped = 0

            let flushSampleNote () =
                if sectionDropped > 0 then
                    result.Add (
                        sprintf "<!-- sampled: head %d + tail %d kept, %d row(s) elided -->"
                            (min headCount sectionTotalRows)
                            (min tailCount (sectionTotalRows - min headCount sectionTotalRows))
                            sectionDropped)
                sectionDropped <- 0
                sectionDataIdx <- 0
                sectionTotalRows <- 0
                sectionCellCount <- 0

            let buildElideRow (cellCnt: int) (elided: int) : string =
                // cell 0 = `...`, cell 1 = `(N rows elided)`, 나머지 cell = `...`.
                // cell 수 < 2 인 비정상 표는 단일 `...` cell 만.
                if cellCnt <= 0 then
                    sprintf "| (%d rows elided) |" elided
                else
                    let cells =
                        Array.init cellCnt (fun idx ->
                            if idx = (min 1 (cellCnt - 1)) then
                                sprintf "(%d rows elided)" elided
                            else
                                "...")
                    joinCells cells

            while i < lines.Length do
                let line = lines.[i]
                if isAlignmentRow line then
                    // 새 section 진입 — section 의 total data row count 미리 계산.
                    inDataSection <- true
                    prevWasTableRow <- true
                    sectionCellCount <- alignmentCellCount line
                    sectionTotalRows <- countSectionDataRows lines i
                    sectionDataIdx <- 0
                    sectionDropped <- 0
                    result.Add line
                elif isTableRow line then
                    if inDataSection then
                        // head/tail 5 + 중간 elide 결정.
                        let keepHead = sectionDataIdx < headCount
                        let keepTail = sectionDataIdx >= sectionTotalRows - tailCount
                        let needsElide = sectionTotalRows > headCount + tailCount
                        if keepHead then
                            result.Add line
                        elif needsElide && sectionDataIdx = headCount then
                            // elide row 박제 (한 번만).
                            let elidedCnt = sectionTotalRows - headCount - tailCount
                            result.Add (buildElideRow sectionCellCount elidedCnt)
                            sectionDropped <- sectionDropped + elidedCnt
                            // 본 row 자체는 drop (이미 elide 안 포함).
                        elif keepTail then
                            result.Add line
                        else
                            // 중간 drop (elide row 박제 후 또는 needsElide=false 인 경우 모든 row keep 이라 여기 도달 안 됨).
                            ()
                        sectionDataIdx <- sectionDataIdx + 1
                        prevWasTableRow <- true
                    else
                        // header row — keep.
                        result.Add line
                        prevWasTableRow <- true
                else
                    // **B·M1 / F·M4 fix** — blank 외 H2/quote/comment 등 section boundary 도 종료.
                    if prevWasTableRow && isSectionBoundary line then
                        // 표 영역 종료 — sample note flush.
                        flushSampleNote ()
                        inDataSection <- false
                    prevWasTableRow <- false
                    result.Add line
                i <- i + 1

            // 마지막 표가 끝까지 진행된 경우 (trailing blank line 없음) flush.
            flushSampleNote ()
            joinLines (result.ToArray()) trailing

    // ── Stage 3 — split ───────────────────────────────────────────────

    /// markdown 을 N 등분 — header (첫 H1 이전 + H1) / footer (`---` 다음) 공유,
    /// 표 data row 들을 N 등분하여 각 part 에 분배. 본 구현은 단순 — 모든 표 의 data row 를
    /// 통합 list 로 보고 N 등분 후 part 별 markdown 재조립. 표가 여러 section 으로 분리되어
    /// 있는 경우 (자료 A 처럼 시트별 H2 + 표) section 경계 보존 + part 별 data row 만 분배.
    ///
    /// 본 구현 단순화: data row 의 index range 를 N 등분하여 각 part 가 해당 range 의 data row
    /// 만 emit, 나머지는 drop. header/H2/alignment 는 part 마다 그대로 박제.
    let private buildSplitParts (partCount: int) (markdown: string) : string list =
        if partCount < 2 then [ markdown ]
        else
            let trailing = hasTrailingNewline markdown
            let lines = splitLines markdown
            // 1. 전체 data row 개수 count.
            let totalDataRows =
                let mutable cnt = 0
                let mutable inDataSection = false
                let mutable prevWasTableRow = false
                for line in lines do
                    if isAlignmentRow line then
                        inDataSection <- true
                        prevWasTableRow <- true
                    elif isTableRow line then
                        if inDataSection then cnt <- cnt + 1
                        prevWasTableRow <- true
                    else
                        // B·M1 / F·M4 fix — section boundary 일반화.
                        if prevWasTableRow && isSectionBoundary line then
                            inDataSection <- false
                        prevWasTableRow <- false
                cnt

            if totalDataRows = 0 then [ markdown ]
            else
                // 2. 각 part 의 data row range 계산.
                let perPart =
                    (totalDataRows + partCount - 1) / partCount  // ceil
                let parts = ResizeArray<string>()
                for partIdx in 0 .. partCount - 1 do
                    let startRow = partIdx * perPart
                    let endRow = min totalDataRows ((partIdx + 1) * perPart)
                    let partLines = ResizeArray<string>()
                    let mutable inDataSection = false
                    let mutable prevWasTableRow = false
                    let mutable dataRowIdx = 0
                    let mutable emittedInSection = 0
                    for line in lines do
                        if isAlignmentRow line then
                            inDataSection <- true
                            prevWasTableRow <- true
                            emittedInSection <- 0
                            partLines.Add line
                        elif isTableRow line then
                            if inDataSection then
                                if dataRowIdx >= startRow && dataRowIdx < endRow then
                                    partLines.Add line
                                    emittedInSection <- emittedInSection + 1
                                dataRowIdx <- dataRowIdx + 1
                                prevWasTableRow <- true
                            else
                                partLines.Add line
                                prevWasTableRow <- true
                        else
                            // B·M1 / F·M4 fix — section boundary 일반화.
                            if prevWasTableRow && isSectionBoundary line then
                                inDataSection <- false
                            prevWasTableRow <- false
                            partLines.Add line
                    // **B·M3 (Outlier/Minor 묶음 1)** — split note 위치를 머리말 5행 다음 + H1 이전
                    // 으로 보정. 종전 구현은 part markdown 의 가장 앞 (머리말 1행 보다 앞) 에 박제 →
                    // 머리말 첫 행이 `<!-- generated by ... -->` SSOT 인데 split note 가 그 앞에 박제되면
                    // 외부 reader (LLM / 진단 도구) 가 첫 5행 머리말 박제 invariant 를 못 인식. 본 fix 는
                    // partLines 안에서 첫 blank line (= 머리말 5행 직후 trailing blank) 위치를 찾아 그
                    // 다음에 noteLine 삽입. blank line 부재 시 (fixture 비정상) fallback 으로 맨 앞 박제.
                    let noteLine =
                        sprintf "<!-- split: part %d of %d (data rows %d~%d of %d) -->"
                            (partIdx + 1) partCount (startRow + 1) endRow totalDataRows
                    let arr = partLines.ToArray()
                    let insertIdx =
                        // 머리말 5행 직후 첫 blank line index 검색 → 그 다음 위치 (idx + 1) 에 insert.
                        let mutable idx = -1
                        let mutable i = 0
                        while idx < 0 && i < arr.Length do
                            if String.IsNullOrWhiteSpace arr.[i] then idx <- i
                            i <- i + 1
                        if idx < 0 then 0 else idx + 1
                    let injected = ResizeArray<string>(arr.Length + 1)
                    for i in 0 .. arr.Length - 1 do
                        if i = insertIdx then injected.Add noteLine
                        injected.Add arr.[i]
                    if insertIdx >= arr.Length then injected.Add noteLine
                    let partMd = joinLines (injected.ToArray()) trailing
                    parts.Add partMd
                parts |> List.ofSeq

    // ── byte size 측정 ─────────────────────────────────────────────────

    let private byteSize (markdown: string) : int =
        Encoding.UTF8.GetByteCount markdown

    // ── 단계 escalation 진입점 ─────────────────────────────────────────

    /// cap 정책 진입점. `markdown` 의 byte size 가 `MaxMarkdownBytes` 이하면 Original 반환.
    /// 초과 시 Stage 1 → 2 → 3 순으로 escalation. 모든 단계가 cap 안 흡수 가능하게 시도.
    let applyCap (markdown: string) : CapResult =
        let originalSize = byteSize markdown
        if originalSize <= MaxMarkdownBytes then
            { Markdown = markdown
              Stage = Original
              SizeBytes = originalSize
              SplitParts = None }
        else
            // **F·m1 (Outlier/Minor 묶음 1)** — Stage escalation 진단 logger 도입. 종전은 silent
            // escalation 으로 (어느 fixture 가 어느 단계 까지 갔는지 추적 불가) PR review 시 부담.
            // Log.lighthouse (`Ds2.LightHouse`) 로 통일.
            Log.lighthouse.Debug(
                sprintf "MarkdownCapPolicy: cap exceeded (size=%d > cap=%d), Stage 1 진입"
                    originalSize MaxMarkdownBytes)
            // Stage 1 — 컬럼 truncate.
            let stage1 = applyColumnTruncate MaxCellChars markdown
            let stage1Size = byteSize stage1
            if stage1Size <= MaxMarkdownBytes then
                Log.lighthouse.Debug(
                    sprintf "MarkdownCapPolicy: Stage 1 (ColumnTruncated, maxCellChars=%d) 흡수 size=%d"
                        MaxCellChars stage1Size)
                { Markdown = stage1
                  Stage = ColumnTruncated MaxCellChars
                  SizeBytes = stage1Size
                  SplitParts = None }
            else
                // Stage 2 — head/tail sampling (SSOT §8.5.5 — "처음 5 + 마지막 5 + ...N개 생략").
                // 단일 정책 — head 5 + tail 5 + 중간 elide. cap 안 흡수 못할 시 곧바로 Stage 3 진입.
                Log.lighthouse.Debug(
                    sprintf "MarkdownCapPolicy: Stage 1 미흡수 size=%d, Stage 2 진입" stage1Size)
                let stage2 = applySampling SampleHeadCount SampleTailCount stage1
                let stage2Size = byteSize stage2
                if stage2Size <= MaxMarkdownBytes then
                    Log.lighthouse.Debug(
                        sprintf "MarkdownCapPolicy: Stage 2 (Sampled head=%d, tail=%d) 흡수 size=%d"
                            SampleHeadCount SampleTailCount stage2Size)
                    { Markdown = stage2
                      Stage = Sampled (SampleHeadCount, SampleTailCount)
                      SizeBytes = stage2Size
                      SplitParts = None }
                else
                    // Stage 3 — split. partCount 를 size 기반 lower bound 부터 시도, doubling 으로 escalate.
                    // 단순 +1 escalation 은 N 큰 case 에 O(N^2) → doubling 으로 O(log N) 안.
                    //
                    // **--review 라운드 1 Critical-1 fix**: split 입력은 stage2 (head/tail sampling 적용본)
                    // 이어야 함. 기존 stage1 입력은 sampling 효과 무시 → split 후 각 part 가 cap 못 들어가 무한
                    // doubling → SplitMaxParts 초과 fail-fast 회귀 risk. SSOT (`§8.5.5`) 의 3 단계 escalation
                    // 정의 = "Stage 1 → 2 → 3 누적 적용" — Stage 3 가 Stage 2 결과를 입력으로 받음이 의도.
                    // 최소 partCount 추정: stage2 size / cap (ceil) — header 중복 무시한 lower bound.
                    Log.lighthouse.Debug(
                        sprintf "MarkdownCapPolicy: Stage 2 미흡수 size=%d, Stage 3 (Split) 진입" stage2Size)
                    let initialPartCount =
                        max 2 ((stage2Size + MaxMarkdownBytes - 1) / MaxMarkdownBytes)
                    let mutable partCount = initialPartCount
                    let mutable parts = []
                    let mutable splitDone = false
                    while not splitDone && partCount <= SplitMaxParts do
                        let candidate = buildSplitParts partCount stage2
                        let maxPartSize =
                            candidate
                            |> List.map byteSize
                            |> List.max
                        if maxPartSize <= MaxMarkdownBytes then
                            parts <- candidate
                            splitDone <- true
                        else
                            // header overhead 흡수 + safety margin — doubling.
                            partCount <- partCount * 2
                    if not splitDone then
                        // fail-fast — 자료 비정상.
                        failwithf
                            "MarkdownCapPolicy: split escalation failed (partCount=%d max), source markdown %d bytes 가 cap %d 안 안 들어감"
                            SplitMaxParts originalSize MaxMarkdownBytes
                    let firstPart = List.head parts
                    Log.lighthouse.Debug(
                        sprintf "MarkdownCapPolicy: Stage 3 (Split) partCount=%d 흡수, firstPartSize=%d"
                            partCount (byteSize firstPart))
                    { Markdown = firstPart
                      Stage = Split (1, partCount)
                      SizeBytes = byteSize firstPart
                      SplitParts = Some parts }
