module Ds2.LightHouseService.Tests.SummaryDirIntegrationTests

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Cli
open Ds2.LightHouseService
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

/// **PR-I3 (todo-documents-based-gfm.md §2 PR-I3 + §8.5.5)** — `summary/` 디렉토리의 upload e2e 회귀.
///
/// 검증 시나리오 (service 실행 미의존, lib-level e2e):
///   1. IoList synthetic xlsx fixture 색인 → `TextDumper.dumpStrategySummaries` 박제 → `Packager.createZip`
///      → `.lighthouse-kb/summary/*.md` zip 안 동봉 verify.
///   2. zip 추출 (LightHouseService.ZipImport.extractAll) 후 `summary/` markdown 파일 round-trip 정합 verify.
///   3. rejected.json / near-miss.json 도 zip 안 동봉 verify (signature 미달 fixture 박제 시).

// ── helpers (TextDumperStrategiesTests 와 동형, 모듈 격리 위해 사본) ───────────

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-sum-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private mkCell (cellRef: string) (text: string) : Cell =
    let cell = Cell()
    cell.CellReference <- StringValue(cellRef)
    cell.DataType <- EnumValue(CellValues.InlineString)
    let inl = InlineString()
    inl.AppendChild(Text(text)) |> ignore
    cell.AppendChild(inl) |> ignore
    cell

let private colLetter (idx: int) : string =
    let sb = StringBuilder()
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
                1, "Project: ***REDACTED***2_SV_SIDE_OUTER"
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

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new PdfExtractor() :> IExtractor
    new OoxmlExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]

let private disposeAll (ex: IExtractor list) =
    for e in ex do e.Dispose()

// ── 시나리오 1: IoList xlsx → summary/ 박제 → Packager.createZip → zip 안 동봉 ─

[<Fact>]
let ``Packager.createZip — summary/*.md 가 zip 안 .lighthouse-kb/summary/ prefix 로 동봉`` () =
    withTempDir (fun dir ->
        makeIoListFixture (Path.Combine(dir, "iolist.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore

        // strategy 박제 (Packager.runIngest 미진입 path — text dump / SQLite 미생성 시뮬).
        let ex = extractors()
        let dumpSummary =
            try TextDumper.dumpStrategySummaries dir ex CancellationToken.None
            finally disposeAll ex
        Assert.Equal(1, dumpSummary.SummaryFiles.Length)

        // zip 박제.
        let zipPath = Packager.createZip dir
        try
            Assert.True(File.Exists zipPath)
            use archive = ZipFile.OpenRead zipPath
            // `.lighthouse-kb/summary/IoListStrategy-*.md` entry 존재 verify.
            let summaryEntries =
                archive.Entries
                |> Seq.filter (fun e ->
                    e.FullName.StartsWith(".lighthouse-kb/summary/", StringComparison.OrdinalIgnoreCase)
                    && e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                |> Seq.toArray
            Assert.Equal(1, summaryEntries.Length)
            Assert.Contains("IoListStrategy-", summaryEntries.[0].FullName)
        finally
            if File.Exists zipPath then File.Delete zipPath)

// ── 시나리오 2: zip → LightHouseService.ZipImport.extractAll round-trip ──────

[<Fact>]
let ``zip round-trip — extractAll 후 summary/ markdown 파일 byte-equal`` () =
    withTempDir (fun dir ->
        makeIoListFixture (Path.Combine(dir, "iolist.xlsx"))
        Directory.CreateDirectory (SqliteStore.kbDir dir) |> ignore
        let ex = extractors()
        let dumpSummary =
            try TextDumper.dumpStrategySummaries dir ex CancellationToken.None
            finally disposeAll ex
        let originalSummaryBytes = File.ReadAllBytes dumpSummary.SummaryFiles.[0]

        let zipPath = Packager.createZip dir
        try
            withTempDir (fun extractRoot ->
                let zipSize = (FileInfo zipPath).Length
                use zipStream = File.OpenRead zipPath
                let _ = ZipImport.extractAll zipStream extractRoot zipSize 100
                // .lighthouse-kb/summary/IoListStrategy-*.md 추출 verify.
                let extractedSummaryDir = Path.Combine(extractRoot, ".lighthouse-kb", "summary")
                Assert.True(Directory.Exists extractedSummaryDir,
                    sprintf "summary/ 디렉토리 미추출 — %s" extractedSummaryDir)
                let extractedFiles = Directory.GetFiles(extractedSummaryDir, "*.md")
                Assert.Equal(1, extractedFiles.Length)
                // byte-equal round-trip.
                let extractedBytes = File.ReadAllBytes extractedFiles.[0]
                Assert.Equal<byte>(originalSummaryBytes, extractedBytes))
        finally
            if File.Exists zipPath then File.Delete zipPath)
