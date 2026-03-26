# Ds2.IOList Export 규격서

> GenerationResult → CSV / Excel 변환 규격

**Version**: 2.0
**Last Updated**: 2026-03-27

---

## 1. 개요

### 범위

이 문서는 `Pipeline.generate`가 반환하는 `GenerationResult`를 CSV와 Excel 파일로 내보내는 규격을 정의한다.
신호 생성 로직(SPECIFICATION.md)은 다루지 않는다.

### 현재 상태

| 기능 | 상태 |
|------|------|
| IO CSV (Legacy 6컬럼) | ✅ 구현 완료 |
| Dummy CSV (Legacy 5컬럼) | ✅ 구현 완료 |
| IO CSV (확장 컬럼) | 📋 Phase 1 |
| Dummy CSV (확장 컬럼) | 📋 Phase 1 |
| Excel 단순 테이블 | 📋 Phase 2 |

### 입력 계약

Export의 유일한 입력은 `GenerationResult`다.

```fsharp
type GenerationResult = {
    IoSignals: SignalRecord list
    DummySignals: SignalRecord list
    Errors: GenerationError list
    Warnings: string list
}

type SignalRecord = {
    VarName: string       // "W_S301_Q_RBT1_HOME_POS"
    DataType: string      // "BOOL"
    Address: string       // "%IW3070.0"
    IoType: string        // "IW", "QW", "MW"
    Category: string      // "RBT"
    Comment: string option
    FlowName: string      // "S301"
    WorkName: string      // "S301.RBT"
    CallName: string      // "RBT1.HOME_POS"
    DeviceName: string    // "RBT1"
}
```

---

## 2. CSV Export

### 2.1 공통 규칙

- 인코딩: UTF-8 with BOM
- 줄바꿈: CRLF
- RFC 4180 quoting: 값에 `,`, `"`, 줄바꿈 포함 시 `"` 감싸기
- `"` 자체는 `""` 로 escape
- null/None → 빈 문자열

### 2.2 Legacy 모드 (현재 구현)

기존 시스템 연동용. 현재 `Pipeline.exportIoList` / `exportDummyList`가 이 포맷을 사용한다.

**IO CSV 컬럼 (6개)**:

| # | 컬럼명 | 원본 | 예시 |
|---|--------|------|------|
| 1 | var_name | VarName | W_S301_I_RBT1_HOME_POS |
| 2 | data_type | DataType | BOOL |
| 3 | address | Address | %IW3070.0 |
| 4 | io_type | IoType | IW |
| 5 | category | Category | RBT |
| 6 | comment | Comment | |

**Dummy CSV 컬럼 (5개)**:

| # | 컬럼명 | 원본 | 예시 |
|---|--------|------|------|
| 1 | var_name | VarName | W_S301_M_READY |
| 2 | data_type | DataType | BOOL |
| 3 | address | Address | %MW3000.0 |
| 4 | category | Category | STN |
| 5 | comment | Comment | |

### 2.3 확장 모드 (Phase 1)

DS2 모델 추적 정보를 포함하는 확장 포맷.

**IO CSV 컬럼 (11개)**:

| # | 컬럼명 | 원본 | 예시 |
|---|--------|------|------|
| 1 | var_name | VarName | W_S301_I_RBT1_HOME_POS |
| 2 | data_type | DataType | BOOL |
| 3 | address | Address | %IW3070.0 |
| 4 | io_type | IoType | IW |
| 5 | category | Category | RBT |
| 6 | flow_name | FlowName | S301 |
| 7 | work_name | WorkName | S301.RBT |
| 8 | call_name | CallName | RBT1.HOME_POS |
| 9 | device_name | DeviceName | RBT1 |
| 10 | direction | (derived) | Input |
| 11 | comment | Comment | |

`direction` 도출 규칙:

- IoType이 `IW` → `Input`
- IoType이 `QW` → `Output`
- IoType이 `MW` → `Memory`

**Dummy CSV 컬럼 (9개)**:

| # | 컬럼명 | 원본 | 예시 |
|---|--------|------|------|
| 1 | var_name | VarName | W_S301_M_READY |
| 2 | data_type | DataType | BOOL |
| 3 | address | Address | %MW3000.0 |
| 4 | io_type | IoType | MW |
| 5 | category | Category | STN |
| 6 | flow_name | FlowName | S301 |
| 7 | work_name | WorkName | S301.STN |
| 8 | call_name | CallName | LATCH_1ST.RET |
| 9 | comment | Comment | |

### 2.4 CSV row 예시

IO (확장):
```csv
var_name,data_type,address,io_type,category,flow_name,work_name,call_name,device_name,direction,comment
W_S152_I_RBT4_HOME_POS,BOOL,%IW3210.0,IW,RBT,S152,S152.RBT,RBT4.HOME_POS,RBT4,Input,
W_S152_Q_RBT4_HOME_POS,BOOL,%QW3210.0,QW,RBT,S152,S152.RBT,RBT4.HOME_POS,RBT4,Output,
```

Dummy (확장):
```csv
var_name,data_type,address,io_type,category,flow_name,work_name,call_name,comment
W_S152_M_READY,BOOL,%MW3000.0,MW,STN,S152,S152.STN,LATCH_1ST.RET,
```

### 2.5 정렬 규칙

CSV 출력 row 정렬:

1. IoType 우선순위: `IW` → `QW` → `MW`
2. Address의 word 번호 (숫자 비교)
3. Address의 bit 번호 (숫자 비교)

### 2.6 파일명 규칙

| 종류 | 파일명 패턴 | 예시 |
|------|------------|------|
| IO (Legacy) | `{stem}_io.csv` | `iolist_io.csv` |
| Dummy (Legacy) | `{stem}_dummy.csv` | `iolist_dummy.csv` |
| IO (확장) | `{stem}_io_ext.csv` | `iolist_io_ext.csv` |
| Dummy (확장) | `{stem}_dummy_ext.csv` | `iolist_dummy_ext.csv` |

`stem`이 비어 있으면 `iolist`를 기본값으로 사용한다.

---

## 3. Excel Export (Phase 2)

### 3.1 라이브러리

ClosedXML (NuGet: `ClosedXML`) 사용. .fsproj에 추가 필요:

```xml
<PackageReference Include="ClosedXML" Version="0.104.*" />
```

### 3.2 출력 구조

하나의 `.xlsx` 파일에 아래 시트를 생성한다:

| 순서 | 시트명 | 내용 |
|------|--------|------|
| 1 | IO | IO 신호 테이블 |
| 2 | Dummy | Dummy 신호 테이블 |
| 3 | Summary | 생성 요약 정보 |

### 3.3 IO 시트

**헤더 (Row 1)**:

| 컬럼 | A | B | C | D | E | F | G | H | I | J | K |
|------|---|---|---|---|---|---|---|---|---|---|---|
| 헤더 | No | VarName | DataType | Address | IoType | Direction | FlowName | WorkName | CallName | DeviceName | Comment |

**데이터 (Row 2~)**:

- `No`: 1부터 순번
- 나머지: SignalRecord 필드 직접 매핑
- Direction: IoType에서 도출 (2.3의 direction 규칙과 동일)

**서식**:

| 요소 | 스타일 |
|------|--------|
| 헤더 행 | Bold, 배경 연녹색 (#C6EFCE), 하단 테두리 |
| 데이터 행 | 기본 글꼴 (맑은 고딕 10pt) |
| IW 행 배경 | 연한 청록 (#E2EFDA) |
| QW 행 배경 | 연한 황색 (#FFF2CC) |
| 열 너비 | AutoFit |
| 필터 | AutoFilter 적용 |
| Freeze | 1행 고정 (헤더 행) |

### 3.4 Dummy 시트

**헤더 (Row 1)**:

| 컬럼 | A | B | C | D | E | F | G | H | I |
|------|---|---|---|---|---|---|---|---|---|
| 헤더 | No | VarName | DataType | Address | IoType | FlowName | WorkName | CallName | Comment |

**서식**: IO 시트와 동일 규칙. MW 행 배경은 연한 회색 (#F2F2F2).

### 3.5 Summary 시트

| 행 | 내용 |
|----|------|
| 1 | **IO List Summary** (제목, Bold 16pt, 병합 A1:D1) |
| 3 | `IO 신호 수:` / 값 |
| 4 | `Dummy 신호 수:` / 값 |
| 5 | `에러 수:` / 값 |
| 6 | `경고 수:` / 값 |
| 8 | **Warnings** (소제목, Bold) |
| 9~ | 경고 메시지 목록 (있는 경우) |

### 3.6 파일명

- `{stem}_iolist.xlsx`
- stem 기본값: `iolist`

---

## 4. Export 옵션

### 4.1 타입 정의

```fsharp
type ExportFormat =
    | CsvLegacy      // 현재 구현된 6/5 컬럼
    | CsvExtended    // 확장 컬럼
    | Excel          // .xlsx

type ExportOptions = {
    Format: ExportFormat
    OutputDirectory: string
    FileStem: string           // 기본값: "iolist"
    Overwrite: bool            // 기본값: true
}
```

### 4.2 API 확장

기존 Pipeline 모듈에 추가:

```fsharp
module Pipeline =
    // 기존 (유지)
    val generate: DsStore -> string -> GenerationResult
    val exportIoList: GenerationResult -> string -> Result<unit, string>
    val exportDummyList: GenerationResult -> string -> Result<unit, string>

    // 확장 CSV (Phase 1)
    val exportIoListExtended: GenerationResult -> string -> Result<unit, string>
    val exportDummyListExtended: GenerationResult -> string -> Result<unit, string>

    // Excel (Phase 2)
    val exportToExcel: GenerationResult -> string -> Result<unit, string>

    // 통합 (Phase 2)
    val export: GenerationResult -> ExportOptions -> Result<string list, string>
```

통합 `export` 함수는 생성된 파일 경로 목록을 반환한다.

### 4.3 IoListGeneratorApi 확장

```fsharp
type IoListGeneratorApi() =
    // 기존 (유지)
    member _.Generate: DsStore * string -> GenerationResult
    member _.ExportIoList: GenerationResult * string -> Result<unit, string>
    member _.ExportDummyList: GenerationResult * string -> Result<unit, string>

    // 추가
    member _.ExportIoListExtended: GenerationResult * string -> Result<unit, string>
    member _.ExportDummyListExtended: GenerationResult * string -> Result<unit, string>
    member _.ExportToExcel: GenerationResult * string -> Result<unit, string>
    member _.Export: GenerationResult * ExportOptions -> Result<string list, string>
```

---

## 5. 에러 처리

### 5.1 Export 전 검증

Export를 시작하기 전 아래를 확인한다:

| 검증 | 동작 |
|------|------|
| IoSignals + DummySignals 모두 빈 리스트 | `Error "No signals to export"` 반환 |
| OutputDirectory 존재하지 않음 | 디렉토리 자동 생성 시도 |
| 파일 이미 존재 + Overwrite=false | `Error "File already exists: ..."` 반환 |

### 5.2 CSV 에러

- 파일 쓰기 실패 → `Error` with 경로와 예외 메시지
- 인코딩 문제 → StreamWriter가 처리 (UTF-8 BOM 고정)

### 5.3 Excel 에러

- ClosedXML workbook 생성 실패 → `Error` with 예외 메시지
- 파일 저장 실패 (잠김 등) → `Error` with 경로와 예외 메시지

---

## 6. 구현 파일 구조

### 현재 (변경 없음)

```
Ds2.IOList/
├── Types.fs              # SignalRecord, GenerationResult 등
├── AddressConfig.fs      # 주소 설정 파서
├── TemplateParser.fs     # 템플릿 파서
├── ContextBuilder.fs     # DS2→GenerationContext 변환
├── SignalGenerator.fs    # 신호 생성 엔진
└── Pipeline.fs           # 공개 API + CSV export
```

### Phase 1 추가

```
├── CsvExporter.fs        # CSV export 전담 모듈 (Pipeline.fs에서 분리)
```

Pipeline.fs에 인라인된 CSV 로직을 `CsvExporter` 모듈로 분리한다.

```fsharp
module CsvExporter =
    val exportIoLegacy: SignalRecord list -> string -> Result<unit, string>
    val exportDummyLegacy: SignalRecord list -> string -> Result<unit, string>
    val exportIoExtended: SignalRecord list -> string -> Result<unit, string>
    val exportDummyExtended: SignalRecord list -> string -> Result<unit, string>
```

### Phase 2 추가

```
├── ExcelExporter.fs      # Excel export 전담 모듈
├── ExportTypes.fs        # ExportFormat, ExportOptions
```

.fsproj 컴파일 순서:

```xml
<Compile Include="Types.fs" />
<Compile Include="AddressConfig.fs" />
<Compile Include="TemplateParser.fs" />
<Compile Include="ContextBuilder.fs" />
<Compile Include="SignalGenerator.fs" />
<Compile Include="ExportTypes.fs" />
<Compile Include="CsvExporter.fs" />
<Compile Include="ExcelExporter.fs" />
<Compile Include="Pipeline.fs" />
```

---

## 7. 수용 기준

### CSV Legacy

- [x] `io.csv` 생성, 6컬럼, row 수 = IoSignals 수
- [x] `dummy.csv` 생성, 5컬럼, row 수 = DummySignals 수
- [x] UTF-8 BOM
- [x] 빈 Comment는 빈 문자열

### CSV 확장 (Phase 1)

- [ ] `io_ext.csv` 생성, 11컬럼
- [ ] `dummy_ext.csv` 생성, 9컬럼
- [ ] FlowName/WorkName/CallName/DeviceName 값이 SignalRecord와 일치
- [ ] direction 값이 IoType에서 올바르게 도출
- [ ] RFC 4180 quoting 적용 (콤마/따옴표/줄바꿈 포함 값)
- [ ] row가 IoType → Word → Bit 순으로 정렬

### Excel (Phase 2)

- [ ] `.xlsx` 파일 생성 (ClosedXML)
- [ ] IO 시트: 헤더 + 데이터 row 수 = IoSignals 수
- [ ] Dummy 시트: 헤더 + 데이터 row 수 = DummySignals 수
- [ ] Summary 시트: 생성 요약 정보 표시
- [ ] AutoFilter, FreezePane 적용
- [ ] IoType별 행 배경색 구분
- [ ] 열 너비 AutoFit
