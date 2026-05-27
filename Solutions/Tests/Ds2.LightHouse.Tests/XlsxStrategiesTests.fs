module Ds2.LightHouse.Tests.XlsxStrategiesTests

open System
open System.IO
open System.Threading
open System.Text.RegularExpressions
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

/// **PR-I1 (todo-documents-based-gfm.md §2 PR-I1 + §3.1)** — strategy interface + IoListStrategy +
/// XlsxSignatureClassifier + Diagnostics schema 회귀.
///
/// 검증 시나리오:
///   1. synthetic xlsx fixture (광명2 IO List 의 4-block layout 모사) — strategy 매치 + markdown 구조 회귀.
///   2. signature 미매치 fixture (단순 docx-like 표) — Rejected 반환 회귀.
///   3. classifier 진입점 — Matched / NearMiss / Unmatched 분기 회귀.
///   4. DiagnosticSchemas — JSON serialize / deserialize round-trip 회귀.
///   5. (옵션) 자료 A 실파일 — `F:/Git/dualsoft/secrets/KBSamples/core/4-3.*.xlsx` 존재 시만.

let private withTempPath (ext: string) (action: string -> 'r) : 'r =
    let path = Path.Combine(Path.GetTempPath(), sprintf "lh-iolist-%s%s" (Guid.NewGuid().ToString("N")) ext)
    try action path
    finally if File.Exists path then File.Delete path

// ── synthetic xlsx fixture — 광명2 IO List 4-block layout 모사 ────────────────

/// 단일 cell 박제 helper. CellReference + InlineString.
let private mkCell (cellRef: string) (text: string) : Cell =
    let cell = Cell()
    cell.CellReference <- StringValue(cellRef)
    cell.DataType <- EnumValue(CellValues.InlineString)
    let inl = InlineString()
    inl.AppendChild(Text(text)) |> ignore
    cell.AppendChild(inl) |> ignore
    cell

/// 1-based column index → Excel column letter ("A" / "AA" / ...).
let private colLetter (idx: int) : string =
    let sb = System.Text.StringBuilder()
    let mutable n = idx
    while n > 0 do
        let r = (n - 1) % 26
        sb.Insert(0, char (int 'A' + r)) |> ignore
        n <- (n - 1) / 26
    sb.ToString()

/// row 박제 helper. cells = (colIdx, value) tuple list (1-based).
let private mkRow (rowIdx: uint) (cells: (int * string) list) : Row =
    let row = Row()
    row.RowIndex <- UInt32Value(rowIdx)
    for (colIdx, value) in cells do
        let cellRef = sprintf "%s%d" (colLetter colIdx) (int rowIdx)
        row.AppendChild(mkCell cellRef value) |> ignore
    row

/// 광명2 IO List signature 충족 xlsx fixture.
///   - 시트 8개 (zone code S201~S205 + 보조)
///   - 각 시트에 R1=헤더, R6+=데이터 (`%IW`/`%QW` 토큰 다수, BOOL DataType)
///   - 4-block 가로 layout (20 columns / row).
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
            // COVER 시트 — 데이터 0, R1 텍스트.
            sheetData.AppendChild(mkRow 1u [ 1, "COVER" ]) |> ignore
        else
            // R1 헤더 — "PLC : LS XGT" / "I/O LIST".
            sheetData.AppendChild(mkRow 1u [ 1, "PLC : LS XGT"; 5, "I/O LIST" ]) |> ignore
            // R2 — Project meta.
            sheetData.AppendChild(mkRow 2u [
                1, "Project: 광명2_SV_SIDE_OUTER"
                5, sprintf "Part: %s" name
            ]) |> ignore
            // R3 — 표 헤더.
            sheetData.AppendChild(mkRow 3u [ 1, "워드" ]) |> ignore
            // R4 — zone meta.
            sheetData.AppendChild(mkRow 4u [ 1, sprintf "Zone: %s" name ]) |> ignore
            // R5 — 컬럼 헤더 (4 block).
            let headerCells = [
                1, "Word";  2, "Tag";  3, "Type";  4, "Addr";  5, "Sym"
                6, "Word";  7, "Tag";  8, "Type";  9, "Addr"; 10, "Sym"
                11, "Word"; 12, "Tag"; 13, "Type"; 14, "Addr"; 15, "Sym"
                16, "Word"; 17, "Tag"; 18, "Type"; 19, "Addr"; 20, "Sym"
            ]
            sheetData.AppendChild(mkRow 5u headerCells) |> ignore
            // R6~R13 — 데이터 8 rows × 4 block = 32 행 (long-form 변환 후).
            // %IW/%QW 토큰 ≥ 50 충족 위해 8 rows × 4 block = 32 + 다른 시트 합산 → 7 sheet × 32 = 224 토큰.
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

/// signature 미매치 xlsx fixture — 단순 BOM 표 (zone code 없음, IW/QW 없음).
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

let private extractDocument (path: string) : ExtractedDocument =
    use ext = new OoxmlExtractor() :> IExtractor
    ext.Extract(path, CancellationToken.None)

// ── IoListStrategy signature + build 회귀 ─────────────────────────────────

[<Fact>]
let ``IoListStrategy signature — 광명2 fixture 는 매치 (score >= threshold)`` () =
    withTempPath ".xlsx" (fun path ->
        makeIoListFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        let result = strategy.Signature extracted
        Assert.True(result.Matched, sprintf "signature 미매치 — detail=%s" result.Detail)
        Assert.True(result.Score >= result.Threshold))

[<Fact>]
let ``IoListStrategy signature — BOM fixture 는 미매치 (score < threshold)`` () =
    withTempPath ".xlsx" (fun path ->
        makeBomFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        let result = strategy.Signature extracted
        Assert.False(result.Matched, sprintf "BOM 이 signature 매치되면 false-positive — detail=%s" result.Detail))

[<Fact>]
let ``IoListStrategy Build — 매치 시 markdown 반환 + 머리말 5행 강제`` () =
    withTempPath ".xlsx" (fun path ->
        makeIoListFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        match strategy.Build (path, extracted) with
        | Rejected entry -> Assert.Fail(sprintf "Build 가 Rejected 반환 — reason=%s" entry.Reason)
        | Built markdown ->
            // 머리말 5행 (`documents-based-gfm.md` §8.5.5).
            let lines = markdown.Split([| '\n' |], 8)
            Assert.True(lines.Length >= 5, "최소 5 행 머리말 필요")
            // 1행: generated by IoListStrategy vX.Y.Z at <ISO>
            Assert.Contains("generated by IoListStrategy v", lines.[0])
            // 2행: source: <filename> (docId: <8-char>)
            Assert.Contains("source:", lines.[1])
            Assert.Contains("docId:", lines.[1])
            // 3행: signature: IoListStrategy:N/9
            Assert.Contains("signature: IoListStrategy:", lines.[2])
            // 4행: estimated-tokens
            Assert.Contains("estimated-tokens:", lines.[3])
            // 5행: strategy-version: IoListStrategy vX.Y.Z (M11 — version 박제 stale 감지 용)
            Assert.Contains("strategy-version: IoListStrategy v", lines.[4]))

[<Fact>]
let ``IoListStrategy Build — markdown 안 H1 + sheet H2 + 6 컬럼 표 회귀`` () =
    withTempPath ".xlsx" (fun path ->
        makeIoListFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        match strategy.Build (path, extracted) with
        | Rejected entry -> Assert.Fail(sprintf "Build 가 Rejected 반환 — reason=%s" entry.Reason)
        | Built markdown ->
            // H1 = "# IO List — <project>"
            Assert.Contains("# IO List —", markdown)
            // H2 시트명 — zone code sheet 들 각각 박제.
            Assert.Contains("## S201_RB1", markdown)
            Assert.Contains("## S204_KEY_JIG1", markdown)
            // 6 컬럼 헤더 (Word/Direction/Tag/DataType/Address/Symbol).
            Assert.Contains("| Word | Direction | Tag | DataType | Address | Symbol |", markdown)
            // 컬럼 alignment row.
            Assert.Contains("|:---|:---:|:---|:---|:---|:---|", markdown)
            // Input/Output 박제.
            Assert.Contains("| Input |", markdown)
            Assert.Contains("| Output |", markdown)
            // %IW 데이터 row 1개 이상.
            Assert.Matches(Regex(@"\|\s*%IW\d+"), markdown)
            // footer (M11 정합).
            Assert.Contains("<!-- footer -->", markdown)
            Assert.Contains("cross-ref-hash:", markdown))

[<Fact>]
let ``IoListStrategy Build — BOM fixture 는 Rejected 반환 (정상 분기, exception 아님)`` () =
    withTempPath ".xlsx" (fun path ->
        makeBomFixture path
        let extracted = extractDocument path
        let strategy = IoListStrategy() :> IXlsxStrategy
        match strategy.Build (path, extracted) with
        | Built _ -> Assert.Fail("BOM 이 Built 반환 — false-positive")
        | Rejected entry ->
            Assert.Equal("IoListStrategy", entry.Strategy)
            Assert.Contains("signature 미매치", entry.Reason))

// ── XlsxSignatureClassifier dispatch 회귀 ─────────────────────────────────

[<Fact>]
let ``XlsxSignatureClassifier — 매치 시 Matched 반환`` () =
    withTempPath ".xlsx" (fun path ->
        makeIoListFixture path
        let extracted = extractDocument path
        let result = XlsxSignatureClassifier.classify path extracted
        match result with
        | Matched (name, markdown) ->
            Assert.Equal("IoListStrategy", name)
            Assert.True(markdown.Length > 0)
        | other -> Assert.Fail(sprintf "Matched 기대했으나 %A" other))

[<Fact>]
let ``XlsxSignatureClassifier — 점수 0 fixture 는 Unmatched 반환`` () =
    withTempPath ".xlsx" (fun path ->
        makeBomFixture path
        let extracted = extractDocument path
        let result = XlsxSignatureClassifier.classify path extracted
        match result with
        | Unmatched -> ()
        | NearMiss entries ->
            // BOM 이 점수 1 이상 받아 NearMiss 로 분기될 수도 있음 — 단 매치는 안 됨.
            Assert.All(entries, fun e ->
                Assert.True(e.Score < e.Threshold))
        | other -> Assert.Fail(sprintf "Unmatched/NearMiss 기대했으나 %A" other))

[<Fact>]
let ``XlsxSignatureClassifier — PR-I2 시점 strategy list = IoListStrategy + WorkOrderStrategy 2건`` () =
    // todo §2.1 의 "PR-I2 dispatch 정합 — strategy list append 1줄" 가드.
    let names =
        XlsxSignatureClassifier.strategies
        |> List.map (fun s -> s.Name)
    Assert.Equal(2, List.length names)
    Assert.Equal<string list>([ "IoListStrategy"; "WorkOrderStrategy" ], names)

// ── WorkOrderStrategy fixture + signature + build (PR-I2) ────────────────

/// 광명2 조립작업서 signature 충족 xlsx fixture.
///   - 시트 4개 (#201 / #202 / #203 + COVER)
///   - 각 #\d{3} 시트는 Gantt header (작업내역/시작/시간/누계) + Sym 토큰 다수.
///   - 데이터 행 누계 단조 증가, 한국어 비율 ≥ 30%.
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
            // R1 = Gantt header: NO / SYM / 작업내역 / 시작 / 시간 / 누계
            sheetData.AppendChild(mkRow 1u [
                1, "NO"; 2, "SYM"; 3, "작업내역"; 4, "시작"; 5, "시간"; 6, "누계"
            ]) |> ignore
            // R2~R11 — 10 step. 누계 단조 증가.
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

[<Fact>]
let ``WorkOrderStrategy signature — Gantt fixture 는 매치 (score >= threshold)`` () =
    withTempPath ".xlsx" (fun path ->
        makeWorkOrderFixture path
        let extracted = extractDocument path
        let strategy = WorkOrderStrategy() :> IXlsxStrategy
        let result = strategy.Signature extracted
        Assert.True(result.Matched, sprintf "WorkOrder signature 미매치 — detail=%s" result.Detail)
        Assert.True(result.Score >= result.Threshold))

[<Fact>]
let ``WorkOrderStrategy signature — IoList fixture 는 미매치 (시트명 #\d{3} 없음)`` () =
    withTempPath ".xlsx" (fun path ->
        makeIoListFixture path
        let extracted = extractDocument path
        let strategy = WorkOrderStrategy() :> IXlsxStrategy
        let result = strategy.Signature extracted
        Assert.False(result.Matched, sprintf "IoList 가 WorkOrder 매치되면 false-positive — detail=%s" result.Detail))

[<Fact>]
let ``WorkOrderStrategy Build — 매치 시 markdown 반환 + 머리말 5행 강제`` () =
    withTempPath ".xlsx" (fun path ->
        makeWorkOrderFixture path
        let extracted = extractDocument path
        let strategy = WorkOrderStrategy() :> IXlsxStrategy
        match strategy.Build (path, extracted) with
        | Rejected entry -> Assert.Fail(sprintf "Build 가 Rejected 반환 — reason=%s" entry.Reason)
        | Built markdown ->
            let lines = markdown.Split([| '\n' |], 8)
            Assert.True(lines.Length >= 5, "최소 5 행 머리말 필요")
            Assert.Contains("generated by WorkOrderStrategy v", lines.[0])
            Assert.Contains("source:", lines.[1])
            Assert.Contains("docId:", lines.[1])
            Assert.Contains("signature: WorkOrderStrategy:", lines.[2])
            Assert.Contains("estimated-tokens:", lines.[3])
            Assert.Contains("strategy-version: WorkOrderStrategy v", lines.[4]))

[<Fact>]
let ``WorkOrderStrategy Build — H1 + sheet H2 + 6 컬럼 표 회귀`` () =
    withTempPath ".xlsx" (fun path ->
        makeWorkOrderFixture path
        let extracted = extractDocument path
        let strategy = WorkOrderStrategy() :> IXlsxStrategy
        match strategy.Build (path, extracted) with
        | Rejected entry -> Assert.Fail(sprintf "Build 가 Rejected 반환 — reason=%s" entry.Reason)
        | Built markdown ->
            // H1 = "# 조립작업서 — <project>"
            Assert.Contains("# 조립작업서 —", markdown)
            // H2 시트명 — Gantt suffix 제거된 상태 (`[Gantt schedule]` strip).
            Assert.Contains("## 5-1. #201", markdown)
            Assert.Contains("## 5-3. #203", markdown)
            // [Gantt schedule] suffix 가 H2 에 남지 않아야 함.
            Assert.DoesNotContain("[Gantt schedule]", markdown)
            // 6 컬럼 헤더 (NO/SYM/TASK/START/DURATION/CUMULATIVE).
            Assert.Contains("| NO | SYM | TASK | START | DURATION | CUMULATIVE |", markdown)
            // 컬럼 alignment row.
            Assert.Contains("|---:|:---|:---|---:|---:|---:|", markdown)
            // 누계 정수 박제 (단조 증가 마지막 = 30).
            Assert.Contains("| 30 |", markdown)
            // footer (M11 정합).
            Assert.Contains("<!-- footer -->", markdown)
            Assert.Contains("cross-ref-hash:", markdown))

[<Fact>]
let ``XlsxSignatureClassifier — WorkOrder fixture 가 WorkOrderStrategy 로 dispatch`` () =
    withTempPath ".xlsx" (fun path ->
        makeWorkOrderFixture path
        let extracted = extractDocument path
        let result = XlsxSignatureClassifier.classify path extracted
        match result with
        | Matched (name, markdown) ->
            Assert.Equal("WorkOrderStrategy", name)
            Assert.Contains("# 조립작업서 —", markdown)
        | other -> Assert.Fail(sprintf "Matched 기대했으나 %A" other))

[<Fact>]
let ``WorkOrderStrategy — pdf DocType fixture 는 signature 미매치 (DocType != Xlsx 가드)`` () =
    // pdf 처럼 다른 DocType 의 ExtractedDocument 를 받을 경우 0 점.
    let extracted : ExtractedDocument = {
        DocType = FileKind.Pdf
        PageOrSheetCnt = Some 50
        Title = Some "test"
        Outline = [||]
        Segments = [||]
        Images = [||]
    }
    let strategy = WorkOrderStrategy() :> IXlsxStrategy
    let result = strategy.Signature extracted
    Assert.False(result.Matched)
    Assert.Equal(0, result.Score)

// ── 자료 C 실파일 회귀 (옵션 — 파일 존재 시만) ──────────────────────────

let private sampleCPath =
    @"F:/Git/dualsoft/secrets/KBSamples/core/4-1. SV_SIDE_조립작업서_240328.xlsx"

[<Fact>]
let ``자료 C 실파일 — 존재 시 WorkOrderStrategy 매치 + markdown 박제 e2e`` () =
    if not (File.Exists sampleCPath) then
        // 다른 머신에서 fixture 부재 — silent skip (M5 default 정합 — fixture commit 보류).
        ()
    else
        let extracted = extractDocument sampleCPath
        Assert.Equal(FileKind.Xlsx, extracted.DocType)
        let result = XlsxSignatureClassifier.classify sampleCPath extracted
        match result with
        | Matched (name, markdown) ->
            Assert.Equal("WorkOrderStrategy", name)
            // 머리말 5행.
            let headLines = markdown.Split([| '\n' |], 6)
            Assert.True(headLines.Length >= 5)
            Assert.Contains("generated by WorkOrderStrategy v", headLines.[0])
            Assert.Contains("strategy-version: WorkOrderStrategy v", headLines.[4])
            // 6 컬럼 표 헤더.
            Assert.Contains("| NO | SYM | TASK | START | DURATION | CUMULATIVE |", markdown)
            // 자료 C 의 #\d{3} 시트 H2 박제 — #204 또는 #201 중 하나는 박제 (자료 9시트 가정).
            Assert.True(
                markdown.Contains("#201") || markdown.Contains("#204") || markdown.Contains("#202"),
                "자료 C 의 #\\d{3} 시트 H2 박제 누락")
            // 토큰 추정값 양수.
            Assert.Matches(Regex(@"estimated-tokens: \d+"), markdown)
        | other ->
            Assert.Fail(sprintf "자료 C 가 WorkOrderStrategy 매치 실패 — %A" other)

// ── DiagnosticSchemas JSON round-trip ────────────────────────────────────

[<Fact>]
let ``DiagnosticSchemas — RejectedEntry serialize / deserialize round-trip`` () =
    let entry = {
        File = @"F:/secrets/test.xlsx"
        Sheet = Some "S201_RB1"
        Reason = "매크로 xlsm 미지원"
        Strategy = "IoListStrategy"
        RejectedAt = DateTime(2026, 5, 27, 10, 0, 0, DateTimeKind.Utc)
    }
    let json = DiagnosticJson.serialize entry
    Assert.Contains("\"file\":", json)
    Assert.Contains("\"sheet\":", json)
    Assert.Contains("\"strategy\":", json)
    let restored : RejectedEntry = DiagnosticJson.deserialize json
    Assert.Equal(entry.File, restored.File)
    Assert.Equal(entry.Sheet, restored.Sheet)
    Assert.Equal(entry.Reason, restored.Reason)
    Assert.Equal(entry.Strategy, restored.Strategy)

[<Fact>]
let ``DiagnosticSchemas — NearMissEntry round-trip`` () =
    let entry = {
        File = @"F:/secrets/test.xlsx"
        CandidateStrategy = "IoListStrategy"
        Score = 4
        Threshold = 6
        Detail = "ioTokens=30(+0), zoneSheets=2(+0), ..."
        DetectedAt = DateTime(2026, 5, 27, 11, 0, 0, DateTimeKind.Utc)
    }
    let json = DiagnosticJson.serialize entry
    let restored : NearMissEntry = DiagnosticJson.deserialize json
    Assert.Equal(entry.Score, restored.Score)
    Assert.Equal(entry.Threshold, restored.Threshold)
    Assert.Equal(entry.CandidateStrategy, restored.CandidateStrategy)

[<Fact>]
let ``DiagnosticSchemas — StaleEntry round-trip (PR-I1 schema only)`` () =
    let entry = {
        File = @"F:/secrets/test.xlsx"
        Strategy = "IoListStrategy"
        IndexedVersion = "1.0.0"
        CurrentVersion = "2.0.0"
        IndexedHash = "deadbeef"
        CurrentHash = "cafebabe"
        Reason = "major-version-mismatch"
        DetectedAt = DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc)
    }
    let json = DiagnosticJson.serialize entry
    let restored : StaleEntry = DiagnosticJson.deserialize json
    Assert.Equal(entry.IndexedVersion, restored.IndexedVersion)
    Assert.Equal(entry.CurrentVersion, restored.CurrentVersion)
    Assert.Equal(entry.Reason, restored.Reason)

// ── 자료 A 실파일 회귀 (옵션 — 파일 존재 시만) ────────────────────────────

let private sampleAPath =
    @"F:/Git/dualsoft/secrets/KBSamples/core/4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx"

[<Fact>]
let ``자료 A 실파일 — 존재 시 IoListStrategy 매치 + markdown 박제 e2e`` () =
    if not (File.Exists sampleAPath) then
        // 다른 머신에서 fixture 부재 — silent skip (M5 default 정합 — fixture commit 보류).
        ()
    else
        let extracted = extractDocument sampleAPath
        Assert.Equal(FileKind.Xlsx, extracted.DocType)
        let result = XlsxSignatureClassifier.classify sampleAPath extracted
        match result with
        | Matched (name, markdown) ->
            Assert.Equal("IoListStrategy", name)
            // 머리말 5행.
            let headLines = markdown.Split([| '\n' |], 6)
            Assert.True(headLines.Length >= 5)
            Assert.Contains("generated by IoListStrategy v", headLines.[0])
            Assert.Contains("strategy-version: IoListStrategy v", headLines.[4])
            // 6 컬럼 표 헤더.
            Assert.Contains("| Word | Direction | Tag | DataType | Address | Symbol |", markdown)
            // 자료 A 의 핵심 zone — S204 / S205 시트 박제.
            Assert.True(
                markdown.Contains("## S204_KEY") || markdown.Contains("## S205_WRS"),
                "자료 A 의 S204/S205 zone 시트 H2 박제 누락")
            // 토큰 추정값 양수.
            Assert.Matches(Regex(@"estimated-tokens: \d+"), markdown)
            // **Backlog D (todo-documents-based-gfm.md §6.1 N5 + documents-based-gfm.md §8.5.5)** —
            // 자료 A 의 raw markdown 약 454KB → MarkdownCapPolicy 의 256KB cap 안 흡수 검증.
            // Stage 1 (column truncate) 또는 Stage 2 (sampling) 로 cap 안 들어가야 함 (Stage 3 split 는
            // 본 backlog scope 외, Stage 1/2 적용 결과 단일 markdown 반환).
            let mdBytes = System.Text.Encoding.UTF8.GetByteCount markdown
            Assert.True(
                mdBytes <= MarkdownCapPolicy.MaxMarkdownBytes,
                sprintf "자료 A markdown %d bytes 가 cap %d 초과 — MarkdownCapPolicy escalation 결함"
                    mdBytes MarkdownCapPolicy.MaxMarkdownBytes)
        | other ->
            Assert.Fail(sprintf "자료 A 가 IoListStrategy 매치 실패 — %A" other)
