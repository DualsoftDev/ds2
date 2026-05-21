# Ds2.LightHouse — PPTX / XLSX 활성 + 내부 이미지 색인 + LLM 표시 wiring

세션 이어받기용 TODO. parent `done-lighthouse-kb-index.md` (r15, Phase 1 종결 + Phase 2 s6-r12 진행 중) 의 Phase 2 잔여 task 일부를 사용자 결정사항 반영하여 분리 박제.

> **변경 이력**: [revision-history/lighthouse-kb-index-xlsx-pptx-images.md](revision-history/lighthouse-kb-index-xlsx-pptx-images.md) (분리 보관)

---

## 0. 작업 목표 (한 줄)

`Ds2.LightHouse` 의 OoxmlExtractor 에 **PPTX 활성 + XLSX 활성** 추가, 두 포맷의 **내부 이미지 색인** 활성, `attachment_read` 의 **이미지 표시 모드** (`caption_only` / `includeImages`) wiring.

---

## 1. 배경 / 현재 SSOT 상태 (2026-05-21 시점, 코드 grep 검증)

### parent Phase 1 완료 박제 (done-lighthouse-kb-index.md r15)
- `Ds2.LightHouse` lib 본체 + lib unit test 종결. 누적 154 Fact (lib only) / 누적 532 Fact (server 포함).
- commit anchor: `9736237` (Phase 1 §4.8) → `00b72eb` (§4.4) → `bccb0ea` (§4.1).

### parent Phase 2 진행 박제
- **s6-r8**: schema 확장 — `ImageCache` / `ImageReferences` 테이블 + `Chunks.ImageCount` ALTER + `IndexerVersion` 1.0.0 → 1.1.0 + `SchemaVersion` 1 → 2.
- **s6-r11**: `ImageStore.fs` 신설 (170 line, 9 함수 surface).
- **s6-r12**: `Indexer.ingestImagesIntoStore` + `ExtractedDocument.Images` 필드 wiring. 기존 9 extractor 가 `Images = [||]` literal 박제.
- **s6-r12-followup 이후**: PdfExtractor + OoxmlExtractor (docx) 의 image 추출은 **이미 활성** (todo r15 의 task C2/C3 체크박스는 stale — 실제 코드는 박제 완료).

### 실 코드 활성 상태 (grep 검증 2026-05-21)

| 항목 | 코드 SSOT |
|---|---|
| PDF text 추출 | `PdfExtractor.fs` — 페이지 단위 segment (`p=N`) |
| **PDF image 추출** | `PdfExtractor.fs:78-97` — `page.GetImages()` + `IPdfImage.TryGetPng(out bytes)` + per-image fail-safe. `RefLocator = p=N` + `Ordinal = 1..M`. **활성** |
| DOCX text + outline + 이미지 | `OoxmlExtractor.fs` — body paragraph + table cell + header/footer + image-only paragraph 모두 박제. **활성** |
| DOCX image ContentType 화이트리스트 | `image/png` / `image/jpeg` / `image/gif` / `image/webp` 4 종. 그 외 (EMF/WMF/BMP/TIFF) 자연 skip |
| **PPTX activation** | `OoxmlExtractor.fs:335-338` (`Supports = Docx only`) + `OoxmlExtractor.fs:340-362` (`Extract` 가 `ExtractDocx` 직호출 — 진정한 dispatch 아님, **Critical-1**). **비활성** |
| **XLSX activation** | 동일. **비활성** |
| **RefLocator EBNF (slide/sheet × img)** | `RefLocator.fs` DU `RefUnit = P/Slide/Sheet` + `RefSubKey = Img` + `tryParse/toStored/formatDisplay` 모두 **일반화 박제 완료** (r1 검증, **Critical-2**). `slide=5#img=2` / `sheet=BOM#img=1` round-trip + "슬라이드 5 그림 2" / "시트 BOM 그림 1" 표시형 활성. |
| ImageStore + DB | sha256 dedup + `ImageCache(ImageHash, StoredPath, MimeType, Width, Height, CaptionText, CaptionAt, CaptionModel)` + `ImageReferences(DocumentId, ChunkId, ImageHash, RefLocator, Ordinal)`. 실측 **225 line / 13 surface** (r0 의 170/9 stale, r1 정정). **활성** |
| **`captionGen` caller 주입 surface** | `Indexer.fs:85, 130-131, 181` — `captionGen: byte[] -> ImageFormat -> CaptionResult` 가 `ingestImagesIntoStore` + `ingestFile` 인자로 박제 완료. lib 은 surface only, provider 는 caller (server) 책임. **활성** (r1 박제) |
| **VLM caption provider 본체** | provider 구현 자체는 lib 외부 (server) — `Ds2.LightHouseService` 측 책임. lib 의 `CaptionGenerator.noop` 가 무인 경로 default. **lib scope out** |
| `attachment_read` image 모드 | `caption_only` / `includeImages` 둘 다 미구현. **미진입** (server phase Phase S5 흡수) |
| **Test fixture 패턴 SSOT** | `TestFixtures.fs` 의 `SamplePng.bytes` (1×1 PNG 결정성 + `ExpectedSha256` literal) + `OoxmlExtractorTests.fs:17` `withTempPath` + `OoxmlExtractorTests.fs:23` `makeDocx` 외 6 helper (`makeDocxWithImage` / `WithInlineImage` / `WithImageOnlyParagraph` / `WithHeaderImage` / `WithTableCellImage` / `WithNestedTableImage`) **프로그램적 생성** SSOT. Fixtures 폴더 / fsproj `<None Include>` / `CopyToOutputDirectory` 패턴 **부재** (r1 검증, **Critical-3**). PPTX/XLSX 도 동일 — `PresentationDocument.Create` / `SpreadsheetDocument.Create` + `makePptx*` / `makeXlsx*` helper 신설. |

### 영향 받는 파일 / 경로

| 경로 | 역할 |
|---|---|
| `Solutions\Core\Ds2.LightHouse\Extractors\OoxmlExtractor.fs` | PPTX / XLSX 활성 진입점. 신규 `ExtractPptx` + `ExtractXlsx` 추가. `Supports` 분기 확대. |
| `Solutions\Core\Ds2.LightHouse\Extractors\PdfExtractor.fs` | 참조용 (image 추출 패턴 재사용). 변경 없음. |
| `Solutions\Core\Ds2.LightHouse\Models.fs` | `FileKind` DU 의 `Pptx` / `Xlsx` case 이미 존재 — 변경 없음. `ExtractedImage` / `ExtractedDocument.Images` 도 그대로. |
| `Solutions\Core\Ds2.LightHouse\RefLocator.fs` | `slide=N#img=M` / `sheet=<name>#img=M` sub-key 지원 — EBNF 의 `Unit` 에 slide/sheet 포함 + `#img=N` 일반화 필요. 현 구현 점검 의무. |
| `Solutions\Core\Ds2.LightHouse\ImageStore.fs` | 변경 없음 (재사용). |
| `Solutions\Core\Ds2.LightHouse\Indexer.fs` | `routeExtractor` 가 OoxmlExtractor 의 Pptx/Xlsx Supports true 반환을 그대로 활용. 변경 없음 가능성 높음 — 진입 시 grep 재확인. |
| `Solutions\Tests\Ds2.LightHouse.Tests\OoxmlExtractorTests.fs` | PPTX / XLSX Fact 추가. |
| `Solutions\Tests\Ds2.LightHouse.Tests\RefLocatorTests.fs` | `slide=5#img=2` / `sheet=BOM#img=1` round-trip Fact 추가. |
| `Apps\Promaker\Docs\done-lighthouse-kb-index.md` | §3.13 RefLocator 표에 행 2개 추가 (PPTX 이미지 / XLSX 이미지). Phase 2 의 pptx/xlsx 활성 task 체크 갱신. |

---

## 2. 사용자가 명시적으로 확정한 결정사항 (2026-05-21 turn)

### XLSX — "최대한 간편하게"

| 결정 항목 | 결정 | 사유 / 적용 방식 |
|---|---|---|
| **수식** | 포기 — `Cell.CellValue` (cached value) 만 사용 | `CellFormula` element 자체 무시. stale value 면 그대로. 평가 안 함. |
| **hidden sheet** | 포기 (색인 안 함) | `Sheet.State = "hidden" \| "veryHidden"` 시 skip + `Log.lighthouse.Debug`. visible 만 진입. |
| **merged cell** | top-left 값만 | `MergeCells` element enumerate 안 함. 자연스럽게 top-left cell 의 값만 노출, 다른 cell 은 empty → 자연 skip. |
| **빈 행** | 무시 (단순 skip) | segment 끊기 X. `Row` 안 모든 cell value 가 빈 문자열이면 그 행 자체 skip. |
| **행 packed 크기** | `Chunker` 한도에 위임 | xlsx 전용 packing logic 없음. 시트 1개 = 1 segment (또는 빈 행 skip 후 모든 행 tab join). Chunker 가 200~500 token 한도로 자동 분할. |
| **좌표 RefLocator** | Phase 3 backlog | 시트 단위 `sheet=<name>` 만 활성. `sheet=BOM!A1:D40` 범위 ref 는 Phase 3 backlog 박제. `attachment_read` 의 ref 파서도 그때 강화. |

### PPTX + XLSX 내부 이미지

| 결정 항목 | 결정 |
|---|---|
| **이미지 색인** | 두 포맷 모두 별도 색인 (text 와 함께) — 사용자 결정 |
| **LLM 표시 가능** | `attachment_read` 의 `caption_only` / `includeImages` 모드 wiring 진행 — 사용자 결정 |
| **텍스트** | 살림 — text 추출 + image 추출이 동등 first-class |

### 공통 — DOCX 의 image 추출 helper 재사용

`OoxmlExtractor.fs` 의 `extractImagesAtRefLocator` / `extractImagesFromBlips` / `collectValidBlips` 패턴 그대로 재사용. ContentType 화이트리스트 4 종 동일.

---

## 3. 남은 할 일 (Phase 별)

### Task 0 — OoxmlExtractor 선행 refactor (r1 신설, Critical-1 + M6 + M8)

**범위**: 진정한 dispatch 없는 `Extract` + closure 화된 image helper 4종 + 중복된 `ContentType→ImageFormat` 매핑을 PPTX/XLSX 진입 전 정리.

**예상 분량**: ~50 line 변경, DOCX 동작 회귀 0. 자가 검열 sub-agent 의무 (3+ trigger 충족).

**구현 절차**:
- [ ] **(a) Extract dispatch 신설** — 현 `Extract` (`OoxmlExtractor.fs:340-362`) 가 `ExtractDocx` 직호출 + 4 arm 의 `DocType=Docx` hardcode → `FileKind` 기반 dispatch 로 변경:
  ```
  member this.Extract (path, ct) =
      let kind = Classifier.classifyForKb path
      OoxmlExtractor.ExtractWithFailSafe kind path ct (fun () ->
          match kind with
          | Docx -> OoxmlExtractor.ExtractDocx path ct
          | Pptx -> OoxmlExtractor.ExtractPptx path ct
          | Xlsx -> OoxmlExtractor.ExtractXlsx path ct
          | _ -> failwith "OoxmlExtractor: Supports invariant 위반")
  ```
- [ ] **(b) `ExtractWithFailSafe` wrapper** — 4 catch arm (FileFormatException / OpenXmlPackageException / InvalidDataException / IOException) 의 빈 record 박제를 helper 로 통합. `DocType` 인자를 받아 정확한 FileKind 박제:
  ```
  static member private ExtractWithFailSafe
      (docType: FileKind) (path: string) (ct: CancellationToken)
      (action: unit -> ExtractedDocument) : ExtractedDocument =
      try action ()
      with
      | :? FileFormatException as ex -> ...
      | :? OpenXmlPackageException as ex -> ...
      | :? InvalidDataException as ex -> ...
      | :? IOException as ex -> ...
      | :? System.Xml.XmlException as ex -> ...  // r1 m4: deferred parsing
      → { DocType = docType; PageOrSheetCnt = None; Title = None; Outline = [||]; Segments = [||]; Images = [||] }
  ```
  m4 추가 — `System.Xml.XmlException` 도 catch (OpenXml lazy deferred parsing 시점에 발생 가능).
- [ ] **(c) `ImagePartToFormat` helper** — 현 코드 `OoxmlExtractor.fs:115-118` + `:174-177` 2회 중복된 ContentType 매핑을 단일 함수로:
  ```
  static member private ImagePartToFormat (contentType: string) : ImageFormat option =
      match contentType with
      | "image/png"  -> Some Png
      | "image/jpeg" -> Some Jpeg
      | "image/gif"  -> Some Gif
      | "image/webp" -> Some Webp
      | _ -> None
  ```
- [ ] **(d) closure helper 4종 static 승격** — `extractImagesAtRefLocator` / `extractImagesFromBlips` / `collectValidBlips` / `extractImagesFromOpenXmlPart` 가 `ExtractDocx` body 안 closure 로 `images: ResizeArray<ExtractedImage>` 를 capture 중 (R3 M6) → static member 로 승격, `images` 를 인자로 받기. PPTX/XLSX 의 `ExtractPptx` / `ExtractXlsx` 에서 재사용 가능해야 함.
- [ ] **(e) DOCX 회귀 검증** — 기존 `OoxmlExtractorTests.fs` 의 makeDocx* 7 helper 기반 Fact 모두 통과 (회귀 0). build + test green 확인 후 commit.

**자가 검열 의무**: refactor 단독 commit + sub-agent 일반 검열. 이후 Task 1/2 가 helper 100% 재사용.

### Task 1 — PPTX 활성 + 내부 이미지

**범위**: `OoxmlExtractor.fs` 에 `ExtractPptx` 메서드 신설 + `Supports = Pptx` 추가. **전제** = Task 0 완료 (closure helper 4종 static 승격 + `ExtractWithFailSafe` wrapper 도입).

**예상 분량 (r2 보정)**: F# 신규 코드 **160~240 line** (fixture helper `makeMinimalPptx` base + `makePptx*` 6종 mutate 포함, OpenXml SDK helper API 의 Theme + SlideMaster + SlideLayout boilerplate ~30-60 line 흡수) + 테스트 **10~14 Fact**.

**구현 절차**:
- [ ] `Supports` 분기에 `| Pptx -> true` 추가.
- [ ] `static member private ExtractPptx (path: string) (ct: CancellationToken) : ExtractedDocument` 신설. Title 박제 = `None` (DOCX 와 동일 OpenXml 3.x `PackageProperties` experimental 회피 — r1 M7).
- [ ] `PresentationDocument.Open(path, false)` 진입 — `ExtractWithFailSafe` (Task 0-b) wrapper 가 fail-safe 흡수.
- [ ] **DrawingML namespace alias 박제** (r2 Minor 5) — 현 `OoxmlExtractor.fs:9-12` 의 `Wordprocessing` open + `Blip = Drawing.Blip` alias 패턴. PPTX 의사코드 안에서 사용할 `Drawing.Paragraph` / `Drawing.Text` 는 W namespace 와 형명 충돌 가능 → file top 에 alias 추가:
  ```
  type private DrawingParagraph = DocumentFormat.OpenXml.Drawing.Paragraph
  type private DrawingText = DocumentFormat.OpenXml.Drawing.Text
  ```
- [ ] **슬라이드 순회 — `SlideIdList` SSOT + null guard** (r1 Critical-5 + r2 Major-1, MS Learn 공식):
  ```
  let pres = presentationPart.Presentation
  // r2 Major-1: 빈/손상 pptx 의 SlideIdList null 시 NRE 방지. ExtractWithFailSafe 5종 어느 catch 도 NRE 안 잡음.
  let slideIds =
      if isNull pres || isNull pres.SlideIdList then Seq.empty
      else pres.SlideIdList.Elements<SlideId>() :> seq<_>
  let mutable slideNo = 1
  for slideId in slideIds do
      ct.ThrowIfCancellationRequested()   // r2 Minor 6: 100+ slide deck cancel 응답성
      // r2 Minor 3: 손상 pptx 의 RelationshipId null guard
      if not (isNull slideId.RelationshipId) && slideId.RelationshipId.HasValue then
          let relId = slideId.RelationshipId.Value
          match presentationPart.GetPartById(relId) with
          | :? SlidePart as slidePart ->
              ...
              slideNo <- slideNo + 1
          | _ ->
              Log.lighthouse.Warn(sprintf "ExtractPptx: relId=%s 가 SlidePart 아님 — path=%s" relId path)
      else
          Log.lighthouse.Warn(sprintf "ExtractPptx: slideId(no=%d) RelationshipId null — path=%s" slideNo path)
  ```
  `presentationPart.SlideParts` 직접 enumerate **금지** — zip relationship 순서라 reorder/insert 시 정렬 어긋남.
- [ ] **각 슬라이드의 추출**:
  - **Outline (title placeholder)** — Title + CenteredTitle 둘 다 매칭 (r1 M4):
    ```
    let titleShape =
        slidePart.Slide.CommonSlideData.ShapeTree.Elements<Shape>()
        |> Seq.tryFind (fun shape ->
            let ph = shape.NonVisualShapeProperties.ApplicationNonVisualDrawingProperties.PlaceholderShape
            if isNull ph || isNull ph.Type then false
            else
                ph.Type.Value = PlaceholderValues.Title           // ECMA-376: <ph type="title">
                || ph.Type.Value = PlaceholderValues.CenteredTitle) // <ph type="ctrTitle">
    ```
    EnumValue 직접 비교 (string "title" 비교 금지). title 부재 슬라이드 fallback = `"슬라이드 N"` literal (r1 M11 결정).
  - **Segment (paragraph break 보존)** — `Slide.InnerText` 직접 사용 금지 (bullet 들러붙음, r1 M5). paragraph 사이 `\n` 명시 삽입:
    ```
    let textBuilder = StringBuilder()
    // r2 Minor 5: DrawingParagraph / DrawingText alias (file top 박제)
    for paragraph in slidePart.Slide.Descendants<DrawingParagraph>() do
        let paraText = String.Join("", paragraph.Descendants<DrawingText>() |> Seq.map (fun t -> t.Text))
        if paraText.Length > 0 then
            textBuilder.AppendLine(paraText) |> ignore
    ```
  - **Speaker notes 합성** — body + `--- 노트 ---` marker + notes (r1 M10 결정. 단일 RefLocator 유지 + LLM citation 시 시각 구분):
    ```
    if not (isNull slidePart.NotesSlidePart) then
        let notesText = ... (NotesSlide 의 paragraph break 보존 동일 패턴)
        if notesText.Length > 0 then
            textBuilder.AppendLine("--- 노트 ---").AppendLine(notesText) |> ignore
    ```
    `RefLocator = "slide=N"` 단일.
  - **Image** — `SlidePart` 안 `Blip.Embed` enumerate → Task 0-d 의 static `extractImagesAtRefLocator` 재사용. `location = "slide"`, `refLocator = "slide=N"`. resolver = `slidePart.GetIdOfPart(imgPart)`.
- [ ] `PageOrSheetCnt = Some slideIds.Count` 박제 (전체 슬라이드 수).
- [ ] **명시 skip 항목**:
  - SlideMaster / SlideLayout placeholder text — `SlidePart` 직속 enumerate, master/layout 진입 안 함.
  - comments / notes master — Phase 3 backlog.

**테스트** (`OoxmlExtractorTests.fs`):
- [ ] 정상 pptx — title + body + speaker notes + image 1장 슬라이드 3장. segment 3 / outline 3 / image 1+ / `PageOrSheetCnt = Some 3` 검증.
- [ ] image-only 슬라이드 — title=0, body=0, image 1장. segment 미박제 / image 박제.
- [ ] notes 합성 — body + `--- 노트 ---` marker + notes 단일 segment.
- [ ] 손상 pptx fail-safe — `DocType = Pptx` (Task 0 회귀 가드) + 빈 결과.
- [ ] 화이트리스트 외 image (EMF) skip — image 0.
- [ ] **title 부재 슬라이드** (r1 M11) — outline label = "슬라이드 N" fallback.
- [ ] **paragraph break 보존** (r1 M5) — bullet 2개 슬라이드 → segment text 안에 `\n` 포함.
- [ ] **빈 pptx** (r1 M13) — 0 슬라이드 → `PageOrSheetCnt = Some 0` + 빈 결과.
- [ ] **동일 image cross-slide dedup** (r1 M12) — logo 가 3 슬라이드 박힌 deck → `ImageCache` 행 1 / `ImageReferences` 행 3.
- [ ] **CenteredTitle (`ctrTitle`)** (r1 M4) — title 슬라이드의 ctrTitle placeholder 도 outline label 매칭.

**Fixture helper** (프로그램적 생성 — `PresentationDocument.Create`. r2 Minor 7: base+mutate 패턴):
- `makeMinimalPptx (path: string) : PresentationPart` — minimal valid pptx base (Theme + SlideMaster + SlideLayout boilerplate ~30-60 line, OpenXml SDK helper API 의존). 다른 helper 가 mutate.
- `makePptx (path: string)` — minimal base + 3 slide (title + body + notes + image) mutate
- `makePptxImageOnly (path: string)` — base + 1 image-only slide
- `makePptxEmpty (path: string)` — base + 0 slide (SlideIdList Empty 또는 null — Major-1 fixture)
- `makePptxWithDuplicateImage (path: string)` — base + 3 slide × 동일 sha256 logo
- `makePptxCenteredTitle (path: string)` — base + `ctrTitle` placeholder slide
- `makePptxNoTitle (path: string)` — base + title 부재 slide (M11 fixture)

### Task 2 — XLSX 활성 + 내부 이미지

**범위**: `OoxmlExtractor.fs` 에 `ExtractXlsx` 메서드 신설 + `Supports = Xlsx` 추가. **전제** = Task 0 완료.

**예상 분량 (r1 보정)**: F# 신규 코드 **180~280 line** (sparse cell `expandSparseRow` + phonetic ruby filter + fixture helper `makeXlsx*` 5~7 종 포함) + 테스트 **12~16 Fact**.

**구현 절차**:
- [ ] `Supports` 분기에 `| Xlsx -> true` 추가.
- [ ] `static member private ExtractXlsx (path: string) (ct: CancellationToken) : ExtractedDocument` 신설. Title 박제 = `None` (r1 M7 정합).
- [ ] `SpreadsheetDocument.Open(path, false)` 진입 — `ExtractWithFailSafe` (Task 0-b) 가 fail-safe 흡수.
- [ ] **SharedStringTable 사전 로드 — phonetic ruby (rPh) 제외** (r1 M2 + r2 Minor 1 단순화, ECMA-376):
  ```
  let sst = workbookPart.SharedStringTablePart
  let sstItems =
      if isNull sst then [||]
      else
          sst.SharedStringTable.Elements<SharedStringItem>()
          |> Seq.map (fun item ->
              // PhoneticRun (<rPh>) / PhoneticProperties (<phoneticPr>) 제외, Text (<t>) 만 join.
              // r2 Minor 1: 이중부정 제거 — Ancestors<PhoneticRun>() 가 비어있는 (= ruby 외부) Text 만 통과.
              item.Descendants<Text>()
              |> Seq.filter (fun t -> t.Ancestors<PhoneticRun>() |> Seq.isEmpty)
              |> Seq.map (fun t -> t.Text)
              |> String.concat "")
          |> Array.ofSeq
  ```
  `item.InnerText` 직접 사용 금지 (일본어/일부 한국어 xlsx 의 ruby 오염).
- [ ] **Sheet.State enum 비교** (r1 Critical-6, MS Learn 공식):
  ```
  let isHidden (sheet: Sheet) =
      not (isNull sheet.State)
      && sheet.State.HasValue
      && (sheet.State.Value = SheetStateValues.Hidden
          || sheet.State.Value = SheetStateValues.VeryHidden)
  let visibleSheets =
      workbookPart.Workbook.Sheets.Elements<Sheet>()
      |> Seq.filter (isHidden >> not)
  ```
  `State.Value.ToString() = "visible"` 비교 **금지** — locale/대소문자 변동 + `EnumValue.HasValue = false` 시 `InvalidOperationException` risk.
- [ ] **sparse cell 처리 — `expandSparseRow` helper** (r1 Critical-4, MS Learn + ECMA-376 강 근거):
  ```
  /// Excel column letter ("A" / "AA" / "AB" / ...) → 1-based index.
  let columnLetterToIndex (letter: string) : int =
      let mutable result = 0
      for c in letter.ToUpperInvariant() do
          result <- result * 26 + (int c - int 'A' + 1)
      result

  /// CellReference ("B12" / "AA3") → column letter 부분만.
  let cellRefToColumnLetter (cellRef: string) : string =
      String(cellRef.ToCharArray() |> Array.takeWhile System.Char.IsLetter)

  /// row 의 sparse cell 들을 dense array 로 확장. gap 은 "" 채움.
  /// r2 Minor 2: worst-case 폭주 가드 — A1 + ZZZ1 시 maxCol=18278. 산업 빈도 매우 낮으나 hard cap + Warn.
  /// r2 Minor 4: cell.CellReference null guard (OOXML 규약상 required 이나 SDK strict 아님).
  let [<Literal>] MaxXlsxColumnsPerRow = 1024
  let expandSparseRow (cells: Cell seq) (sheetName: string) : string[] =
      let cellList =
          cells
          |> Seq.filter (fun c ->
              if isNull c.CellReference || isNull c.CellReference.Value then
                  Log.lighthouse.Warn(sprintf "ExtractXlsx: CellReference null skip — sheet=%s" sheetName)
                  false
              else true)
          |> Seq.toList
      if List.isEmpty cellList then [||]
      else
          let maxCol =
              cellList
              |> List.map (fun c -> columnLetterToIndex (cellRefToColumnLetter c.CellReference.Value))
              |> List.max
          let cappedMax =
              if maxCol > MaxXlsxColumnsPerRow then
                  Log.lighthouse.Warn(sprintf "ExtractXlsx: row column cap 초과 (maxCol=%d > %d) — sheet=%s, cap 적용" maxCol MaxXlsxColumnsPerRow sheetName)
                  MaxXlsxColumnsPerRow
              else maxCol
          let result = Array.create cappedMax ""
          for cell in cellList do
              let colIdx = columnLetterToIndex (cellRefToColumnLetter cell.CellReference.Value) - 1
              if colIdx < cappedMax then
                  result.[colIdx] <- resolveCellValue cell
          result
  ```
  `row.Elements<Cell>()` 직접 tab join **금지** — `<row>` 가 값 있는 cell 만 child 라 A1+C1 row 는 `[A1, C1]` 2개 반환, "A1값\tC1값" 으로 컬럼 alignment 깨짐 (B 컬럼 silent 소실).
- [ ] **Row.OrderBy(RowIndex)** (r1 M3) — `worksheetPart.Worksheet.Descendants<Row>()` 순서 미보장 → `Seq.sortBy (fun r -> r.RowIndex.Value)` 명시.
- [ ] **셀 값 해결 — DataType 분기** (r1 M1 Str/Error 추가):
  ```
  let resolveCellValue (cell: Cell) : string =
      if isNull cell.CellValue then ""
      else
          match cell.DataType with
          | null -> cell.CellValue.Text  // Number / Date / Boolean (cached value)
          | dt when dt.Value = CellValues.SharedString ->
              match System.Int32.TryParse cell.CellValue.Text with
              | true, idx when idx >= 0 && idx < sstItems.Length -> sstItems.[idx]
              | _ -> ""
          | dt when dt.Value = CellValues.InlineString ->
              if isNull cell.InlineString then "" else cell.InlineString.InnerText
          | dt when dt.Value = CellValues.String -> cell.CellValue.Text  // formula string result
          | dt when dt.Value = CellValues.Error ->
              Log.lighthouse.Debug(sprintf "ExtractXlsx: error cell skip — ref=%s, value=%s" cell.CellReference.Value cell.CellValue.Text)
              ""  // #REF! #DIV/0! 등 명시 skip
          | _ -> cell.CellValue.Text
  ```
  `cell.CellValue` null guard 박제 (r1 M14 — `<c><f>...</f></c>` cached value 부재 cell).
- [ ] **각 시트의 추출**:
  - `sheetName = sheet.Name.Value`. RefLocator escape 정책 (r2 Major-2 반론 정리):
    - **`#` 만 fragment separator 충돌** → 시트명에 `#` 포함 시 `Log.lighthouse.Warn` + sheet skip (EBNF escape Phase 3 backlog). r1 M18 SSOT.
    - **`=` / `.` 는 안전** — RefLocator.fs:71-78 의 `parseFragment` 가 `String.IndexOf('=')` (첫 `=` 만) 로 split, `.` 은 EBNF 특수문자 아님. 예: `"sheet=BOM=spec"` → `tryParse` 가 `Value="BOM=spec"` 으로 정상 parse + round-trip 보존. 시트명 `=` / `.` 포함은 skip 불필요.
    - **Excel 금지 char** (`\ / ? * [ ]`) — Excel 자체가 시트명에 허용 안 함, 박제 불필요.
  - **Outline**: `OutlineNode { NodeType = Sheet; Label = sheetName; RefLocator = "sheet=<name>" }`. r1 M17 결정 — segment 박제 안 되더라도 outline 은 박제 (시트 존재 자체가 정보).
  - **타임라인 좁은 컬럼 필터 빌드** (r3 — Gantt 시각화 noise 제거):
    ```
    /// `worksheet.Columns` 의 width < threshold 인 컬럼 1-based index set 빌드.
    /// 산업 .xlsx 의 Gantt 시각화는 보통 width=0.75 의 좁은 컬럼 70+개로 표현 (실측: 4-1.SV_SIDE 의 AM~DF).
    /// 이 셀들은 빈 값 + fill style only 라 dense array 의 trailing `""` 가 됨 → join 단계 drop.
    /// tick label (`1,2,3...`) 처럼 값 있는 셀은 보존.
    let [<Literal>] NarrowColumnWidthThreshold = 1.0
    let narrowColIndexes (worksheetPart: WorksheetPart) : Set<int> =
        let cols = worksheetPart.Worksheet.GetFirstChild<Columns>()
        if isNull cols then Set.empty
        else
            cols.Elements<Column>()
            |> Seq.filter (fun c ->
                not (isNull c.Width) && c.Width.HasValue
                && c.Width.Value < NarrowColumnWidthThreshold
                && not (isNull c.Min) && not (isNull c.Max))
            |> Seq.collect (fun c -> seq { int c.Min.Value .. int c.Max.Value })
            |> Set.ofSeq
    ```
  - **행 단위 tab join + sparse cell + sort + 좁은컬럼 필터** (r3 보강):
    ```
    let narrowSet = narrowColIndexes worksheetPart
    let sortedRows =
        worksheetPart.Worksheet.Descendants<Row>()
        |> Seq.sortBy (fun r -> if isNull r.RowIndex then 0u else r.RowIndex.Value)
    let sb = StringBuilder()
    for row in sortedRows do
        ct.ThrowIfCancellationRequested()
        let dense = expandSparseRow (row.Elements<Cell>()) sheetName   // r2 Minor 2/4: cap + null guard
        // r3: 좁은(=Gantt 시각화) 컬럼이 빈 값이면 drop, 값 있으면 (tick label) 보존
        let kept =
            dense
            |> Array.mapi (fun i v ->
                let colIdx = i + 1
                if Set.contains colIdx narrowSet && v = "" then None else Some v)
            |> Array.choose id
        let line = String.Join("\t", kept)
        if line.Trim().Length > 0 then
            sb.AppendLine(line) |> ignore
    ```
  - **Segment**: text 0 이면 skip (시트는 outline 만 박제). text 있으면 `ExtractedSegment { OutlineIndex = Some idx; RefLocator = "sheet=<name>"; Text = sb.ToString().Trim() }`.
  - **Image** — `worksheetPart.DrawingsPart` null guard (r1 M16):
    ```
    if not (isNull worksheetPart.DrawingsPart) then
        let drawingsPart = worksheetPart.DrawingsPart
        let imgPartByRelId =
            drawingsPart.ImageParts
            |> Seq.map (fun ip -> drawingsPart.GetIdOfPart(ip), ip)
            |> Map.ofSeq
        let resolve relId = Map.tryFind relId imgPartByRelId
        OoxmlExtractor.extractImagesAtRefLocator
            drawingsPart.WorksheetDrawing resolve "sheet"
            (sprintf "sheet=%s" sheetName)
            images
    ```
    좌표 anchor (OneCellAnchor/TwoCellAnchor) 무시.
- [ ] `PageOrSheetCnt = Some visibleSheets.Count` 박제 (hidden 제외, r1 m5 명문화).
- [ ] **명시 skip 항목**:
  - `Sheet.State = hidden | veryHidden` — `Log.lighthouse.Debug` + skip.
  - `CellFormula` — element 무시. `CellValue` cached 만.
  - `MergeCells` — top-left cell 만 자연 노출.
  - `CellValues.Error` — `Log.lighthouse.Debug` + 빈 값.
  - 시트명 `#` 포함 — `Log.lighthouse.Warn` + sheet skip.
  - Defined Name / Pivot Table / Chart — Phase 3 backlog.

**테스트** (`OoxmlExtractorTests.fs`):
- [ ] 정상 xlsx — 3 시트 (visible 2 + hidden 1) → segment 2 + outline 2 + hidden 미박제. `PageOrSheetCnt = Some 2`.
- [ ] **sparse cell expandSparseRow** (r1 Critical-4) — A1=v1, C1=v2 row → segment text 안 `"v1\t\tv2"` (B 컬럼 빈 채로 보존).
- [ ] SharedString resolve — SST 안 문자열 정상 노출.
- [ ] **phonetic ruby skip** (r1 M2) — `<rPh>` 포함 SST item → ruby 제외, base text 만 segment 박제.
- [ ] formula cell — cached value 만 노출, formula string 미노출.
- [ ] **formula cached value 부재 (`<c><f>...</f></c>` no CellValue)** (r1 M14) — null guard 동작, "" 박제.
- [ ] **CellValues.Error** (r1 M1) — `#REF!` cell → 빈 값 + log.
- [ ] merged cell — top-left 값만, 다른 cell 위치는 빈 값.
- [ ] 빈 행 skip — 중간 빈 행 미박제, 다음 데이터 행 이어짐.
- [ ] **Row.OrderBy(RowIndex)** (r1 M3) — element 순서가 RowIndex 와 다른 fixture → RowIndex 순 정렬 보장.
- [ ] image 추출 — `DrawingsPart` 있는 시트의 image 박제, anchor 좌표 무시.
- [ ] **DrawingsPart null guard** (r1 M16) — image 없는 시트 → null guard 동작, image 0.
- [ ] **veryHidden** (r1 M15) — `SheetStateValues.VeryHidden` 시트도 skip (hidden 과 분리 fixture).
- [ ] **Sheet.State HasValue=false** (r1 Critical-6) — `State` 자체 부재 시 visible 처리.
- [ ] **시트명 `#` 포함** (r1 M18) — `"A#B"` 시트 → Warn + skip + 다른 시트 정상.
- [ ] **시트명 `=` 포함 round-trip** (r2 Major-2 반론 검증) — `"BOM=spec"` 시트 → skip 안 함, RefLocator `"sheet=BOM=spec"` 가 tryParse round-trip 정상 통과 (`Value="BOM=spec"` 보존).
- [ ] **expandSparseRow worst-case cap** (r2 Minor 2) — A1 + ZZZ1 row → `MaxXlsxColumnsPerRow=1024` 적용 + Warn log + 첫 1024 컬럼만 박제.
- [ ] **타임라인 좁은 컬럼 drop** (r3) — `Column.Width=0.75` 컬럼 70개 + 데이터 컬럼 (width=4.5) 6개 fixture → segment text 가 데이터 컬럼만 tab join, trailing 빈 컬럼 제거. 산업 .xlsx (`4-1.SV_SIDE`) 의 row 14 (`1\tM\t233-1호…\t0\t6`) 가 그대로 박제.
- [ ] **타임라인 좁은 컬럼의 tick label 보존** (r3) — width=0.75 컬럼이지만 값 (`1,2,3...`) 있는 row 7 → drop 되지 않고 보존. (filter 는 `width < 1.0 AND value = ""` 동시 충족 시에만 drop)
- [ ] 손상 xlsx fail-safe — `DocType = Xlsx` (Task 0 dispatch 회귀 가드) + 빈 결과.

**Fixture helper** (프로그램적 생성 — `SpreadsheetDocument.Create`. r2 Minor 7: base+mutate 패턴):
- `makeMinimalXlsx (path: string) : WorkbookPart` — minimal valid xlsx base (Workbook + Sheets + SharedStringTablePart boilerplate ~30-50 line). 다른 helper 가 mutate.
- `makeXlsx (path: string)` — base + 3 sheet (visible 2 + hidden 1)
- `makeXlsxSparseRow (path: string)` — base + A/C 컬럼만 채운 row (Critical-4 fixture)
- `makeXlsxWithPhoneticRuby (path: string)` — base + SST 의 `<rPh>` 포함 item (M2 fixture)
- `makeXlsxWithFormula (path: string)` — base + formula cell (cached + null) (M14 fixture)
- `makeXlsxWithError (path: string)` — base + `#REF!` error cell (M1 fixture)
- `makeXlsxOutOfOrderRows (path: string)` — base + RowIndex 역순 element (M3 fixture)
- `makeXlsxWithImage (path: string)` — base + DrawingsPart + Blip
- `makeXlsxWithHashSheet (path: string)` — base + 시트명에 `#` 포함 (M18 fixture)
- `makeXlsxWithEqualsSheet (path: string)` — base + 시트명에 `=` 포함 (r2 Major-2 반론 검증 fixture — round-trip 정상 통과 의무)
- `makeXlsxGanttNarrowCols (path: string)` — base + `Columns` element 에 width=0.75 × 70 컬럼 + width=4.5 × 6 데이터 컬럼 + row 14 같은 sparse 데이터 (r3 timeline filter fixture)

### Task 2-extra — Gantt schedule 시트 type 힌트 (r3 신설)

**범위**: 산업 .xlsx 의 작업 일정표 (Gantt 형식) 시트 검출 + segment 머리에 role-기반 동적 안내문 prepend. LLM 이 row tab-join 데이터를 "이 컬럼은 시작시간 (초)", "이 컬럼은 작업 내역" 등으로 정확 해석하도록 지원. Task 2 본체 완료 후 진입.

**전제** = Task 2 완료 (`ExtractXlsx` + `expandSparseRow` + timeline filter 박제).

**예상 분량**: F# 신규 코드 **~120 line** (synonym map literal 50 + normalize helper 30 + score 검출 40) + 테스트 **6 Fact** + fixture **3 종** 신설.

**구현 절차**:
- [ ] **(a) 8 role + synonym set 박제** — lib 내부 const literal. 본 todo r3 박제 8 role:
  ```
  type SheetRole =
      | RNo | RSym | RTask | RStart | RDuration | RCumulative | RScore | RGrade
  let private roleSynonyms : Map<SheetRole, Set<string>> =
      Map.ofList [
          RNo,         Set.ofList ["no"; "no."; "번호"; "순번"; "순서"; "#"; "step"; "idx"; "seq"]
          RSym,        Set.ofList ["sym"; "symbol"; "기호"; "심볼"; "약호"; "code"]
          RTask,       Set.ofList ["작업내역"; "작업내용"; "작업"; "task"; "work"; "공정"; "내역"; "description"; "desc"; "단계"]
          RStart,      Set.ofList ["시작"; "시작시간"; "시작초"; "개시"; "start"; "starttime"; "from"; "begin"]
          RDuration,   Set.ofList ["시간"; "소요"; "소요시간"; "소요초"; "duration"; "dur"; "span"; "length"]
          RCumulative, Set.ofList ["누계"; "누적"; "누적시간"; "종료"; "종료시간"; "cum"; "cumulative"; "to"; "end"; "total"]
          RScore,      Set.ofList ["용접점수"; "점수"; "score"; "points"]
          RGrade,      Set.ofList ["용접등급"; "등급"; "grade"; "rank"; "class"]
      ]
  ```
- [ ] **(b) normalize helper** — header cell 텍스트 → match key 변환:
  ```
  /// 공백/줄바꿈/탭/전각공백 제거 + 괄호 안 부연 strip + 한자→한글 (소수 대표만) + 소문자.
  let private normalizeHeader (s: string) : string =
      let stripped = System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", "")  // (sec), (s)
      let noSpace = stripped |> String.filter (fun c -> not (System.Char.IsWhiteSpace c))
      let lower = noSpace.ToLowerInvariant()
      lower
        .Replace("時間", "시간").Replace("開始", "시작")
        .Replace("作業", "작업").Replace("番號", "번호")
  ```
- [ ] **(c) 2-row header concat** — 산업 .xlsx 의 merged 위쪽 (`시간`) + 아래쪽 (`10,20,30`) 분리 시 위쪽만 단독 매칭. column-wise 로 row1+row2 normalize 결과를 concat 후 매칭. 단일 키 정확 매칭 우선, concat 매칭은 fallback.
- [ ] **(d) Map<int,Role> 빌더** — header 후보 row (텍스트 비율 >50% + 숫자 비율 <20% 인 첫 row) 의 각 컬럼 cell value normalize → role match. `Map<columnIndex, SheetRole>` 빌드.
- [ ] **(e) score 판정** — match 개수 ≥3 AND (`RStart`+`RDuration`+`RCumulative` 중 ≥2) → **Gantt schedule**. ≥5 + narrow col 50+ → **Gantt schedule (strong)**. else 일반 시트.
- [ ] **(f) role 기반 동적 안내문** — segment text 머리에 prepend (Gantt 시트만):
  ```
  let preamble =
      let parts =
          roleMap
          |> Map.toList
          |> List.sortBy fst
          |> List.map (fun (colIdx, role) ->
              let letter = columnIndexToLetter colIdx
              let label =
                  match role with
                  | RNo -> "NO(순번)" | RSym -> "SYM(심볼)" | RTask -> "TASK(작업내역)"
                  | RStart -> "START(시작초)" | RDuration -> "DURATION(소요초)"
                  | RCumulative -> "CUMULATIVE(누계초)"
                  | RScore -> "SCORE(점수)" | RGrade -> "GRADE(등급)"
              sprintf "%s=%s" letter label)
      sprintf "이 시트는 작업 일정표(Gantt)입니다. 컬럼 의미: %s. 좁은 컬럼들은 Gantt 시각화 막대(데이터 없음).\n"
          (String.concat ", " parts)
  sb.Insert(0, preamble) |> ignore
  ```
- [ ] **(g) Outline label 갱신** — Gantt 검출 시 `OutlineNode.Label = sprintf "%s [Gantt schedule]" sheetName`.

**테스트** (`OoxmlExtractorTests.fs`, Task 2-extra Fact 6종):
- [ ] **정상 Gantt 검출** — header `NO|SYM|작업내역|시작|시간|누계` 6 컬럼 → segment 머리에 preamble + outline label `[Gantt schedule]`.
- [ ] **순서 바뀜 매칭** — header `[SYM, NO, 시작, 작업내역, 시간, 누계]` 순 → 동일 role map 빌드 + preamble 의 컬럼 letter 가 실제 위치 반영.
- [ ] **영문 헤더** — `[NO, Symbol, Task, Start, Duration, Cumulative]` → Gantt 검출 + preamble 정상 박제.
- [ ] **공백 normalize** — `"작 업 내 역"`, `"시 작"`, `"시간(sec)"` 헤더 → 정상 매칭 (괄호 부연 strip + 공백 제거).
- [ ] **2-row merged header** — row1=merged `시간` (위쪽), row2=`10,20,30...` (아래쪽) + 데이터 컬럼은 row1 단독 매칭 → Gantt 검출 + 타임라인 컬럼 미오인.
- [ ] **미검출 false negative** — match 2개 이하 (예: `NO|Item|값`) 시트 → Gantt 판정 안 함, preamble 미박제, outline label 그대로.

**Fixture helper 신설**:
- `makeXlsxGantt (path: string)` — 산업 .xlsx 축소판 (3 시트, 1개가 Gantt 형식, `width=0.75` × 30 narrow + 데이터 컬럼 6 + row 5개 + tick label row)
- `makeXlsxGanttReordered (path: string)` — Gantt 검출 but 컬럼 순서 바뀜
- `makeXlsxGanttTwoRowHeader (path: string)` — merged 위쪽 + tick 아래쪽 (2-row header fixture)

**self-check** — 본 todo §6 #1 trigger ②④ (신규 함수/타입 5+, dispatch helper 신설) 충족 → 자가 검열 sub-agent 의무.

**전제 정정** (r1): `RefLocator.fs` 가 이미 `RefUnit = P | Slide | Sheet` + `RefSubKey = Img` DU + `tryParse`/`toStored`/`formatDisplay` 일반화 박제 완료 — **코드 변경 0**. r0 의 "PDF 만 지원 가능성 → 일반화 필요" 박제는 hallucination 으로 검증됨. 진입자가 신규 분기 추가 시도 시 시간 낭비.

**남은 작업** = `RefLocatorTests.fs` 에 regression guard Fact 추가만:

- [ ] `[<InlineData("slide=5#img=2")>]` round-trip (`tryParse >> Option.map toStored = Some`)
- [ ] `[<InlineData("sheet=BOM#img=1")>]` round-trip
- [ ] `[<InlineData("sheet=주요-사양#img=3")>]` 한글/하이픈 round-trip
- [ ] `[<InlineData("sheet=BOM!A1:D40#img=2")>]` range × img cartesian round-trip (r1 누락 Fact 5종 마지막)
- [ ] 표시형 변환 Fact 3 종 — "슬라이드 5 그림 2" / "시트 BOM 그림 1" / "시트 BOM A1:D40 그림 2"
- [ ] 기존 `[<InlineData("p=14#img=2")>]` regression guard 명시 (코멘트로 "r1: Critical-2 — slide/sheet × img 일반화 보호")

**예상 분량**: 코드 변경 0, 테스트 ~10 line 추가 (5~8 InlineData + 3 표시형).

> r3 박제 — Task 2-extra (Gantt schedule 시트 type 힌트) 는 Task 2 상위 본문 안 §Task 2-extra 절 참조.

### Task 4 — parent todo `done-lighthouse-kb-index.md` 갱신

- [ ] §3.13 RefLocator 표에 행 2개 추가 (PPTX 이미지 / XLSX 이미지).
- [ ] §4.3 Phase 2 부분의 task 체크박스 갱신:
  - "PPTX (슬라이드 + speaker notes) 활성" → [x] (본 todo 완료 시점)
  - "XLSX 활성 — 컬럼 헤더 + 행 그룹 packed" → [x] (단순화 정책 박제로 변경)
- [ ] §4.3 Phase 3 backlog 박제 추가:
  - "xlsx 좌표 RefLocator (`sheet=BOM!A1:D40`) + `attachment_read` ref 파서 강화"
  - "xlsx Defined Name / Pivot Table / Chart"
  - "pptx SlideMaster / SlideLayout / comments / notes master"
- [ ] rev 표에 rN+1 행 추가 (PPTX/XLSX 활성 + image 색인 완료 박제, 사용자 결정 5건 출처).

### Task 5 — VLM caption 생성 wiring (r1 분담 단순화)

**r1 m2 박제** — 분담 결정 불필요. `Indexer.fs:85, 130-131, 181` 의 `captionGen: byte[] -> ImageFormat -> CaptionResult` surface 가 이미 caller 주입 박제 완료. lib 은 surface only (`CaptionGenerator.noop` default), provider 본체는 **server (`Ds2.LightHouseService`) 측 단일 책임**.

**현재 미진입 상태**:
- `ImageCache.CaptionText` 컬럼만 박제, lib unit test 의 `captionGen = noop` 사용으로 항상 NULL (lib 기준 정상). server 측 `captionGen` 활성 wiring 미진입.
- `attachment_read(caption_only=true)` 의미화 의존.

**server 측 잔여 작업** (별 todo 박제 — `todo-lighthouse-kb-server-vlm.md` 가칭):
- `IVlmCaptionProvider` 추상화 (Anthropic / OpenAI / Ollama)
- server 색인 endpoint 에서 `Indexer.ingest(..., captionGen = provider.Generate)` 주입
- `LlmConfig.VlmConfig` — provider / model tier / daily cost cap (parent §3.15.5 MR4)
- invalidation — `CaptionModel` generation tier 변경 시 NULL reset (MR3)
- per-image fail-safe — 이미 `CaptionResult = Captioned | SkippedCaption` DU 박제 (Indexer.fs:99~105) → provider 측 호출 실패 시 `SkippedCaption` 반환

**본 todo scope 결정**: Task 5 는 **scope out** (lib 측 surface 박제 완료 + server 분담 명확). server phase 진입 confirm 후 별 todo 로 분리.

### Task 6 — `attachment_read` image 모드 wiring

**전제**: Task 5 (VLM caption) 가 진행되어야 `caption_only` 가 의미 있음.

**구현** (`AttachmentTools` — server phase Phase S5 에 흡수, parent §4.5 SKIP 정합):
- [ ] `attachment_read(fileId, ref, includeImages=true)` — `ImageReferences.RefLocator = ref` 조건의 `ImageCache.ImageHash` + `StoredPath` → blob bytes → base64 inline content block (Microsoft.Extensions.AI 의 image content abstraction)
- [ ] `attachment_read(fileId, ref, caption_only=true)` — 위 동일 lookup → `ImageCache.CaptionText` 반환. NULL 이면 빈 응답 + `hint = "caption not generated yet"`
- [ ] `attachment_search` 의 `hasImages` 가 실제 값 — 해당 chunk 의 `ImageReferences` row 존재 여부 (현 false hardcode 정정)
- [ ] `5.knowledge-base.md` prompt 룰 — "도면/그림 추궁 시 `caption_only` 우선 → 부족 시 `includeImages`. quota 4000/16000 token 유지"

**본 todo scope**: Task 6 도 **scope out** — server phase Phase S5 (`done-lighthouse-kb-server.md`) 의 책임. server 진입 시점에 정합 확인.

### Task 7 — standalone image 파일 색인 활성 (r4 신설)

**범위**: `.png` / `.jpg` / `.jpeg` / `.gif` / `.webp` 단일 image 파일을 KB 색인 대상으로 활성. 본 todo 본 주제 (OoxmlExtractor embedded image) 와 별 layer — KB 입력 surface 자체의 확장. **PNG/JPEG/GIF/WEBP 4 종만 활성** (BMP/TIFF/SVG/ICO/HEIC 는 Phase 3 backlog — 사용자 결정 미박제).

**전제** = 없음 (Task 0~4 와 독립). Task 5 (VLM caption server provider) 가 진행되어야 caption 채워짐 — provider 미진입 시 `ImageCache.CaptionText = NULL` 로 정상 색인, 다음 색인 시점에 재시도 (parent §3.15.5 MR2 정합).

**실측 진단 SSOT (2026-05-21 turn)**:
- `dotnet run index /f/tmp/i` (Picture1.png 130 KB 단일 파일) 후 `index.db`:
  - `Documents` 0 row / `Chunks` 0 row / `ImageCache` 0 row / `ImageReferences` 0 row / `Meta` 만 stamp (schema_version=5, indexer_version=2.1.0)
  - 사유 = `Classifier.fs:41-50` 의 `rejectedExtensions` 가 `.png` 명시 등재 → `classifyForKb` 가 `Unsupported ".png"` → `Indexer.ingestFile` (`Indexer.fs:201-207`) 의 `Skipped "rejected ext: .png"` 분기

**사용자 결정 박제 (r6 확정 — 5건 모두 결정 완료)**:
- **(A) 활성 확장자 6종 — 확정** — PNG / JPEG / GIF / WEBP / EMF / WMF. BMP/TIFF/SVG/ICO/HEIC 는 보류 (Phase 3 backlog).
- **(B) RefLocator scheme — 확정 옵션 ①** — `RefLocator.fs` 의 기존 관례 (`RefUnit = P / Slide / Sheet` 모두 main 단독 + `Img` 는 sub key 전용) 따라 `RefUnit` DU 에 `Image` case 신설 + `image=N` literal. standalone 파일은 N=1 고정. `formatDisplay` = `"이미지"` literal (slide 의 `"슬라이드 N"` 정합).
- **(C) 8 KB icon 가드 유지 — 확정** — `Indexer.MinImageBytesForIndex = 8192` 그대로. standalone image 도 동일 가드 적용 (8 KB 미만 skip). `ingestImagesIntoStore` 변경 0.
- **(D) standalone EMF/WMF 활성 — 확정** — `OoxmlExtractor.EmfToPng` (Plan 3 helper, `OoxmlExtractor.fs:95-114`) 재사용. `ImageExtractor` 가 직호출 → Metafile→PNG 변환 후 `Format=Png` 박제. 의존성 추가 0.
- **(E) System.Drawing.Common — 확정** — Width/Height parse Windows-only 정합 (Plan 3 `EmfToPng` 와 동일 trade-off).
- **(H) Format ↔ bytes 박제 정합** — `ExtractedImage.Format` 가 `Bytes` 내용과 정확 일치 의무. **변환 매트릭스 (r6 단순화)**:
  - PNG / JPEG / GIF / WEBP → raw 보존, `Format` = raw, `ImageStore.saveBlob` 이 extension 분기 정합
  - EMF / WMF → Metafile→PNG 변환 (Plan 3 `EmfToPng` 재사용), `Format=Png`

**보류 결정 (Phase 3 backlog 박제)**:
- ~~(F) SVG~~ — 보류. raster 변환 (`Svg.Skia` NuGet) + `ImageFormat.Svg` case + VLM provider SVG skip 정책.
- ~~(G) BMP/TIFF~~ — 보류. Bitmap→PNG 재인코딩 분기.

**구현 절차 (r6 사용자 결정 박제 후 확정)**:
- [ ] **(a) `Classifier.fs:41-50` 정정** — `rejectedExtensions` 에서 `.png` / `.jpg` / `.jpeg` / `.gif` / `.webp` 5 종 제거. BMP/TIFF/SVG/ICO/HEIC 는 그대로 유지 (Phase 3 backlog).
- [ ] **(b) `Classifier.fs:19-35` 정정** — `supportedExtensions` 에 6 종 매핑 추가:
  ```
  ".png",   Image
  ".jpg",   Image
  ".jpeg",  Image
  ".gif",   Image
  ".webp",  Image
  ".emf",   Image
  ".wmf",   Image
  ```
- [ ] **(c) `Models.fs:10-17` 정정** — `FileKind` DU 에 `Image` case 추가:
  ```
  type FileKind =
      | Pdf | Docx | Pptx | Xlsx | Text | Markdown | Image
      | Unsupported of ext: string
  ```
  `SqliteStore` 의 DocType ↔ string 매핑 동시 갱신 (`"image"` lowercase).
- [ ] **(d) 신규 `Solutions\Core\Ds2.LightHouse\Extractors\ImageExtractor.fs`** — `IExtractor` 구현:
  - `Supports kind = kind = Image`
  - `Extract (path, ct)`:
    - file bytes 전체 로드 (`File.ReadAllBytes path`).
    - 확장자 분기 (사용자 결정 H 변환 매트릭스):
      - **PNG/JPEG/GIF/WEBP (raw 보존)**: magic byte 검증 — PNG (`89 50 4E 47`) / JPEG (`FF D8 FF`) / GIF (`47 49 46 38`) / WEBP (`52 49 46 46 .. .. .. .. 57 45 42 50`). mismatch 시 빈 결과 + `Log.lighthouse.Warn`. `Format` = raw (Png/Jpeg/Gif/Webp).
      - **EMF/WMF (Metafile→PNG 변환)**: `OoxmlExtractor.EmfToPng` (Plan 3 helper, `OoxmlExtractor.fs:95-114`) 직호출 → PNG bytes 반환. `Format=Png`. 변환 실패 (Metafile constructor throw) 시 빈 결과 + `Log.lighthouse.Warn`. **OoxmlExtractor `EmfToPng` 접근성 의무** — 현재 `static member private` 일 경우 `internal` 또는 `public static member` 로 승격 (Task 0 의 closure 4종 static 승격 패턴 정합). 또는 ImageStore/별 helper 모듈로 분리 — 본 구현 turn 에 결정.
    - `Width` / `Height` = `System.Drawing.Image.FromStream` (Windows) 변환 후 / raw bytes 에서 parse. EMF/WMF 는 변환된 PNG bytes 기준. 실패 시 None.
    - `ExtractedImage` 1 개 박제:
      ```
      { Bytes = bytes
        Format = (Png/Jpeg/Gif/Webp/Png(변환))
        Width = w; Height = h
        RefLocator = "image=1"  // 사용자 결정 B 옵션 ① 확정
        Ordinal = 1 }
      ```
    - `ExtractedDocument { DocType=Image; PageOrSheetCnt=None; Title=Some (Path.GetFileNameWithoutExtension path); Outline=[||]; Segments=[||]; Images=[|단일|] }`
- [ ] **(e) `IExtractor` list 등록** — `Packager.runIngest` (`Solutions\Tools\Ds2.LightHouse.Cli\Packager.fs:109-113`) + 다른 caller (Promaker 측 KbManager / IntegrationTests) 의 extractors list 에 `new ImageExtractor()` 추가.
- [ ] **(f) `Indexer.ingestFile` 검증 — image-only flow** — `extracted.Segments.Length = 0 && extracted.Images.Length = 1` 경로 검증. `Chunker.chunkify [||]` 가 빈 chunks 박제 + `insertChunks` no-op + `ingestImagesIntoStore` 가 단일 image dispatch. parent §6 #8 의 image-only paragraph 정합 그대로 재사용. **코드 변경 0** — 본 회귀 검증만.
- [ ] **(g) icon size 가드 — 동일 적용 (사용자 결정 C 확정)** — `Indexer.fs:101-105` 의 `MinImageBytesForIndex = 8192` 분기 그대로. standalone image 도 8 KB 미만이면 skip + Debug log. **코드 변경 0**.
- [ ] **(h) `RefLocator.fs` 정정 (사용자 결정 B 옵션 ① 확정)** — `RefUnit` DU 에 `Image` case 추가 + `tryParse` / `toStored` / `formatDisplay` 분기 + `unitToken` / `parseUnit` 매핑 (`"image"` literal). parent `RefLocator.fs` EBNF 박제 + `Plan 1` escape policy doc (commit `1e2022d`) 정합. `formatDisplay` 에서 `Image` 분기 = `sprintf "이미지 %s" value` (slide/sheet 와 형태 정합) 또는 단순 `"이미지"` literal (사용자 결정 의무 — 본 구현 turn 박제).
- [ ] **(i) `attachment_read` 정합 (server phase Phase S5)** — `ImageReferences.RefLocator = "image=1"` 매칭이 기존 `includeImages` / `caption_only` 분기에 자동 적용. server 측 추가 변경 의무 검토만 (lib 변경 0 가정 — server 측 박제는 Task 6 흡수).

**테스트** (`ImageExtractorTests.fs` 신설, r6 활성 6 종 정합):
- [ ] 정상 PNG — 1×1 SamplePng (`TestFixtures.fs` 재사용) → Images.Length=1, Format=Png, Width=Some 1, Height=Some 1, RefLocator="image=1", Title=Some "fname".
- [ ] 정상 JPEG — minimal valid JPEG bytes → Format=Jpeg + magic byte 통과.
- [ ] 정상 GIF — minimal valid GIF bytes → Format=Gif.
- [ ] 정상 WEBP — minimal valid WEBP bytes → Format=Webp.
- [ ] **정상 EMF → PNG 변환** — minimal valid EMF metafile bytes → `EmfToPng` 호출 → Format=**Png** (변환 후), Bytes 의 magic byte = PNG signature.
- [ ] **정상 WMF → PNG 변환** — minimal valid WMF metafile bytes → 동일 변환 + Format=Png.
- [ ] magic byte mismatch — `.png` 확장자 + JPEG bytes → Warn + 빈 Images 결과.
- [ ] 0 byte 파일 — empty → 빈 Images.
- [ ] **icon size 가드 동일 적용 (사용자 결정 C 확정)** — 5 KB PNG → `ingestImagesIntoStore` 가 `MinImageBytesForIndex` 미만 skip + `ImageCache` 0 row + Debug log.
- [ ] `RefLocator` round-trip (`RefLocatorTests.fs` 보강) — `image=1` 의 `tryParse >> Option.map toStored = Some "image=1"`. `formatDisplay` 검증 `"이미지 1"`.
- [ ] `Classifier` 분기 (`ClassifierTests.fs` 보강) — `.png` / `.jpg` / `.jpeg` / `.gif` / `.webp` / `.emf` / `.wmf` 모두 `Image` 박제. `.bmp` / `.tiff` / `.svg` / `.ico` / `.heic` 는 여전히 `Unsupported` (Phase 3 backlog 가드).
- [ ] **end-to-end CLI** — `dotnet run index /f/tmp/i` 가 Picture1.png 색인 후 `Documents` 1 row (DocType=Image) + `ImageCache` 1 row + `ImageReferences` 1 row + `Chunks` 0 row.

**예상 분량 (r6 보정)**: F# 신규 코드 **120~170 line** (`Classifier.fs` 7 line + `Models.fs` 1 line + `ImageExtractor.fs` 100~140 line — 6 format 매핑 + magic byte + EMF/WMF `EmfToPng` 호출 분기 + `SqliteStore.fs` 매핑 3 line + `Indexer.fs` 0 (가드 동일) + `RefLocator.fs` Image case 15~25 line + `Packager.fs` 등 caller list 1~3 line) + 테스트 **8~12 Fact**.

**자가 검열 의무**: trigger ② (신규 함수/타입 3+ — `ImageExtractor` class + `FileKind.Image` case + `RefUnit.Image` case) + trigger ⑤ (public API — `Classifier` 분기 정책 변경, `FileKind` DU SSOT 갱신). 단독 commit + sub-agent 검열 의무.

**parent todo `done-lighthouse-kb-index.md` 갱신 (Task 4 와 묶을 수도 있음)**:
- §3.3 (FileKind 매핑 표) 에 `Image` 행 추가
- §3.11 (rejected vs unsupported) 의 image 5 종 정책 변경 박제
- §3.13 (RefLocator 표) 에 `image=N` 행 추가
- §4.3 Phase 2 또는 Phase 2.5 task 체크박스 신설 — "standalone image 파일 색인 활성"

---

## 4. 권장 진입 순서 (r1 갱신)

0. **Task 0 (선행 refactor)** — r1 신설 의무. `OoxmlExtractor.Extract` dispatch + `ExtractWithFailSafe` wrapper + `ImagePartToFormat` helper + closure 4종 static 승격. ~50 line. DOCX 회귀 0. **단독 commit + 자가 검열 sub-agent**.
1. **Task 1 (PPTX)** — Task 0 전제. SlideIdList null guard (r2 Major-1) + SSOT 순서 + CenteredTitle + paragraph break + notes marker + Drawing alias + slide loop ct. **160~240 line + 10~14 Fact** (r2 Minor 7 보정).
2. **Task 2 (XLSX)** — Task 0 전제. `expandSparseRow` (cap + null guard, r2 Minor 2/4) + `SheetStateValues` enum + phonetic ruby filter (r2 Minor 1 단순화) + Row sort + Cell.DataType 6 분기 + 시트명 `#` escape (= / . 안전 r2 Major-2 반론) + **타임라인 좁은 컬럼 필터 (r3, width<1.0 + 빈 값 drop)**. **200~300 line + 16~20 Fact** (r3 +2 Fact).
2.5. **Task 2-extra (Gantt schedule 힌트)** — Task 2 전제. 8 role synonym map + 3단계 normalize + 2-row header concat + score 판정 + role 기반 동적 안내문 prepend + outline label. **~120 line + 6 Fact**. 별도 commit + 자가 검열 sub-agent.
3. **Task 3 (RefLocator regression Fact)** — 코드 변경 0. InlineData 5~8개 + 표시형 3개. ~10 line.
4. **Task 4 (parent todo 갱신)** — r16 박제.
5. **Task 5 (VLM caption)** — server phase 진입 confirm 후 별 todo 로 분리.
6. **Task 6 (attachment_read image 모드)** — server phase Phase S5 흡수.
7. **Task 7 (standalone image 파일 색인, r4 신설)** — Task 0~4 와 독립 진입 가능 (lib 안 완결). 사용자 결정 의무 5건 (확장자 범위 / RefLocator scheme / icon 가드 우회 / EMF-WMF standalone / Width-Height 의존성) 박제 후 진입. **100~150 line + 6~10 Fact**. 단독 commit + 자가 검열 sub-agent.

**0~4 + 7 은 server phase 무관 — parent lib 안에서 완결**. 즉 server 진입 confirm 없이 즉시 진입 가능. **Task 0 단독 commit** 후 Task 1/2 진입 (refactor 와 신규 활성 mix 시 회귀 진단 곤란). Task 2-extra 는 Task 2 commit 후 단독. Task 7 은 Task 0~4 와 무관 — 어느 시점이든 진입 가능 (단 사용자 결정 5건 confirm 우선).

---

## 5. 관련 파일 / 경로 (전수, r1 갱신)

### 수정
- `Solutions\Core\Ds2.LightHouse\Extractors\OoxmlExtractor.fs` — Task 0 (Extract dispatch + ExtractWithFailSafe wrapper + ImagePartToFormat helper + closure 4종 static 승격) + Task 1 (ExtractPptx 신설) + Task 2 (ExtractXlsx + expandSparseRow + columnLetterToIndex + cellRefToColumnLetter + resolveCellValue helper) + `Supports` 분기 확대.
- ~~`RefLocator.fs`~~ — r1 정정: **수정 없음** (이미 일반화 박제). Critical-2. **Task 7 (r4) 추가** — `RefUnit` DU 에 `Image` case 신설 (사용자 결정 B 옵션 ① 가정) + `tryParse` / `toStored` / `formatDisplay` 분기 — **10~20 line 추가**.
- `Solutions\Core\Ds2.LightHouse\Classifier.fs` — **Task 7 (r6)** — `rejectedExtensions` 에서 raster 5 종 (`.png` / `.jpg` / `.jpeg` / `.gif` / `.webp`) 제거 + `supportedExtensions` 에 raster 5 + vector 2 (`.emf` / `.wmf`) = **7 매핑 추가**. **~7 line 변경**.
- `Solutions\Core\Ds2.LightHouse\Models.fs` — **Task 7 (r6)** — `FileKind` DU 에 `Image` case 추가. **1 line 변경**.
- `Solutions\Core\Ds2.LightHouse\SqliteStore.fs` — **Task 7 (r6)** — DocType ↔ string 매핑 (`Image` ↔ `"image"`) 추가. **~3 line 변경**.
- `Solutions\Core\Ds2.LightHouse\Extractors\ImageExtractor.fs` — **Task 7 (r6) 신규 파일** — standalone image 파일 추출. `IExtractor` 구현 + 확장자 분기 (raster 4 raw + EMF/WMF Metafile→PNG 변환) + magic byte 검증 + Width/Height parse. **100~140 line**.
- `Solutions\Core\Ds2.LightHouse\Indexer.fs` — **Task 7 (r6)** — icon 가드 동일 적용 (사용자 결정 C 확정). **코드 변경 0** (회귀 검증만).
- `Solutions\Core\Ds2.LightHouse\Extractors\OoxmlExtractor.fs` — **Task 7 (r6) 부수 정정 의무** — `ConvertMetafileToPng` 의 접근성 `static member private` → `internal` 또는 별 helper 모듈 (예: `MetafileConverter.fs`) 로 분리 — Task 7 구현 turn 에 SSOT 결정. **~3 line 변경 또는 신규 파일 분리**.
- `Solutions\Tools\Ds2.LightHouse.Cli\Packager.fs` — **Task 7 (r4)** — `runIngest` 의 extractors list 에 `ImageExtractor` 추가. **1 line 변경**.
- `Solutions\Tests\Ds2.LightHouse.Tests\OoxmlExtractorTests.fs` — Task 1 PPTX Fact 8~12 + Task 2 XLSX Fact 12~16 + `makePptx*` 5종 + `makeXlsx*` 7종 fixture helper 추가. 기존 `withTempPath` + `makeDocx*` 7 helper 패턴 확장.
- `Solutions\Tests\Ds2.LightHouse.Tests\RefLocatorTests.fs` — InlineData 5~8개 + 표시형 3개 regression guard 추가 (코드 변경 0). **Task 7 (r4) 추가** — `image=1` round-trip Fact 1~2개.
- `Solutions\Tests\Ds2.LightHouse.Tests\ImageExtractorTests.fs` — **Task 7 (r4) 신규 파일** — standalone image 파일 추출 Fact 6~10개 (PNG/JPEG/GIF/WEBP/magic byte mismatch/0 byte/icon size 가드 분기/end-to-end CLI).
- `Solutions\Tests\Ds2.LightHouse.Tests\ClassifierTests.fs` — **Task 7 (r4)** — image 5 종 supportedExtensions 매핑 + BMP/TIFF/SVG/ICO/HEIC 여전히 Unsupported 분기 Fact.
- `Apps\Promaker\Docs\done-lighthouse-kb-index.md` — §3.13 표 + §4.3 Phase 2 체크 + rev 표 r16. **Task 7 (r4)** — §3.3 FileKind 표 + §3.11 rejected vs unsupported 정책 변경 + §3.13 RefLocator 표 (`image=N`) + §4.3 phase task 체크박스 신설.

### 참조용 (수정 없음)
- `Solutions\Core\Ds2.LightHouse\Extractors\PdfExtractor.fs` — image 추출 패턴 참조
- `Solutions\Core\Ds2.LightHouse\Extractors\OoxmlExtractor.fs:95-114` — Task 7 (r6) 의 `EmfToPng` helper SSOT (Plan 3 박제, ImageExtractor 가 호출 의무. 접근성 승격 별도 박제 — §5 수정 항목 참조)
- `Solutions\Core\Ds2.LightHouse\Models.fs` — `FileKind` / `ExtractedImage` / `ExtractedDocument`
- `Solutions\Core\Ds2.LightHouse\ImageStore.fs` — sha256 + blob + upsert (r1 실측 225 line / 13 surface, r0 의 170/9 stale 정정)
- `Solutions\Core\Ds2.LightHouse\Indexer.fs` — `routeExtractor` / `ingestImagesIntoStore` / `captionGen` caller 주입 surface (Task 5 분담 SSOT, line 85/130-131/181)
- `Solutions\Core\Ds2.LightHouse\RefLocator.fs` — `RefUnit = P/Slide/Sheet` + `RefSubKey = Img` 일반화 박제 (Critical-2 검증). r1 코드 변경 0.
- `Solutions\Tests\Ds2.LightHouse.Tests\TestFixtures.fs` — `SamplePng.bytes` + `ExpectedSha256` SSOT (PPTX/XLSX 의 image fixture 도 재사용)
- `Apps\Promaker\Docs\done-lighthouse-kb-server.md` — Task 5/6 의 server phase 분담 결정 시 정합

---

## 6. 주의 사항 (r1 갱신)

1. **CLAUDE.md 자가 검열 trigger 충족 — Task 0 단독 commit 도 의무** — (i) 함수 시그니처 변경 (`Extract` 의 진정한 dispatch), (ii) 신규 static helper 5 개 (ExtractWithFailSafe + ImagePartToFormat + closure 4종 승격), (iii) dispatch / control flow 재작성. Task 0 + Task 1 + Task 2 각각 sub-agent 검열.

2. **commit 정책** — Task 0 / Task 1 / Task 2 / **Task 2-extra (r3)** / Task 3 + Task 4 각각 별도 commit. 사용자 confirm 별도. (memory: `feedback_commit_authorization`)

3. **F# 진입 시점에 `~/.claude/dotnet.md` 지침 준수**.

4. **선호 stack** — log4net `Log.lighthouse.Debug/Warn`, `Ds2.LightHouse.TextEncoding`. JSON 사용처 없음 (binary image bytes 직접 박제).

5. **예외 처리 — r1 m4 XmlException 추가** — DOCX 의 4 종 catch (`FileFormatException` / `OpenXmlPackageException` / `InvalidDataException` / `IOException`) 에 **`System.Xml.XmlException`** 추가 (OpenXml lazy deferred parsing 시점 발생 가능). Task 0 의 `ExtractWithFailSafe` wrapper 가 5 종 통합. per-image fail-safe (M2 결론) — image 단위 try/catch + log + skip.

6. **line ending / 인코딩** — F# = LF + UTF-8 (BOM 없음). 본 todo 파일 = UTF-8 (BOM 없음).

7. **OoxmlExtractor.Supports 가 false → routing 누락** — `Indexer.routeExtractor` 의 첫 매칭 정책 정합 확인. Pptx/Xlsx 가 다른 extractor 에 fallback 되지 않도록 OoxmlExtractor 가 단독 책임. grep `routeExtractor` 로 진입 시 재확인.

8. **DOCX 의 `image-only paragraph` 패턴 (s6-r21) — r1 m3 박제 완료 확인** — `Indexer.fs:116-117` 의 ChunkId 매핑 분기가 None 케이스 정상 처리 박제 완료. PPTX 의 image-only 슬라이드 / XLSX 의 image-only 시트도 동일 정합 (segment 미박제 + image 박제 + ChunkId=None).

9. **테스트 fixture — 프로그램적 생성 SSOT (r1 Critical-3 정정, r2 Minor 8 정리)** — 정적 fixture 폴더 / fsproj `<None Include>` / `CopyToOutputDirectory` 패턴 **부재 (실측 검증)**. PPTX/XLSX 도 `PresentationDocument.Create` / `SpreadsheetDocument.Create` + `makePptx*` / `makeXlsx*` helper 의 **프로그램적 생성 only**. 정적 fixture 박제 **금지** — git history 깔끔 + dotnet pack 정합. (r0 의 정적 fixture 패턴 박제는 hallucination 으로 검증 — r2 에서 marker 문구 자체도 정리하여 향후 reviewer 의 stale 오인 회피.)

10. **fixture helper 양식** — 기존 `OoxmlExtractorTests.fs:23` `makeDocx` 패턴 그대로:
    ```
    let private makePptx (path: string) =
        use doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation)
        ... AddPresentationPart + Presentation + SlideIdList + SlideMasterPart + SlideLayoutPart + SlidePart ...
    ```
    `withTempPath ".pptx"` / `withTempPath ".xlsx"` 로 임시 path 발급.

11. **MEMORY.md `## Project` 등록** — Task 0+1+2 commit 직후 본 todo 항목 메모리 등록 검토 (parent `lighthouse-phase1-lib-tests-done.md` 패턴).

12. **본 todo 의 line 박제 (예: `OoxmlExtractor.fs:340-362` `Extract` dispatch / `Indexer.fs:85` `captionGen` / `RefLocator.fs` DU) stale 위험** — 진입 시 grep 으로 재확인. 가능한 곳은 symbol 기반 (`OoxmlExtractor.Extract` interface 구현) 으로 약화.

13. **r1 검증 안 한 외부 출처 (R2 outlier 3건)** — `expandSparseRow` (Critical-4) / `SlideIdList` ordering (Critical-5) / `SheetStateValues` enum (Critical-6) 은 MS Learn / ECMA-376 공식 출처 강 근거이나 본 lib code 실측은 아직 미진행. **Task 1/2 진입 시점에 첫 fixture 작성 후 의도적 회귀 테스트** (sparse row / reordered slide / hidden sheet) 로 외부 출처 정확성 재확인 의무.

14. **r0 → r1 review 외부 reviewer 통계** — Critical 6 (R1+R3 2/5 + R2 외부출처 3 + R1+R4+R5 3/5) + Major 18 (R2 OOXML SDK 11 + R3 4 + R4 7 — 일부 중복) + Minor 8 = **32건 반영**. 검증 통과 outlier (R2 단독 발견) 5건 모두 채택 — 외부 docs cross-validation 강.

15. **r3 Gantt 시트 진단 출처** — `/f/tmp/f/4-1. SV_SIDE_조립작업서_240328.xlsx` 실측 (sheet 13개, 호기별 작업서 9개가 Gantt 형식, `Column.Width=0.75 × 72개` 타임라인, bar = fill style only, 데이터 컬럼 SSOT = NO/SYM/작업내역/시작/시간/누계). Phase 4 backlog 분리: (i) fill style 기반 bar range 복원 (ii) 시트 PNG render + VLM caption (iii) 사용자 정의 synonym alias config. Phase 3 backlog 신설: 셀 메모 (`comments1.xml`) 색인 — 380 KB 분량의 작업자 주의사항.

---

## 7. 다음 세션 첫 행동 권장 (r1 갱신)

1. **본 todo (r1) + parent `done-lighthouse-kb-index.md` 의 §0 / §3.13 / §4.3 Phase 2 + r10~r15 박제** 동시 정독.
2. 진입 시 grep / 사실 재확인:
   - `OoxmlExtractor.Extract` dispatch 상태 (`OoxmlExtractor.fs:340-362` 의 `DocType=Docx` hardcode 4 arm — Critical-1)
   - `RefLocator.fs` 의 `RefUnit` DU 일반화 (Critical-2 검증 — 변경 0 확정)
   - Fixtures 폴더 부재 + `withTempPath` + `makeDocx*` 7 helper SSOT (Critical-3 검증)
   - `Indexer.fs` 의 `captionGen` surface 박제 (line 85/130-131/181 — Task 5 분담 SSOT)
   - `Models.fs` 의 `FileKind` DU 에 `Pptx` / `Xlsx` case 존재 확인
   - `Indexer.routeExtractor` 첫 매칭 정책 (Pptx/Xlsx 가 OoxmlExtractor 에 위임되는지)
3. **Task 0 (선행 refactor) 진입** — `Extract` dispatch + `ExtractWithFailSafe` (5 종 catch incl. XmlException) + `ImagePartToFormat` + closure 4종 static 승격. DOCX 회귀 0. **단독 commit + 자가 검열 sub-agent**.
4. **Task 1 (PPTX) 진입** — `SlideIdList` ordering (Critical-5) + Title+CenteredTitle EnumValue (M4) + paragraph break `\n` (M5) + `--- 노트 ---` marker (M10) + title 부재 fallback "슬라이드 N" (M11). fixture 5 종 (정상 / image-only / 빈 pptx / cross-slide dedup / CenteredTitle).
5. **Task 2 (XLSX) 진입** — `expandSparseRow` (Critical-4) + `SheetStateValues` enum (Critical-6) + phonetic ruby filter (M2) + Row.OrderBy(RowIndex) (M3) + Cell.DataType 6 분기 Str/Error (M1) + `cell.CellValue` null guard (M14) + 시트명 `#` Warn skip (M18) + **`narrowColIndexes` 좁은 컬럼 필터 (r3, width<1.0 + 빈 값 drop)**. fixture 8 종 (r3 +1).
5.5. **Task 2-extra (Gantt 힌트) 진입** — Task 2 commit 후 단독. 8 role synonym map + `normalizeHeader` (공백/괄호/한자) + 2-row header concat + `Map<int,Role>` 빌드 + score≥3 AND start/dur/cum≥2 판정 + role 기반 동적 preamble prepend + outline label `[Gantt schedule]`. fixture 3 종 + Fact 6.
6. **Task 3 (RefLocator regression Fact)** — 코드 변경 0. InlineData 5~8개 + 표시형 3개.
7. **Task 4 (parent todo 갱신)** — §3.13 표 행 2 추가 + §4.3 Phase 2 체크 + rev r16 박제.
8. commit message (각 Task 별 별도, 4줄 이내):
   - Task 0: "[xlsx] Phase 2 refactor: OoxmlExtractor 진정 dispatch + ExtractWithFailSafe + ImagePartToFormat + closure static 승격 (DOCX 회귀 0)"
   - Task 1: "[xlsx] Phase 2: pptx activation + 내부 image 색인 — SlideIdList SSOT + paragraph break + notes marker (slide=N + slide=N#img=M)"
   - Task 2: "[xlsx] Phase 2: xlsx activation + 내부 image 색인 — expandSparseRow + SheetStateValues + 수식/hidden/merged/빈행 간편 정책 + 좁은컬럼 필터 (sheet=<name> + sheet=<name>#img=M)"
   - Task 2-extra: "[xlsx] Phase 2: xlsx Gantt schedule 힌트 — 8 role synonym + normalize + score 판정 + 동적 preamble (산업 .xlsx 작업일정표 색인 지원)"
   - Task 3: "[xlsx] RefLocator regression: slide/sheet × img sub-key cartesian Fact (Critical-2 보호)"
   - Task 4: "[xlsx] todo-lighthouse-kb-index r16: PPTX/XLSX 활성 박제 + Phase 3 backlog 분리"
9. Task 0~2-extra 완료 후 **server phase 진입 confirm 받기** — Task 5 (VLM caption) / Task 6 (attachment_read image 모드) 의 server 측 wiring 별 todo 분리.
