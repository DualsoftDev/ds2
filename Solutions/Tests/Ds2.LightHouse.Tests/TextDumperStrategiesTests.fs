module Ds2.LightHouse.Tests.TextDumperStrategiesTests

open System
open System.IO
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// **PR-I3 (todo-documents-based-gfm.md §2 PR-I3 + §8.5.5)** — `TextDumper.dumpStrategySummaries` hook 회귀.
///
/// 검증 시나리오:
///   1. synthetic IoList + WorkOrder fixture (2 xlsx) → `summary/` 안 정확히 2개 markdown 박제.
///   2. signature 미매치 BOM fixture → `rejected.json` 또는 `near-miss.json` 박제 verify (점수 분기).
///   3. 빈 collection → summary/ 빈 디렉토리 + rejected/near-miss 파일 미생성.
///   4. 멱등 — 동일 collection 재호출 시 summary/ wipe 후 재생성, 잔재 파일 0.
///   5. (옵션) 자료 A/B/C 실파일 → 정확히 3개 markdown 박제 (`F:/Git/dualsoft/secrets/KBSamples/core/`).

// ── 공통 helpers ────────────────────────────────────────────────────────

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-td-strat-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new PdfExtractor() :> IExtractor
    new OoxmlExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]

let private disposeAll (ex: IExtractor list) =
    for e in ex do e.Dispose()

let private runDump (dir: string) : TextDumper.StrategyDumpSummary =
    let ex = extractors()
    try TextDumper.dumpStrategySummaries dir ex CancellationToken.None
    finally disposeAll ex

// ── synthetic xlsx fixture helpers (XlsxStrategiesTests.fs 와 동형, 본 모듈 한정) ──

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

/// 광명2 IO List signature 충족 xlsx (XlsxStrategiesTests.fs 의 makeIoListFixture 와 동형).
let private makeIoListFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    let sheetNames = [
        "COVER"; "S201_RB1"; "S202_ARRANGE_JIG"; "S203_WRS";
        "S204_KEY_JIG1"; "S204_WRS1"; "S205_RESPOT_JIG"; "S205_WRS"
    ]
    let mutable sheetId = 1u
    for name in sheetNames do
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        let sheetData = SheetData()
        if name = "COVER" then
            sheetData.AppendChild(mkRow 1u [ 1, "COVER" ]) |> ignore
        else
            sheetData.AppendChild(mkRow 1u [ 1, "PLC : LS XGT"; 5, "I/O LIST" ]) |> ignore
            sheetData.AppendChild(mkRow 2u [
                1, "Project: 광명2_SV_SIDE_OUTER"
                5, sprintf "Part: %s" name
            ]) |> ignore
            sheetData.AppendChild(mkRow 3u [ 1, "워드" ]) |> ignore
            sheetData.AppendChild(mkRow 4u [ 1, sprintf "Zone: %s" name ]) |> ignore
            let headerCells = [
                1, "Word";  2, "Tag";  3, "Type";  4, "Addr";  5, "Sym"
                6, "Word";  7, "Tag";  8, "Type";  9, "Addr"; 10, "Sym"
                11, "Word"; 12, "Tag"; 13, "Type"; 14, "Addr"; 15, "Sym"
                16, "Word"; 17, "Tag"; 18, "Type"; 19, "Addr"; 20, "Sym"
            ]
            sheetData.AppendChild(mkRow 5u headerCells) |> ignore
            for r in 6u .. 13u do
                let i = int r - 5
                let dataCells = [
                    1, "2010";  2, sprintf "%s_RB1_HOME_POS%d" name i;     3, "BOOL"; 4, sprintf "%%IW2010.%d" i;  5, "WRS01"
                    6, "2010";  7, sprintf "%s_RB1_WK_COMP%d" name i;      8, "BOOL"; 9, sprintf "%%IW2010.%d" (i+8); 10, "WRS01"
                    11, "5410"; 12, sprintf "%s_CLP%d_ADV1" name i;       13, "BOOL"; 14, sprintf "%%QW5410.%d" i; 15, "WRS02"
                    16, "5410"; 17, sprintf "%s_CLP%d_RET1" name i;       18, "BOOL"; 19, sprintf "%%QW5410.%d" (i+8); 20, "WRS02"
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

/// 광명2 조립작업서 signature 충족 xlsx (XlsxStrategiesTests.fs 의 makeWorkOrderFixture 와 동형).
let private makeWorkOrderFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    let sheetSpecs = [
        "COVER", true
        "5-1. #201", false
        "5-2. #202", false
        "5-3. #203", false
    ]
    let mutable sheetId = 1u
    for (name, isCover) in sheetSpecs do
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        let sheetData = SheetData()
        if isCover then
            sheetData.AppendChild(mkRow 1u [ 1, "COVER 조립작업서" ]) |> ignore
        else
            sheetData.AppendChild(mkRow 1u [
                1, "NO"; 2, "SYM"; 3, "작업내역"; 4, "시작"; 5, "시간"; 6, "누계"
            ]) |> ignore
            let mutable cumulative = 0
            for i in 1 .. 10 do
                let duration = 3
                cumulative <- cumulative + duration
                let symCode = sprintf "WRS%02d" i
                sheetData.AppendChild(mkRow (uint (i + 1)) [
                    1, sprintf "%d" i
                    2, symCode
                    3, sprintf "용접 작업 단계 %d 한국어 설명" i
                    4, sprintf "%d" (cumulative - duration)
                    5, sprintf "%d" duration
                    6, sprintf "%d" cumulative
                ]) |> ignore
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

/// **Backlog I (todo-documents-based-gfm.md §6.1 + documents-based-gfm.md §8.5.5)** —
/// MarkdownCapPolicy Stage 3 (Split) trigger 충족 대형 IoList fixture.
///   - 시트 200개 (zone code S201~S400) — IoListStrategy signature 매치 + Stage 3 진입 충분.
///   - 시트당 R6~R25 = 20 row × 4 block = 80 long-form rows. 200 시트 × 80 = 16000 long-form rows.
///   - 각 cell 의 symbol 컬럼에 긴 padding (200 char ascii) → Stage 1 column truncate (MaxCellChars=80) 적용.
///   - 실측 (FSI 검증, 90 시트 fixture): RAW ≈ 1.9MB → Stage 2 (sampling) 후 ≈ 145KB (90 × ~1.6KB/시트).
///   - 200 시트 외삽: Stage 2 후 200 × ~1.6KB ≈ 320KB → 256KB cap 초과 → Stage 3 split trigger.
///   - 200 시트 RAW ≈ 4.3MB → partCount ~17 → SplitMaxParts=32 안 (escalation 안정).
let private makeLargeIoListFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    // 200 시트 (Stage 3 trigger 충족 — 시트별 head 5 + tail 5 elide 후도 합산 cap 초과).
    let sheetNames =
        [ for i in 201 .. 400 -> sprintf "S%d_TEST" i ]
    let mutable sheetId = 1u
    let padding = String.replicate 200 "x"
    for name in sheetNames do
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        let sheetData = SheetData()
        sheetData.AppendChild(mkRow 1u [ 1, "PLC : LS XGT"; 5, "I/O LIST" ]) |> ignore
        sheetData.AppendChild(mkRow 2u [
            1, "Project: 광명2_STRESS_FIXTURE"
            5, sprintf "Part: %s" name
        ]) |> ignore
        sheetData.AppendChild(mkRow 3u [ 1, "워드" ]) |> ignore
        sheetData.AppendChild(mkRow 4u [ 1, sprintf "Zone: %s" name ]) |> ignore
        let headerCells = [
            1, "Word";  2, "Tag";  3, "Type";  4, "Addr";  5, "Sym"
            6, "Word";  7, "Tag";  8, "Type";  9, "Addr"; 10, "Sym"
            11, "Word"; 12, "Tag"; 13, "Type"; 14, "Addr"; 15, "Sym"
            16, "Word"; 17, "Tag"; 18, "Type"; 19, "Addr"; 20, "Sym"
        ]
        sheetData.AppendChild(mkRow 5u headerCells) |> ignore
        // 시트당 20 row × 4 block = 80 long-form rows. padding 짧게 — Stage 2 sampling 후 시트당 11 행
        // 으로 줄어들되 60 시트 합산이 cap 초과 → Stage 3 split trigger.
        for r in 6u .. 25u do
            let i = int r - 5
            let dataCells = [
                1, "2010";  2, sprintf "%s_TAG_%d_%s" name i padding; 3, "BOOL"; 4, sprintf "%%IW2010.%d" i;  5, "WRS01"
                6, "2010";  7, sprintf "%s_TAG_%d_B_%s" name i padding; 8, "BOOL"; 9, sprintf "%%IW2010.%d" (i+50); 10, "WRS01"
                11, "5410"; 12, sprintf "%s_TAG_%d_C_%s" name i padding; 13, "BOOL"; 14, sprintf "%%QW5410.%d" i; 15, "WRS02"
                16, "5410"; 17, sprintf "%s_TAG_%d_D_%s" name i padding; 18, "BOOL"; 19, sprintf "%%QW5410.%d" (i+50); 20, "WRS02"
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

/// signature 부분 매치 xlsx — IoList 의 zone 코드 시트 1개 + "I/O LIST" 키워드 박제 (score > 0 미달).
/// classifier 가 NearMiss 로 분류 (IoListStrategy 의 threshold 6 미달).
let private makePartialIoListFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    let wsPart = wbPart.AddNewPart<WorksheetPart>()
    let sheetData = SheetData()
    // R1 — "I/O LIST" 키워드 (IoList weight 2 / WorkOrder 무관).
    sheetData.AppendChild(mkRow 1u [ 1, "PLC : LS XGT"; 5, "I/O LIST" ]) |> ignore
    // 데이터 행 — %IW 토큰 10 개만 (50 미만 → IoList weight 0).
    for r in 2u .. 6u do
        sheetData.AppendChild(mkRow r [
            1, sprintf "%%IW100.%d" (int r)
            2, sprintf "%%IW101.%d" (int r)
        ]) |> ignore
    let ws = Worksheet()
    ws.Append(sheetData :> OpenXmlElement) |> ignore
    wsPart.Worksheet <- ws
    wsPart.Worksheet.Save()
    let sheet = Sheet()
    sheet.Id <- StringValue(wbPart.GetIdOfPart wsPart)
    sheet.SheetId <- UInt32Value(1u)
    sheet.Name <- StringValue("S201_RB1")   // zone code 시트 1개 → 5 개 미만 → weight 0
    sheets.AppendChild(sheet) |> ignore
    wbPart.Workbook.AppendChild(sheets) |> ignore
    wbPart.Workbook.Save()

/// signature 미매치 xlsx — 단순 BOM (zone code / IW/QW / Gantt header 모두 부재).
let private makeBomFixture (path: string) =
    use doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- Workbook()
    let sheets = Sheets()
    let wsPart = wbPart.AddNewPart<WorksheetPart>()
    let sheetData = SheetData()
    sheetData.AppendChild(mkRow 1u [ 1, "부품번호"; 2, "수량"; 3, "단가" ]) |> ignore
    for r in 2u .. 5u do
        sheetData.AppendChild(mkRow r [
            1, sprintf "P%03d" (int r)
            2, sprintf "%d" (int r * 10)
            3, "1000"
        ]) |> ignore
    let ws = Worksheet()
    ws.Append(sheetData :> OpenXmlElement) |> ignore
    wsPart.Worksheet <- ws
    wsPart.Worksheet.Save()
    let sheet = Sheet()
    sheet.Id <- StringValue(wbPart.GetIdOfPart wsPart)
    sheet.SheetId <- UInt32Value(1u)
    sheet.Name <- StringValue("BOM")
    sheets.AppendChild(sheet) |> ignore
    wbPart.Workbook.AppendChild(sheets) |> ignore
    wbPart.Workbook.Save()

// ── 시나리오 1: IoList + WorkOrder 2 xlsx → summary/ 안 정확히 2 파일 ───

[<Fact>]
let ``IoList + WorkOrder 2 xlsx → summary/ 안 IoList 1개만 박제 (2026-06-04 WorkOrder 일반 자료)`` () =
    // WorkOrderStrategy 제거 — WorkOrder xlsx 는 strategy 산출물 미생성 (일반 자료). IoList 만 박제.
    withTempDir (fun dir ->
        makeIoListFixture (Path.Combine(dir, "iolist.xlsx"))
        makeWorkOrderFixture (Path.Combine(dir, "workorder.xlsx"))
        // .lighthouse-kb/ 사전 생성 (Packager.resetKbDir 와 동형 — Indexer 미진입 path 시뮬).
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        Assert.Equal(1, summary.SummaryFiles.Length)
        // 디렉토리 안 .md 파일 정확히 1개 (IoList).
        let mdFiles = Directory.GetFiles(TextDumper.summaryDir dir, "*.md")
        Assert.Equal(1, mdFiles.Length)
        // IoList prefix 확인 + WorkOrder 미박제 확인.
        let names = mdFiles |> Array.map Path.GetFileName |> Array.sort
        Assert.Contains(names, fun (n: string) -> n.StartsWith("IoListStrategy-"))
        Assert.DoesNotContain(names, fun (n: string) -> n.StartsWith("WorkOrderStrategy-"))
        // 머리말 6행 (canary + 기존 5행, 2026-05-27 patch) + footer 박제 확인 (간단 spot-check).
        for path in mdFiles do
            let content = File.ReadAllText(path, System.Text.Encoding.UTF8)
            Assert.Contains("canary:", content)
            Assert.Contains("pong: summary/", content)
            Assert.Contains("generated by", content)
            Assert.Contains("<!-- footer -->", content)
            Assert.Contains("cross-ref-hash:", content))

// ── 시나리오 2-a: partial IoList signature → near-miss.json 박제 ────────────

[<Fact>]
let ``partial IoList signature → near-miss.json 박제 (점수 미달 분기 정상 진단)`` () =
    withTempDir (fun dir ->
        makePartialIoListFixture (Path.Combine(dir, "partial.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        Assert.Equal(0, summary.SummaryFiles.Length)
        // IoList weight 의 R1 "I/O LIST" 키워드 (weight 2) → score 2, threshold 6 → NearMiss.
        Assert.True(summary.NearMissCount > 0,
            sprintf "near-miss 박제 기대 — rejected=%d near-miss=%d"
                summary.RejectedCount summary.NearMissCount)
        Assert.True(File.Exists (TextDumper.nearMissJsonPath dir), "near-miss.json 박제 필요"))

// ── 시나리오 2-b: BOM signature 0 → 진단 파일 미생성 (Unmatched 분기) ──────

[<Fact>]
let ``BOM 신호 0 → Unmatched (rejected/near-miss 박제 0)`` () =
    withTempDir (fun dir ->
        makeBomFixture (Path.Combine(dir, "bom.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        Assert.Equal(0, summary.SummaryFiles.Length)
        // BOM 은 IoList score 0 → Unmatched → 진단 파일 미생성 (WorkOrder 는 2026-06-04 미등록).
        Assert.Equal(0, summary.NearMissCount)
        Assert.Equal(0, summary.RejectedCount)
        Assert.False(File.Exists (TextDumper.nearMissJsonPath dir))
        Assert.False(File.Exists (TextDumper.rejectedJsonPath dir)))

// ── 시나리오 3: 빈 collection → summary/ 빈 디렉토리 + 진단 파일 미생성 ──

[<Fact>]
let ``빈 collection → summary/ 빈 + rejected/near-miss 파일 미생성`` () =
    withTempDir (fun dir ->
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        Assert.Empty(summary.SummaryFiles)
        Assert.Equal(0, summary.RejectedCount)
        Assert.Equal(0, summary.NearMissCount)
        // summary/ 디렉토리는 생성, 안에 파일 0.
        Assert.True(Directory.Exists (TextDumper.summaryDir dir))
        Assert.Empty(Directory.GetFiles(TextDumper.summaryDir dir))
        // diagnostic JSON 파일 미생성 (entry 0 → write 0).
        Assert.False(File.Exists (TextDumper.rejectedJsonPath dir))
        Assert.False(File.Exists (TextDumper.nearMissJsonPath dir)))

// ── 시나리오 4: 멱등 — 재호출 시 summary/ wipe 후 재생성 ──────────────────

[<Fact>]
let ``재호출 멱등 — summary/ wipe 후 재생성, 잔재 0`` () =
    withTempDir (fun dir ->
        makeIoListFixture (Path.Combine(dir, "iolist.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let first = runDump dir
        Assert.Equal(1, first.SummaryFiles.Length)

        // 잔재 파일 인위 박제 — 재호출이 wipe 하는지 확인.
        let stalePath = Path.Combine(TextDumper.summaryDir dir, "stale-leftover.md")
        File.WriteAllText(stalePath, "stale", System.Text.UTF8Encoding(false))
        Assert.True(File.Exists stalePath)

        let second = runDump dir
        Assert.Equal(1, second.SummaryFiles.Length)
        // 잔재 파일 wipe 확인.
        Assert.False(File.Exists stalePath)
        // 재생성 파일은 동일 strategy + 동일 source → 동일 docId → 동일 filename.
        Assert.Equal(first.SummaryFiles.[0], second.SummaryFiles.[0]))

// ── 시나리오 6 (Backlog I): Stage 3 multi-part 박제 회귀 ──────────────────

[<Fact>]
let ``Stage 3 (Split) — multi-part 박제 회귀 (Backlog I) — {basename}.1.md / {basename}.2.md ... 다중 박제`` () =
    withTempDir (fun dir ->
        let xlsxPath = Path.Combine(dir, "large-iolist.xlsx")
        makeLargeIoListFixture xlsxPath
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        // 다중 part 박제 — 최소 2 파일 (Stage 3 trigger 시 SplitParts.Length ≥ 2).
        Assert.True(summary.SummaryFiles.Length >= 2,
            sprintf "Stage 3 multi-part 박제 기대 (≥ 2 파일) — 실제 %d 파일" summary.SummaryFiles.Length)

        // 파일명 패턴 — `IoListStrategy-{docId}-large-iolist.{N}.md`.
        let names = summary.SummaryFiles |> Array.map Path.GetFileName |> Array.sort
        let partPattern =
            System.Text.RegularExpressions.Regex(@"^IoListStrategy-[0-9a-f]{8}-large-iolist\.\d+\.md$")
        for n in names do
            Assert.True(partPattern.IsMatch n,
                sprintf "Stage 3 part 박제 파일명 패턴 미정합 — %s" n)

        // part 번호 1, 2 ... 연속 박제 확인.
        let partIndices =
            names
            |> Array.map (fun n ->
                let m = System.Text.RegularExpressions.Regex.Match(n, @"\.(\d+)\.md$")
                Int32.Parse(m.Groups.[1].Value))
            |> Array.sort
        Assert.Equal(1, partIndices.[0])
        for i in 0 .. partIndices.Length - 1 do
            Assert.Equal(i + 1, partIndices.[i])

        // 각 part 가 cap 안 + header 6행 (canary + 기존 5행, 2026-05-27 patch) + footer 박제
        // (각 part 는 strategy 의 header / footer 공유).
        for path in summary.SummaryFiles do
            let content = File.ReadAllText(path, System.Text.Encoding.UTF8)
            let bytes = System.Text.Encoding.UTF8.GetByteCount content
            Assert.True(bytes <= MarkdownCapPolicy.MaxMarkdownBytes,
                sprintf "part %s size %d bytes 가 cap %d 초과"
                    (Path.GetFileName path) bytes MarkdownCapPolicy.MaxMarkdownBytes)
            // canary 박제 — 모든 part 가 strategy 의 header 공유 → canary 도 모든 part 에 박제 보장.
            Assert.Contains("canary:", content)
            // strategy header (cap policy 가 raw markdown 의 header 줄을 모든 part 에 보존).
            Assert.Contains("generated by IoListStrategy", content)
            // split note 박제 (`<!-- split: part N of M (data rows ...) -->`).
            Assert.Contains("<!-- split: part", content))

[<Fact>]
let ``Stage 0/1/2 단일 박제 — 기존 파일명 패턴 유지 (multi-part 변경의 회귀 방지)`` () =
    withTempDir (fun dir ->
        // 작은 IoList fixture → cap 안 (Stage 0 Original) → 단일 박제.
        makeIoListFixture (Path.Combine(dir, "small-iolist.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        let summary = runDump dir
        Assert.Equal(1, summary.SummaryFiles.Length)
        let name = Path.GetFileName summary.SummaryFiles.[0]
        // 기존 파일명 — `{strategyName}-{docId}-{basename}.md` (part 번호 없음).
        let singlePattern =
            System.Text.RegularExpressions.Regex(@"^IoListStrategy-[0-9a-f]{8}-small-iolist\.md$")
        Assert.True(singlePattern.IsMatch name,
            sprintf "Stage 0 단일 박제 파일명 패턴 미정합 — %s" name))

// ── 시나리오 5: 자료 A/B/C 실파일 (옵션) → 정확히 3 markdown ──────────────

let private kbCorePath = @"F:\Git\dualsoft\secrets\KBSamples\core"

let private hasKbCoreFixtures () : bool =
    Directory.Exists kbCorePath
    && Directory.GetFiles(kbCorePath).Length >= 3

[<Fact>]
let ``자료 A/B/C 실파일 (옵션) → summary/ 안 IoList 1 markdown 만 박제 (2026-06-04 WorkOrder/PDF 일반 자료)`` () =
    // WorkOrder/PdfControlSpec 제거 — 자료 B(PDF)/C(WorkOrder)는 strategy 산출물 미생성 (일반 자료).
    // 자료 A(IoList)만 specialized 박제. user-guide(guide/*.md)는 UserGuideImporter 별도 박제라 본 카운트 무관.
    if not (hasKbCoreFixtures()) then
        // fixture 미존재 머신은 graceful skip — secrets repo clone 전제 (todo M5 default).
        ()
    else
        withTempDir (fun dir ->
            // 자료 A/B/C 를 temp dir 로 복사 (KBSamples 직접 색인 시 stale 잔재 방어).
            for src in Directory.GetFiles(kbCorePath) do
                File.Copy(src, Path.Combine(dir, Path.GetFileName src))
            Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

            let summary = runDump dir
            Assert.Equal(1, summary.SummaryFiles.Length)
            // strategy 분포 — IoList 1 (WorkOrder/PDF 는 일반 자료로 미박제).
            let names = summary.SummaryFiles |> Array.map Path.GetFileName
            Assert.Contains(names, fun (n: string) -> n.StartsWith("IoListStrategy-"))
            Assert.DoesNotContain(names, fun (n: string) -> n.StartsWith("WorkOrderStrategy-"))
            Assert.DoesNotContain(names, fun (n: string) -> n.StartsWith("PdfControlSpecStrategy-")))
