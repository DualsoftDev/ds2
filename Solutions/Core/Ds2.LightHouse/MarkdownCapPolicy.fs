namespace Ds2.LightHouse

open System
open System.Text

/// **Backlog D (todo-documents-based-gfm.md §6.1 N5 + documents-based-gfm.md §8.5.5)** —
/// strategy summary markdown 의 256KB cap 정책 SSOT.
///
/// **배경**: 자료 A (***REDACTED***2 SIDE OUTER SV IO LIST STD MAP.xlsx) 의 IoListStrategy 산출 markdown 이
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
/// comment) 행은 변경하지 않음 — strategy header 6행 (canary 1행 + 기존 5행, 2026-05-27 patch) / footer 7행 / H1 / H2 / quote 박제 그대로.
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

    /// **Phase 4 IoList 전용 SSOT (`todo-lighthouse-iolist-v2.md` Phase 4)** — IoList strategy 의
    /// device-aware sampling 에서 사용하는 시트별 head 행 수. 기존 정책 (5) 대비 2배 — device base
    /// token 분포 보존 + Direction 분포 박제 위해 더 많은 row 표본 keep.
    [<Literal>]
    let IoListSampleHeadCount = 10

    /// **Phase 4 IoList 전용 SSOT** — IoList strategy 의 device-aware sampling 의 시트별 tail 행 수.
    [<Literal>]
    let IoListSampleTailCount = 10

    /// **Phase 4 IoList 전용 SSOT (`todo-lighthouse-iolist-v2.md` Phase 4)** — IoList strategy 의
    /// `strategyName` 식별자. SSOT (`IoListStrategy.fs:50`) 의 `static let strategyName` 정합.
    /// `applyCapFor` 의 dispatch key — 본 값 매치 시 IoList 전용 sampling 분기.
    [<Literal>]
    let IoListStrategyName = "IoListStrategy"

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

    // ── Phase 4 IoList 전용 device-aware sampling ──────────────────────

    /// **Phase 4 (`todo-lighthouse-iolist-v2.md` Phase 4)** — IoList strategy 전용 Stage 2 sampling.
    ///
    /// 일반 `applySampling` 의 head 5 + tail 5 정책은 시트당 ~10 row 만 keep → 시트당 row 수가
    /// 평균 100+ 인 ***REDACTED***2 SIDE OUTER SV IO LIST (43 시트) 에서 device 정보 ~95% 소실 (4011 row 박제).
    /// 본 helper 는 IoList 의 도메인 구조 (Tag = device base token + bit suffix) 를 활용하여 device
    /// coverage 80%+ 박제.
    ///
    /// **알고리즘** (시트별 = section 별 적용):
    ///   1. 시트 안 모든 data row 의 Tag cell 에서 device base token 추출
    ///      (`reshapeTagToDeviceBase` — 마지막 `_TOKEN` 제거. 예: `S204_WRS_1ST_CLAMP1_ADV` → `S204_WRS_1ST_CLAMP1`).
    ///   2. unique device base token 별로 **첫 등장 row** 1건 선정 (device sample row).
    ///   3. 시트 head `IoListSampleHeadCount` (10) row + tail `IoListSampleTailCount` (10) row +
    ///      device sample row 합집합 keep. 나머지는 drop.
    ///   4. 시트 끝에 `<!-- sampled (IoList): N kept (head H + tail T + devices D), M elided. directions: IW=X, QW=Y -->`
    ///      안내 박제 — device coverage + Direction 분포 LLM 가시화.
    ///
    /// **invariant**:
    ///   - 표 alignment row / header row 보존 (`applySampling` 정합).
    ///   - 본 helper 는 IoList 의 6-col 표 layout (Word/Direction/Tag/DataType/Address/Symbol) 가정.
    ///     cell 개수 < 3 인 비정상 표는 fallback (일반 `applySampling` 결과 통과).
    ///   - device base token 추출 fail (Tag cell 빈 값 / `_` 부재) row 는 unique key 로 raw Tag 사용.
    ///
    /// **6-col layout 가정 위반 시 동작 박제** (Phase 5 자가 검열 Major-4 정정):
    ///   - `collectDeviceSampleIndices` 안 `cells.Length < 3` → Tag cell 부재 → `tagCell = ""` 박제.
    ///   - `reshapeTagToDeviceBase ""` 가 `""` 반환 → `key = ""` → `seen.Add key` 가 skip 되어 device set 빈 집합.
    ///   - 결과: device-aware 가 효과 0 → head/tail 만 keep 하는 일반 `applySampling` 동등 결과.
    ///   - 즉 fallback path 는 별도 `applySampling` 호출이 아니라 device set 빈집합 → head/tail 만 동작.
    ///     안전 fallback (Direction 분포 박제 + Stage 2 cap 흡수 동등).
    let private reshapeTagToDeviceBase (tag: string) : string =
        if String.IsNullOrWhiteSpace tag then ""
        else
            let trimmed = tag.Trim()
            let lastUnderscore = trimmed.LastIndexOf('_')
            if lastUnderscore <= 0 then trimmed
            else trimmed.Substring(0, lastUnderscore)

    /// IoList 시트 한 section 의 Direction 분포 박제 — Direction cell 의 값 (`Input` / `Output` / `-`)
    /// 카운트하여 IW / QW 표기로 변환.
    let private countDirectionDistribution
        (lines: string array)
        (sectionStartIdx: int)
        (sectionTotalRows: int) : (int * int * int) =
        // Tag = cell 2 (0-based: Word=0, Direction=1, Tag=2, DataType=3, Address=4, Symbol=5).
        // Direction cell index = 1.
        let mutable iw = 0  // Input
        let mutable qw = 0  // Output
        let mutable other = 0
        let mutable i = sectionStartIdx + 1
        let mutable cnt = 0
        while cnt < sectionTotalRows && i < lines.Length do
            let line = lines.[i]
            if isTableRow line && not (isAlignmentRow line) then
                let cells = splitCells line
                if cells.Length >= 2 then
                    let dir = cells.[1].Trim()
                    if dir.Equals("Input", StringComparison.OrdinalIgnoreCase) then iw <- iw + 1
                    elif dir.Equals("Output", StringComparison.OrdinalIgnoreCase) then qw <- qw + 1
                    else other <- other + 1
                cnt <- cnt + 1
            i <- i + 1
        (iw, qw, other)

    /// IoList 시트 한 section 의 unique device base token 별 첫 등장 row index (section 안 0-based)
    /// 집합 계산. Tag cell (index 2) 에서 base token 추출 → 첫 등장 idx 만 keep.
    let private collectDeviceSampleIndices
        (lines: string array)
        (sectionStartIdx: int)
        (sectionTotalRows: int) : Set<int> =
        let seen = System.Collections.Generic.HashSet<string>()
        let result = System.Collections.Generic.HashSet<int>()
        let mutable i = sectionStartIdx + 1
        let mutable rowIdx = 0
        while rowIdx < sectionTotalRows && i < lines.Length do
            let line = lines.[i]
            if isTableRow line && not (isAlignmentRow line) then
                let cells = splitCells line
                let tagCell =
                    if cells.Length >= 3 then cells.[2] else ""
                let baseToken = reshapeTagToDeviceBase tagCell
                let key = if String.IsNullOrEmpty baseToken then tagCell.Trim() else baseToken
                if not (String.IsNullOrEmpty key) && seen.Add key then
                    result.Add rowIdx |> ignore
                rowIdx <- rowIdx + 1
            i <- i + 1
        Set.ofSeq result

    /// IoList 전용 Stage 2 — device-aware sampling. 시트별 head/tail + unique device sample 합집합 keep.
    let private applyIoListSampling
        (headCount: int)
        (tailCount: int)
        (markdown: string) : string =
        if headCount < 0 || tailCount < 0 then markdown
        else
            let trailing = hasTrailingNewline markdown
            let lines = splitLines markdown
            let result = ResizeArray<string>()
            let mutable i = 0
            let mutable inDataSection = false
            let mutable prevWasTableRow = false
            let mutable sectionCellCount = 0
            let mutable sectionTotalRows = 0
            let mutable sectionDataIdx = 0
            let mutable sectionDropped = 0
            let mutable sectionKept = 0
            let mutable sectionDeviceSet : Set<int> = Set.empty
            let mutable sectionDeviceCount = 0
            // Phase 5 Major-2 fix — section 진입 시 미리 계산: device sample row 중 head/tail 범위 밖의 row 수.
            let mutable sectionDeviceOnly = 0
            // Phase 5 Major-1 fix — section 진입 시 미리 계산: 예상 drop 수 = totalRows - kept.
            // kept = unique row indices in (head ∪ tail ∪ deviceSet). elide marker row 의 정확 elided 수치 박제.
            let mutable sectionExpectedDropped = 0
            let mutable sectionIw = 0
            let mutable sectionQw = 0
            let mutable sectionOther = 0
            let mutable elideEmitted = false

            let flushSampleNote () =
                if sectionDropped > 0 then
                    let headKept = min headCount sectionTotalRows
                    let tailKept = min tailCount (max 0 (sectionTotalRows - headKept))
                    // **Phase 5 Major-2 fix** — device-only = head/tail 범위 밖의 device sample row 수.
                    // 종전: `sectionKept - headKept - tailKept` → head/tail 와 device set 의 overlap
                    // (e.g. head 의 row 가 device 첫 등장 row 이기도 한 경우) 발생 시 deviceOnly 가 음수
                    // 또는 부정확. 본 fix: section 진입 시 미리 계산한 정확 값 (`sectionDeviceOnly`) 박제.
                    result.Add (
                        sprintf "<!-- sampled (IoList): %d kept (head %d + tail %d + devices %d of %d unique), %d row(s) elided. directions: IW=%d, QW=%d, other=%d -->"
                            sectionKept headKept tailKept sectionDeviceOnly sectionDeviceCount sectionDropped
                            sectionIw sectionQw sectionOther)
                sectionDropped <- 0
                sectionKept <- 0
                sectionDataIdx <- 0
                sectionTotalRows <- 0
                sectionCellCount <- 0
                sectionDeviceSet <- Set.empty
                sectionDeviceCount <- 0
                sectionDeviceOnly <- 0
                sectionExpectedDropped <- 0
                sectionIw <- 0
                sectionQw <- 0
                sectionOther <- 0
                elideEmitted <- false

            let buildElideRow (cellCnt: int) (elided: int) : string =
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
                    inDataSection <- true
                    prevWasTableRow <- true
                    sectionCellCount <- alignmentCellCount line
                    sectionTotalRows <- countSectionDataRows lines i
                    sectionDataIdx <- 0
                    sectionDropped <- 0
                    sectionKept <- 0
                    elideEmitted <- false
                    // device sample indices + Direction 분포 사전 계산 (시트당 한 번).
                    sectionDeviceSet <- collectDeviceSampleIndices lines i sectionTotalRows
                    sectionDeviceCount <- sectionDeviceSet.Count
                    let iw, qw, other = countDirectionDistribution lines i sectionTotalRows
                    sectionIw <- iw
                    sectionQw <- qw
                    sectionOther <- other
                    // **Phase 5 Major-1/2 fix** — keep / drop 사전 계산.
                    // keepSet = (0 .. headCount-1) ∪ (totalRows-tailCount .. totalRows-1) ∪ deviceSet.
                    // expectedDropped = totalRows - |keepSet|. deviceOnly = |deviceSet \ head \ tail|.
                    let headHi = min headCount sectionTotalRows
                    let tailLo = max headHi (sectionTotalRows - tailCount)
                    let keepSet = System.Collections.Generic.HashSet<int>()
                    for k in 0 .. headHi - 1 do keepSet.Add k |> ignore
                    for k in tailLo .. sectionTotalRows - 1 do keepSet.Add k |> ignore
                    let mutable deviceOnlyAcc = 0
                    for idx in sectionDeviceSet do
                        let inHead = idx < headHi
                        let inTail = idx >= tailLo
                        if not inHead && not inTail then
                            deviceOnlyAcc <- deviceOnlyAcc + 1
                        keepSet.Add idx |> ignore
                    sectionDeviceOnly <- deviceOnlyAcc
                    sectionExpectedDropped <- max 0 (sectionTotalRows - keepSet.Count)
                    result.Add line
                elif isTableRow line then
                    if inDataSection then
                        let keepHead = sectionDataIdx < headCount
                        let keepTail = sectionDataIdx >= sectionTotalRows - tailCount
                        let keepDevice = sectionDeviceSet.Contains sectionDataIdx
                        let keep = keepHead || keepTail || keepDevice
                        if keep then
                            result.Add line
                            sectionKept <- sectionKept + 1
                        else
                            // drop. 첫 drop 발생 위치에 elide marker row 1회 박제.
                            if not elideEmitted then
                                // **Phase 5 Major-1 fix** — elide marker row 에 정확한 expected drop 수
                                // 박제 (종전 placeholder 0). sample note 의 `M elided` 와 일치.
                                result.Add (buildElideRow sectionCellCount sectionExpectedDropped)
                                elideEmitted <- true
                            sectionDropped <- sectionDropped + 1
                        sectionDataIdx <- sectionDataIdx + 1
                        prevWasTableRow <- true
                    else
                        result.Add line
                        prevWasTableRow <- true
                else
                    if prevWasTableRow && isSectionBoundary line then
                        flushSampleNote ()
                        inDataSection <- false
                    prevWasTableRow <- false
                    result.Add line
                i <- i + 1

            flushSampleNote ()
            // **Phase 5 Major-1 fix** — section 진입 시 사전 계산한 `sectionExpectedDropped` 를
            // elide marker row 박제 시점에 직접 박제 (종전 placeholder `0` → 실제 drop 수).
            // marker row 와 sample note 의 row count 가 동일 — 표 무결성 + 정확 수치 박제 정합.
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
                    // **B·M3 (Outlier/Minor 묶음 1)** — split note 위치를 머리말 6행 다음 + H1 이전
                    // 으로 보정 (2026-05-27 canary 1행 patch 후 baseline). 종전 구현은 part markdown 의
                    // 가장 앞 (머리말 1행 보다 앞) 에 박제 → 머리말 첫 행이 `<!-- canary: ... -->` (line 1)
                    // / `<!-- generated by ... -->` (line 2) SSOT 인데 split note 가 그 앞에 박제되면 외부
                    // reader (LLM / 진단 도구) 가 6행 머리말 박제 invariant 를 못 인식. 본 fix 는 partLines
                    // 안에서 첫 blank line (= 머리말 6행 직후 trailing blank) 위치를 찾아 그 다음에 noteLine
                    // 삽입 — pattern 기반이라 canary 1행 추가 자동 흡수. blank line 부재 시 (fixture 비정상)
                    // fallback 으로 맨 앞 박제.
                    let noteLine =
                        sprintf "<!-- split: part %d of %d (data rows %d~%d of %d) -->"
                            (partIdx + 1) partCount (startRow + 1) endRow totalDataRows
                    let arr = partLines.ToArray()
                    let insertIdx =
                        // 머리말 6행 직후 첫 blank line index 검색 → 그 다음 위치 (idx + 1) 에 insert.
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

    /// **Phase 4 dispatch (`todo-lighthouse-iolist-v2.md` Phase 4)** — strategy 인지 진입점.
    /// `applyCap` 의 wrapper — Stage 2 sampling 분기를 strategy 별 dispatch.
    ///
    /// **dispatch policy**:
    ///   - `strategyName = "IoListStrategy"` → Stage 2 = `applyIoListSampling` (device-aware,
    ///     head 10 + tail 10 + unique device sample + Direction 분포 박제).
    ///   - 그 외 (WorkOrder / PdfControlSpec / 빈 문자열) → Stage 2 = `applySampling` (head 5 +
    ///     tail 5, byte-equal 회귀 가드).
    ///
    /// **byte-equal 회귀 가드**: `applyCap markdown` 호출은 본 wrapper 의 default 분기와 동일 결과
    /// 반환 (기존 caller 변경 0). caller 가 strategy 인지 시점에 `applyCapFor` 명시 호출.
    let rec applyCapFor (strategyName: string) (markdown: string) : CapResult =
        let isIoList =
            not (String.IsNullOrEmpty strategyName)
            && strategyName.Equals(IoListStrategyName, StringComparison.Ordinal)
        let originalSize = byteSize markdown
        if originalSize <= MaxMarkdownBytes then
            { Markdown = markdown
              Stage = Original
              SizeBytes = originalSize
              SplitParts = None }
        else
            Log.lighthouse.Debug(
                sprintf "MarkdownCapPolicy: cap exceeded (size=%d > cap=%d, strategy=%s), Stage 1 진입"
                    originalSize MaxMarkdownBytes
                    (if String.IsNullOrEmpty strategyName then "(default)" else strategyName))
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
                Log.lighthouse.Debug(
                    sprintf "MarkdownCapPolicy: Stage 1 미흡수 size=%d, Stage 2 진입 (strategy=%s)"
                        stage1Size
                        (if isIoList then "IoList(device-aware)" else "default(head/tail)"))
                // Phase 4 — IoList 인 경우 device-aware sampling.
                let stage2, stage2HeadCount, stage2TailCount =
                    if isIoList then
                        applyIoListSampling IoListSampleHeadCount IoListSampleTailCount stage1,
                        IoListSampleHeadCount, IoListSampleTailCount
                    else
                        applySampling SampleHeadCount SampleTailCount stage1,
                        SampleHeadCount, SampleTailCount
                let stage2Size = byteSize stage2
                if stage2Size <= MaxMarkdownBytes then
                    Log.lighthouse.Debug(
                        sprintf "MarkdownCapPolicy: Stage 2 (Sampled head=%d, tail=%d) 흡수 size=%d"
                            stage2HeadCount stage2TailCount stage2Size)
                    { Markdown = stage2
                      Stage = Sampled (stage2HeadCount, stage2TailCount)
                      SizeBytes = stage2Size
                      SplitParts = None }
                else
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
                            partCount <- partCount * 2
                    if not splitDone then
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

    /// cap 정책 진입점. `markdown` 의 byte size 가 `MaxMarkdownBytes` 이하면 Original 반환.
    /// 초과 시 Stage 1 → 2 → 3 순으로 escalation. 모든 단계가 cap 안 흡수 가능하게 시도.
    ///
    /// **byte-equal 회귀 가드**: 본 진입점은 strategy 무관 default 분기 — `applyCapFor ""` 와 동일
    /// 결과. Phase 4 (IoList sampling) 추가 후에도 기존 caller (SpecializedDigestBuilder /
    /// MarkdownCapPolicyTests) 의 출력은 byte-equal 유지.
    and applyCap (markdown: string) : CapResult =
        // **Phase 4 refactor (CLAUDE.md "3줄 이상 반복 패턴 → refactoring")** — `applyCap` 본문 80여
        // 줄이 `applyCapFor` default 분기 (isIoList=false) 와 동일 → `applyCapFor ""` 위임으로 중복
        // 제거. byte-equal 회귀 가드 동등 (`isIoList=false` 분기에서 default sampling 사용).
        // F·m1 (Outlier/Minor 묶음 1) escalation 진단 logger 박제 유지 — `applyCapFor` 의 logger 가
        // strategy 인지 (`strategy=(default)` 표기) 로 박제, 종전 logger format 과 유사 정합.
        applyCapFor "" markdown
