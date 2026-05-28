module Ds2.LightHouse.Tests.IoListStrategyTests

open System
open System.IO
open System.Text
open System.Threading
open System.Text.RegularExpressions
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

/// **PR-I (todo-lighthouse-iolist-v2.md Phase 5)** — IoListStrategy v2 회귀 가드.
///
/// 본 신규 test 묶음은 Phase 1 (22-col 6-col block fix) + Phase 3 (27-col RB 자동 흡수) +
/// Phase 4 (cap policy device-aware sampling) + Phase 5 (검열 Major fix) 의 회귀 가드.
///
/// 검증 시나리오:
///   1. 22-col 6-col block reshape (Phase 1 회귀 가드 — Direction Input/Output 박제).
///   2. 27-col RB 자동 흡수 (Phase 3 회귀 가드 — 5 block 처리).
///   3. device base token 추출 edge case (Phase 4 reshapeTagToDeviceBase indirect verify
///      via cap policy device-aware sampling 결과 박제).
///   4. IoList cap policy device-aware sampling (Phase 4/5 — head/tail + device-only + Direction 분포).
///   5. default cap policy byte-equal 회귀 가드 (Phase 4 의 wrapper refactor 후에도 default 분기 byte-equal).
///   6. 51-col COVER / 1-col Sheet1 signature 미매치 (정상 skip).

// ── helpers (xlsx fixture 생성) ──────────────────────────────────────────

let private withTempPath (ext: string) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-iolist-v2-%s%s" (Guid.NewGuid().ToString("N")) ext)
    try action path
    finally if File.Exists path then File.Delete path

let private mkCell (cellRef: string) (text: string) : Cell =
    let cell = Cell()
    cell.CellReference <- StringValue(cellRef)
    cell.DataType <- EnumValue(CellValues.InlineString)
    let inl = InlineString()
    inl.AppendChild(Text(text)) |> ignore
    cell.AppendChild(inl) |> ignore
    cell

let private colLetter (idx: int) : string =
    let sb = System.Text.StringBuilder()
    let mutable n = idx
    while n > 0 do
        let r = (n - 1) % 26
        sb.Insert(0, char (int 'A' + r)) |> ignore
        n <- (n - 1) / 26
    sb.ToString()

let private mkRow (rowIdx: uint) (cells: (int * string) list) : Row =
    let row = Row()
    row.RowIndex <- UInt32Value(rowIdx)
    for (colIdx, value) in cells do
        let cellRef = sprintf "%s%d" (colLetter colIdx) (int rowIdx)
        row.AppendChild(mkCell cellRef value) |> ignore
    row

let private extractDocument (path: string) : ExtractedDocument =
    use ext = new OoxmlExtractor() :> IExtractor
    ext.Extract(path, CancellationToken.None)

// ── 27-col RB 변형 fixture (5-block × 6-col + leading/trailing) ──────────

/// 27-col RB layout fixture — Phase 3 의 5-block 자동 흡수 회귀 가드.
///   - 시트 5개 (S201_RB1 ~ S205_RB1) + COVER
///   - R5 헤더 / R6~R12 데이터 — 7 rows × 5 block = 35 row (long-form).
let private make27ColRbFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    let sheetNames = [
        "COVER"; "S201_RB1_3-01"; "S202_RB1_3-15"; "S203_RB1_3-25"; "S204_RB1_3-30"; "S205_RB1_3-37"
    ]
    let mutable sheetId = 1u
    for name in sheetNames do
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        let sheetData = SheetData()
        if name = "COVER" then
            sheetData.AppendChild(mkRow 1u [ 1, "COVER" ]) |> ignore
        else
            // R1 — "I/O LIST" 키워드 (col 2 박제, leading col 1 빈 정합).
            sheetData.AppendChild(mkRow 1u [ 2, "PLC : LS XGT"; 6, "I/O LIST" ]) |> ignore
            sheetData.AppendChild(mkRow 2u [ 2, "Project: 광명2_RB"; 6, sprintf "Part: %s" name ]) |> ignore
            // R5 — 5 block × 5 col (col 2~26) + col 27 trailing 빈.
            let headerCells = [
                2, "Word"; 3, "Tag"; 4, "Type"; 5, "Addr"; 6, "Sym"
                7, "Word"; 8, "Tag"; 9, "Type"; 10, "Addr"; 11, "Sym"
                12, "Word"; 13, "Tag"; 14, "Type"; 15, "Addr"; 16, "Sym"
                17, "Word"; 18, "Tag"; 19, "Type"; 20, "Addr"; 21, "Sym"
                22, "Word"; 23, "Tag"; 24, "Type"; 25, "Addr"; 26, "Sym"
                27, ""
            ]
            sheetData.AppendChild(mkRow 5u headerCells) |> ignore
            for r in 6u .. 12u do
                let i = int r - 5
                let dataCells = [
                    2, "3010"; 3, sprintf "%s_HOME%d" name i; 4, "BOOL"; 5, sprintf "%%IW3010.%d" i; 6, "RB1"
                    7, "3010"; 8, sprintf "%s_WK%d" name i; 9, "BOOL"; 10, sprintf "%%IW3010.%d" (i+8); 11, "RB1"
                    12, "5410"; 13, sprintf "%s_CMD%d" name i; 14, "BOOL"; 15, sprintf "%%QW5410.%d" i; 16, "RB1"
                    17, "5410"; 18, sprintf "%s_ACT%d" name i; 19, "BOOL"; 20, sprintf "%%QW5410.%d" (i+8); 21, "RB1"
                    22, "5410"; 23, sprintf "%s_END%d" name i; 24, "BOOL"; 25, sprintf "%%QW5410.%d" (i+16); 26, "RB1"
                    27, ""
                ]
                sheetData.AppendChild(mkRow r dataCells) |> ignore
        let ws = Worksheet()
        ws.Append(sheetData :> OpenXmlElement) |> ignore
        wsPart.Worksheet <- ws
        wsPart.Worksheet.Save()
        let sheet = Sheet()
        sheet.Id <- StringValue(wbPart.GetIdOfPart wsPart)
        sheet.SheetId <- UInt32Value(sheetId)
        sheet.Name <- StringValue(name)
        sheets.AppendChild(sheet) |> ignore
        sheetId <- sheetId + 1u
    wbPart.Workbook.AppendChild(sheets) |> ignore
    wbPart.Workbook.Save()

// ── 51-col COVER 단일 / 1-col Sheet1 단일 fixture ────────────────────────

/// COVER 51-col / Sheet1 1-col 시트만 박제 — signature 미매치 회귀 가드 (정상 skip).
let private makeNonMatchingFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    // COVER — 51 col 빈 row.
    let wsCover = wbPart.AddNewPart<WorksheetPart>()
    let coverData = SheetData()
    let coverHeader = [ for c in 1 .. 51 -> c, sprintf "C%d" c ]
    coverData.AppendChild(mkRow 1u coverHeader) |> ignore
    let coverWs = Worksheet()
    coverWs.Append(coverData :> OpenXmlElement) |> ignore
    wsCover.Worksheet <- coverWs
    wsCover.Worksheet.Save()
    let cover = Sheet()
    cover.Id <- StringValue(wbPart.GetIdOfPart wsCover)
    cover.SheetId <- UInt32Value(1u)
    cover.Name <- StringValue("COVER")
    sheets.AppendChild(cover) |> ignore
    // Sheet1 — 1 col.
    let wsBlank = wbPart.AddNewPart<WorksheetPart>()
    let blankData = SheetData()
    blankData.AppendChild(mkRow 1u [ 1, "" ]) |> ignore
    let blankWs = Worksheet()
    blankWs.Append(blankData :> OpenXmlElement) |> ignore
    wsBlank.Worksheet <- blankWs
    wsBlank.Worksheet.Save()
    let blank = Sheet()
    blank.Id <- StringValue(wbPart.GetIdOfPart wsBlank)
    blank.SheetId <- UInt32Value(2u)
    blank.Name <- StringValue("Sheet1")
    sheets.AppendChild(blank) |> ignore
    wbPart.Workbook.AppendChild(sheets) |> ignore
    wbPart.Workbook.Save()

// ── 5.2-1. 22-col 6-col block reshape 회귀 (Phase 1) ────────────────────

[<Fact>]
let ``Phase 1 회귀 — 22-col 6-col block reshape: Direction Input/Output 비빈 박제`` () =
    // 본 test 는 XlsxStrategiesTests 의 makeIoListFixture (22-col layout) 와 동일 정합.
    // Phase 1 의 reshapeRowToBlocks 가 `i=1` 시작 + `blockSize=5` 로 4 block 흡수 시
    // Direction = Input/Output 박제율 = (전체 data row) × 50% (각 시트 8 row × 2 Input + 2 Output).
    withTempPath ".xlsx" (fun path ->
        // 인라인 22-col fixture (XlsxStrategiesTests 와 정합 — col 1 = leading 빈, col 22 = trailing 빈)
        use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
        let wbPart = doc.AddWorkbookPart()
        wbPart.Workbook <- Workbook()
        let sheets = Sheets()
        let mutable sheetId = 1u
        for name in [ "S201_RB1"; "S202_ARRANGE"; "S203_PNL"; "S204_KEY"; "S205_RESPOT" ] do
            let wsPart = wbPart.AddNewPart<WorksheetPart>()
            let sd = SheetData()
            sd.AppendChild(mkRow 1u [ 2, "PLC : LS XGT"; 6, "I/O LIST" ]) |> ignore
            sd.AppendChild(mkRow 2u [ 2, "Project: 광명2"; 6, sprintf "Part: %s" name ]) |> ignore
            // R5 4-block 헤더 + col 22 trailing 빈.
            let hdr = [
                2, "Word"; 3, "Tag"; 4, "Type"; 5, "Addr"; 6, "Sym"
                7, "Word"; 8, "Tag"; 9, "Type"; 10, "Addr"; 11, "Sym"
                12, "Word"; 13, "Tag"; 14, "Type"; 15, "Addr"; 16, "Sym"
                17, "Word"; 18, "Tag"; 19, "Type"; 20, "Addr"; 21, "Sym"
                22, ""
            ]
            sd.AppendChild(mkRow 5u hdr) |> ignore
            for r in 6u .. 8u do
                let i = int r - 5
                let dataCells = [
                    2, "1000"; 3, sprintf "%s_IN_A%d" name i; 4, "BOOL"; 5, sprintf "%%IW1000.%d" i; 6, "S1"
                    7, "1000"; 8, sprintf "%s_IN_B%d" name i; 9, "BOOL"; 10, sprintf "%%IW1000.%d" (i+10); 11, "S1"
                    12, "2000"; 13, sprintf "%s_OUT_A%d" name i; 14, "BOOL"; 15, sprintf "%%QW2000.%d" i; 16, "S2"
                    17, "2000"; 18, sprintf "%s_OUT_B%d" name i; 19, "BOOL"; 20, sprintf "%%QW2000.%d" (i+10); 21, "S2"
                    22, ""
                ]
                sd.AppendChild(mkRow r dataCells) |> ignore
            let ws = Worksheet()
            ws.Append(sd :> OpenXmlElement) |> ignore
            wsPart.Worksheet <- ws
            wsPart.Worksheet.Save()
            let sh = Sheet()
            sh.Id <- StringValue(wbPart.GetIdOfPart wsPart)
            sh.SheetId <- UInt32Value(sheetId)
            sh.Name <- StringValue(name)
            sheets.AppendChild(sh) |> ignore
            sheetId <- sheetId + 1u
        wbPart.Workbook.AppendChild(sheets) |> ignore
        wbPart.Workbook.Save()
        doc.Dispose()
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        let sigR = strategy.Signature extracted
        Assert.True(sigR.Matched, sprintf "22-col fixture signature 미매치 — detail=%s" sigR.Detail)
        match strategy.Build (path, extracted, sigR) with
        | Rejected entry -> Assert.Fail(sprintf "Build Rejected — %s" entry.Reason)
        | Built markdown ->
            // Input/Output 박제율 — 모든 data row 가 Input/Output 둘 중 하나.
            let inputCnt = Regex.Matches(markdown, @"\| Input \|").Count
            let outputCnt = Regex.Matches(markdown, @"\| Output \|").Count
            Assert.True(inputCnt > 0, sprintf "Input 박제 0건 — Direction reshape 결함")
            Assert.True(outputCnt > 0, sprintf "Output 박제 0건 — Direction reshape 결함")
            // 시트당 (3 row × 2 Input + 2 Output) × 5 시트 = 30 Input + 30 Output.
            Assert.Equal(30, inputCnt)
            Assert.Equal(30, outputCnt))

// ── 5.2-2. 27-col RB 자동 흡수 (Phase 3) ──────────────────────────────────

[<Fact>]
let ``Phase 3 회귀 — 27-col RB 5-block 자동 흡수: 시트당 5 block × N row 박제`` () =
    withTempPath ".xlsx" (fun path ->
        make27ColRbFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        let sigR = strategy.Signature extracted
        Assert.True(sigR.Matched, sprintf "27-col RB fixture signature 미매치 — detail=%s" sigR.Detail)
        match strategy.Build (path, extracted, sigR) with
        | Rejected entry -> Assert.Fail(sprintf "Build Rejected — %s" entry.Reason)
        | Built markdown ->
            // 시트당 7 row × 5 block = 35 long-form row.
            // 5 시트 (S201_RB1 ~ S205_RB1) × 35 = 175 data row.
            // Input (block 1+2 = %IW) = 시트당 14 = 70.
            // Output (block 3+4+5 = %QW) = 시트당 21 = 105.
            let inputCnt = Regex.Matches(markdown, @"\| Input \|").Count
            let outputCnt = Regex.Matches(markdown, @"\| Output \|").Count
            Assert.Equal(70, inputCnt)
            Assert.Equal(105, outputCnt)
            // 27-col 의 5 block × 7 rows × 5 시트 = 175 row 박제. H2 5개.
            Assert.Contains("## S201_RB1_3-01", markdown)
            Assert.Contains("## S205_RB1_3-37", markdown))

// ── 5.2-3. device base token edge case (indirect via markdown) ────────────

[<Fact>]
let ``Phase 4 회귀 — reshapeTagToDeviceBase edge case: '_' 부재 / 단일 / multi-suffix`` () =
    // private reshapeTagToDeviceBase 직접 접근 불가 → applyCapFor IoListStrategy 진입 후 device-aware
    // sampling 결과 (note `devices N of M unique`) 의 unique device 수로 indirect 검증.
    //
    // Tag edge case:
    //   - "" (빈 값) → key 빈 → device set skip.
    //   - "TAG" ('_' 부재) → reshapeTagToDeviceBase = "TAG" → 자체로 device key.
    //   - "DEV_X" ('_' 1개) → reshapeTagToDeviceBase = "DEV" → "DEV" device.
    //   - "DEV_X_ADV_COMP" (multi-suffix) → reshapeTagToDeviceBase = "DEV_X_ADV" (마지막 `_` 만 제거).
    // 본 test 는 cap 미초과 시 Original 반환이라 sample note 박제 0 → cap 초과 fixture 필요 →
    // 본 test 는 cap 초과 large markdown 으로 검증.
    let sb = StringBuilder()
    sb.AppendLine("# IO List — edge") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("## EdgeSheet") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("| Word | Direction | Tag | DataType | Address | Symbol |") |> ignore
    sb.AppendLine("|:---|:---:|:---|:---|:---|:---|") |> ignore
    // cap 초과 의도 — 6000 row × 80byte = ~480KB > 256KB cap.
    // device base token 변종 5종 반복 → unique device 5
    // (NOTOKEN / DEV / DEV_X_ADV / WRS_CLAMP / PNL_ROBOT).
    for r in 1 .. 6000 do
        let baseIdx = r % 5
        let tag =
            match baseIdx with
            | 0 -> "NOTOKEN"                       // '_' 부재
            | 1 -> sprintf "DEV_%d" r              // '_' 1개 → "DEV"
            | 2 -> sprintf "DEV_X_ADV_%d" r        // multi-suffix → "DEV_X_ADV"
            | 3 -> sprintf "WRS_CLAMP_%d" r        // → "WRS_CLAMP"
            | _ -> sprintf "PNL_ROBOT_%d" r        // → "PNL_ROBOT"
        sb.AppendLine(
            sprintf "| 1000 | Input | %s | BOOL | %%%%IW1000.%d | S |"
                tag (r % 16)) |> ignore
    let markdown = sb.ToString()
    let result = MarkdownCapPolicy.applyCapFor "IoListStrategy" markdown
    // cap 초과 → Stage 2 진입 — sample note 박제.
    match result.Stage with
    | MarkdownCapPolicy.Sampled _ ->
        Assert.Matches(Regex(@"<!-- sampled \(IoList\):.*devices \d+ of (\d+) unique"), result.Markdown)
        // unique device 수 = 5 (NOTOKEN / DEV / DEV_X_ADV / WRS_CLAMP / PNL_ROBOT).
        let m = Regex.Match(result.Markdown, @"devices \d+ of (\d+) unique")
        Assert.True(m.Success, "device note 박제 누락")
        let uniqueCnt = Int32.Parse(m.Groups.[1].Value)
        Assert.Equal(5, uniqueCnt)
    | other -> Assert.Fail(sprintf "Stage 2 (Sampled) 기대했으나 %A — size=%d" other result.SizeBytes)

// ── 5.2-4. IoList cap policy device-aware sampling ────────────────────────

[<Fact>]
let ``Phase 4/5 회귀 — IoList cap policy device-aware sampling: head/tail + device + Direction 분포 + 정확 elidedCnt`` () =
    // 본 test 는 applyCapFor "IoListStrategy" 의 sampling 분기 verify.
    //   - head 10 + tail 10 + unique device sample row.
    //   - Direction 분포 (IW=N, QW=N, other=N) 박제.
    //   - **Phase 5 Major-1 fix**: elide marker row 의 `(N rows elided)` 가 정확 수치 박제
    //     (종전 placeholder 0).
    //   - **Phase 5 Major-2 fix**: deviceOnly = head/tail 범위 밖 device set 의 row 수.
    let sb = StringBuilder()
    sb.AppendLine("# IO List — fixture") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("## S204_TEST") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("| Word | Direction | Tag | DataType | Address | Symbol |") |> ignore
    sb.AppendLine("|:---|:---:|:---|:---|:---|:---|") |> ignore
    // 6000 row → 480KB 초과. Tag base token 다양화 — `DEV_<r>` → unique base 1 (`DEV`).
    // Direction: row의 idx 짝수 = Input, 홀수 = Output → IW=3000, QW=3000.
    for r in 1 .. 6000 do
        let dir = if r % 2 = 0 then "Input" else "Output"
        let addrPrefix = if dir = "Input" then "%%IW" else "%%QW"
        sb.AppendLine(
            sprintf "| 1000 | %s | DEV_%d | BOOL | %s1000.%d | S |"
                dir r addrPrefix (r % 16)) |> ignore
    let markdown = sb.ToString()
    let result = MarkdownCapPolicy.applyCapFor "IoListStrategy" markdown
    // Stage 2 (Sampled) 또는 Stage 3 (Split) 진입.
    match result.Stage with
    | MarkdownCapPolicy.Sampled (h, t) ->
        Assert.Equal(MarkdownCapPolicy.IoListSampleHeadCount, h)
        Assert.Equal(MarkdownCapPolicy.IoListSampleTailCount, t)
        // sample note 박제 검증.
        let note =
            Regex.Match(result.Markdown,
                @"<!-- sampled \(IoList\): (\d+) kept \(head (\d+) \+ tail (\d+) \+ devices (\d+) of (\d+) unique\), (\d+) row\(s\) elided\. directions: IW=(\d+), QW=(\d+), other=(\d+) -->")
        Assert.True(note.Success, "IoList sample note 박제 누락")
        let kept = Int32.Parse note.Groups.[1].Value
        let headKept = Int32.Parse note.Groups.[2].Value
        let tailKept = Int32.Parse note.Groups.[3].Value
        let elided = Int32.Parse note.Groups.[6].Value
        let iw = Int32.Parse note.Groups.[7].Value
        let qw = Int32.Parse note.Groups.[8].Value
        // head 10 / tail 10 / kept = head + tail + deviceOnly. total = 6000.
        Assert.Equal(10, headKept)
        Assert.Equal(10, tailKept)
        Assert.Equal(6000, kept + elided)
        Assert.Equal(3000, iw)
        Assert.Equal(3000, qw)
        // **Phase 5 Major-1 fix** — elide marker row 의 `(N rows elided)` 박제 검증.
        let elideMarker =
            Regex.Match(result.Markdown, @"\| \(([0-9]+) rows elided\) \|")
        Assert.True(elideMarker.Success, "elide marker row 누락")
        let elidedInMarker = Int32.Parse elideMarker.Groups.[1].Value
        // elide marker 박제 수 = sample note 의 elided 수와 동일 (Phase 5 Major-1 fix).
        Assert.Equal(elided, elidedInMarker)
    | MarkdownCapPolicy.Split _ ->
        // Stage 3 진입 시 Stage 2 note 는 part 1 안 박제될 수 있음 — 본 test 는 Stage 2 흡수 의도.
        // Stage 3 박제 시 본 test 의 fixture size 조정 필요.
        Assert.Fail(
            sprintf "Stage 2 (Sampled) 기대했으나 Stage 3 (Split) 진입 — fixture 조정 필요. size=%d"
                result.SizeBytes)
    | other -> Assert.Fail(sprintf "Stage 2 기대했으나 %A — size=%d" other result.SizeBytes)

// ── 5.2-5. default cap policy byte-equal 회귀 가드 (Phase 4 wrapper) ──────

[<Fact>]
let ``Phase 4 회귀 — applyCapFor "" markdown == applyCap markdown (byte-equal default 분기)`` () =
    // Phase 4 의 `applyCap` 은 `applyCapFor ""` 위임 — refactor 후 byte-equal 회귀 가드.
    // (1) cap 미초과 small markdown.
    let smallMd = "# header\n\n| a | b |\n|---|---|\n| 1 | 2 |\n"
    let viaFor = MarkdownCapPolicy.applyCapFor "" smallMd
    let viaCap = MarkdownCapPolicy.applyCap smallMd
    Assert.Equal(viaCap.Markdown, viaFor.Markdown)
    Assert.Equal(viaCap.SizeBytes, viaFor.SizeBytes)
    // (2) cap 초과 — Stage 2 sampling 박제 byte-equal.
    let sb = StringBuilder()
    sb.AppendLine("# header") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("## sheet") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("| Word | Direction | Tag | DataType | Address | Symbol |") |> ignore
    sb.AppendLine("|:---|:---:|:---|:---|:---|:---|") |> ignore
    for r in 1 .. 6000 do
        sb.AppendLine(sprintf "| 1000 | Input | TAG_%d | BOOL | %%%%IW1000.%d | S |" r (r % 16)) |> ignore
    let bigMd = sb.ToString()
    let viaForBig = MarkdownCapPolicy.applyCapFor "" bigMd
    let viaCapBig = MarkdownCapPolicy.applyCap bigMd
    Assert.Equal(viaCapBig.Markdown, viaForBig.Markdown)
    Assert.Equal(viaCapBig.SizeBytes, viaForBig.SizeBytes)
    // default 분기는 일반 head/tail sampling — `<!-- sampled (IoList): ... -->` 박제 안 됨.
    Assert.DoesNotContain("sampled (IoList):", viaCapBig.Markdown)

// ── 5.2-6. 51-col COVER + 1-col Sheet1 signature 미매치 (정상 skip) ──────

[<Fact>]
let ``Phase 5 회귀 — 51-col COVER + 1-col Sheet1 만 박제된 xlsx 는 IoListStrategy 미매치 (정상 skip)`` () =
    withTempPath ".xlsx" (fun path ->
        makeNonMatchingFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        let sigR = strategy.Signature extracted
        Assert.False(sigR.Matched,
            sprintf "51-col COVER + 1-col Sheet1 만 박제된 xlsx 가 매치 — false-positive. detail=%s"
                sigR.Detail))
