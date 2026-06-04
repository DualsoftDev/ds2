# Mermaid + IoTag 페어 임포트 — 설계 초안

> 위치: `Ds2.Mermaid/Docs/IoTagPair-DesignDraft.md`
> 상태: **초안 (Draft)** · 2026-06-05
> 범위: 생성형 LLM 으로 만든 mermaid 모델에 PLC IO 태그를 결합하여 ProMaker / Ds2 Store 로 임포트하는 파이프라인 설계

---

## 0. 임포트 진입점 (단일화)

**Mermaid 임포트는 “프로젝트 열기(Open Project)” 단일 경로로만 수행한다.**

- ✅ **프로젝트 열기 다이얼로그** — `*.sdf / *.json / *.aasx / *.md / *.mmd / *.yaml / *.yml` 통합 필터에서 `*.mmd` 선택 시 mermaid 임포트
- ❌ **Flow 컨텍스트 메뉴 “Mermaid 불러오기...”** — **제거**
- ❌ Flow 우클릭, Work 우클릭, 캔버스 드롭존 등 **모든 sub-level 임포트 진입점 제거**

### 0.1 제거 사유

| 사유 | 설명 |
|------|------|
| 진입점 분산 → 사용자 혼동 | 같은 mermaid 라도 어디서 열었는지에 따라 결과(레벨) 가 달라짐 — “Flow 에서 열면 Work 단, 프로젝트에서 열면 System 단” 같은 암묵 규칙은 운영 비용 ↑ |
| IO 태그 페어 일관성 | iotag.json 페어는 mermaid 파일 옆에 위치 — “파일 단위 임포트” 전제. 부분 임포트(Flow 단) 와 모순 |
| 코드 단순화 | `ImportLevel = FlowLevel / WorkLevel` 분기 제거 → `SystemLevel` (=프로젝트 전체) 하나만 유지 |
| ProMaker UI/UX | 우클릭 메뉴 9 줄 → 8 줄로 단축. 시각 잡음 감소 |

### 0.2 임포트 동작

1. 사용자가 **파일 → 열기** → 파일 다이얼로그에서 `*.mmd` 선택
2. 새 프로젝트 컨텍스트로 임포트 (또는 현재 프로젝트 교체 — confirm 후)
3. 동일 디렉터리에 `<stem>.iotag.json` 존재 시 자동 페어링
4. 단일 Active System “Main” + Flows + Works + Calls 생성

### 0.3 ProMaker 변경 사항

| 위치 | 작업 |
|------|------|
| `Promaker/Views/.../FlowContextMenu.xaml` | `Mermaid 불러오기...` 메뉴 항목 삭제 |
| `Promaker/ViewModels/.../FlowContextMenuViewModel.cs` | `ImportMermaidIntoFlowCommand` 삭제 |
| `Promaker/Services/FlowMermaidImport.cs` (있다면) | 파일 삭제 또는 deprecated 마킹 |
| `Promaker/Presentation/FileExtensions.cs` | 변경 없음 (`Mermaid = .md`, `MermaidAlt = .mmd` 유지) |
| `Promaker/Services/FileTypeProbe.cs` | `IsMermaid` 가 `.md` + `.mmd` 둘 다 인식 (적용 완료) |
| `Promaker/Windows/MainWindow/FileOpenDialog.cs` | 필터 문자열에 `*.mmd` 추가 — `Mermaid Files (*.md;*.mmd)` |
| `Promaker/Ds2.Editor.Import` 진입점 | `ImportLevel.FlowLevel` / `WorkLevel` 호출자 제거, `SystemLevel` 만 유지 |

### 0.4 Ds2.Mermaid 라이브러리 변경

- `Ds2.Mermaid/Import/Targets/MapperTargets.fs`
  - `mapToFlow` / `mapToFlowFlat` / `mapToWork` — **deprecated** (호출자 제거 후 후속 PR 에서 삭제)
  - `mapToSystem` 만 활성 유지
- `Ds2.Mermaid/Import/Targets/MapperTargetPlanning.fs` `buildPreview`
  - `FlowLevel` / `WorkLevel` 분기 삭제, `SystemLevel` 만 유지

---

## 1. 배경 & 문제

현 `Ds2.Mermaid` Importer 는 **구조(System/Flow/Work/Call)** 만 생성한다. PLC 실제 주소(`%IX0.0.0`, `F00099`, `K02408` 등)는 어디에도 매핑되지 않아 ProMaker 캔버스의 Call 노드가 “이름만 있는 박스” 로 끝난다.

한편 LLM (Claude / GPT) 으로 생성하는 mermaid 는 다음 한계가 있다:

| 항목 | LLM 강점 | LLM 약점 |
|------|---------|---------|
| 의미 구조 (`스토퍼.전진`) | ◎ | — |
| 자연어 코멘트 | ◎ | — |
| 실제 PLC 주소 | — | × (hallucination — 가짜 `%IX0.0.0` 발생) |
| 데이터타입/길이 | — | △ (추측 위주) |

→ **구조와 IO 매핑은 출처가 다르다.** 한 파일에 묶으면 LLM 이 환각한 주소가 그대로 ProMaker 에 흘러간다.

## 2. 설계 결정 — 2파일 페어

```
project.mmd           # mermaid (LLM 생성, 구조만)
project.iotag.json    # IO 매핑 (brownfield 추출 + 수동 보정)
```

### 2.1 책임 분담

| 파일 | 생성자 | 신뢰원 | 변경 빈도 |
|------|--------|--------|----------|
| `*.mmd` | LLM (ReverseChatOrchestrator) | 사용자 자연어 + 기존 모델 | 중 |
| `*.iotag.json` | Brownfield Extractor (XgwxLd / LSXGT / Fuji) → 보정 | PLC 프로젝트 파일 (xgwx/xg5/...) | 저 |

### 2.2 장단점 요약

**페어 분리 (채택)**

- ✅ LLM hallucination 0 (IO 는 추출기 결과만 사용)
- ✅ 구조/IO 독립 갱신 — 구조 다시 그려도 IO 재바인딩 가능
- ✅ 같은 mermaid + 다른 iotag = 다른 PLC 로 재사용
- ✅ mermaid 문법 무변경
- ⚠️ 두 파일 동기화 책임 필요 → `mermaidRef` + `callPath` 매칭 규약으로 해결

**1파일 통합 (기각)**

- ❌ LLM 토큰 비용 ↑ (구조+태그 동시 추론)
- ❌ 환각 위험
- ❌ mermaid 파서 확장 필요 (비표준 syntax)
- ✅ 단일 파일 관리 (only)

## 3. `*.iotag.json` 스키마

### 3.1 최소 필드

```jsonc
{
  "version": 1,
  "mermaidRef": "project.mmd",         // 동일 디렉터리 mermaid 파일명
  "generatedAt": "2026-06-05T12:00:00+09:00",
  "source": "XgwxLdExtractor",          // brownfield 추출기 식별
  "vendor": "LS_XGK",                    // FUJI / LS_XGK / LS_XGI / Rockwell / Omron / Siemens
  "tags": [
    {
      "callPath": "Main.공정1·투입.실린더.ExtStart.ADV",
      "address": "%IX0.0.0",
      "symbol": "_EXT_START",
      "comment": "외부시작 전진",
      "dataType": "BOOL",
      "direction": "Input"             // Input / Output / Memory
    }
  ]
}
```

### 3.2 `callPath` 규약

```
<SystemName>.<FlowName>.<WorkName>.<DeviceAlias>.<ApiName>
```

- 구분자: `.` (Call label 의 `Device.Api` 와 충돌 → escape 규칙 §3.3)
- `SystemName`: implicit System 인 경우 항상 `Main`
- `FlowName`/`WorkName`: mermaid 의 `["..."]` displayName 사용
- `DeviceAlias.ApiName`: Call 노드의 `splitCallName` 결과와 일치

### 3.3 충돌 회피

- displayName 에 `.` 포함 가능(`공정 1 · 투입` 의 가운뎃점 `·` 은 OK, ASCII `.` 만 escape 대상)
- escape: 백슬래시 → `Main.공정\.A.실린더.ExtStart.ADV`
- 매칭 알고리즘은 escape-aware split (regex `(?<!\\)\.`)

### 3.4 옵션 필드 (확장)

```jsonc
{
  "tags": [{
    "callPath": "...",
    "address": "...",
    "addressAlt": { "raw": "00.00.00", "normalized": "%IX0.0.0" },
    "scaling": { "min": 0, "max": 27648, "engMin": 0.0, "engMax": 100.0, "unit": "bar" },
    "alarm": { "high": 9.5, "low": 0.5 },
    "tags": ["safety", "interlock"]    // 자유 라벨
  }]
}
```

## 4. 매핑 파이프라인

> **단일 진입점**: 파일 → 열기 → `*.mmd` 선택. `mapToSystem` 만 호출됨.

```
┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│  *.mmd       │    │ *.iotag.json │    │ Brownfield   │
│  (LLM gen)   │    │  (pair)      │    │ raw files    │
└──────┬───────┘    └──────┬───────┘    └──────┬───────┘
       │                   │                   │
       │ ParseMermaid      │ ParseIoTag        │ Extract → NormalizedTag[]
       ▼                   ▼                   │
┌────────────────────────────────────┐         │
│  ImportPlan (mapToSystem only)      │         │
│  + IoTagSidecar.Bind (Ds2.Mermaid)  │◄────────┘ (옵션: pair 미존재 시 자동 매칭)
└──────────────┬─────────────────────┘
               │ applyDirect
               ▼
        ┌──────────────────────┐
        │  DsStore (구조만)     │
        │  + IoTagSidecar      │  ← Ds2.Mermaid 내부 in-memory registry
        │    (Ds2.Core 무수정)  │
        └──────────────────────┘
```

### 4.1 IoTagSidecar — Ds2.Core 무수정 전략

새 `ImportPlanOperation` 추가 없이 `Ds2.Mermaid` 레이어 내부에 sidecar registry 만 둔다.

```fsharp
// Ds2.Mermaid/Import/IoTagSidecar.fs (신규)
type IoTagRecord = {
    Address: string
    Symbol: string option
    Comment: string option
    DataType: string option
    Direction: IoDirection option
}

[<RequireQualifiedAccess>]
module IoTagSidecar =
    let private store = Dictionary<Guid, IoTagRecord>()
    let bind (callId: Guid) (rec_: IoTagRecord) = store.[callId] <- rec_
    let tryGet (callId: Guid) =
        match store.TryGetValue callId with true, v -> Some v | _ -> None
    let clear () = store.Clear()
    let all () = store :> IReadOnlyDictionary<_, _>
```

### 4.2 신규 모듈

| 파일 (제안) | 책임 |
|------------|------|
| `Ds2.Mermaid/Import/IoTagSidecar.fs` | in-memory registry (Ds2.Core 무수정) |
| `Ds2.Mermaid/Import/IoTagPairLoader.fs` | `*.iotag.json` 파싱 → `IoTagRecord seq` |
| `Ds2.Mermaid/Import/IoTagBinder.fs` | `callPath` ↔ `CallId` 매칭, escape-aware split |

### 4.3 매칭 알고리즘 (의사코드)

```fsharp
let bindIoTags
    (callIndex: Dictionary<string (* callPath *), Guid>)
    (iotags: IoTagRecord seq)
    (warnings: ResizeArray<string>) =
    for (path, rec_) in iotags do
        match callIndex.TryGetValue path with
        | true, callId -> IoTagSidecar.bind callId rec_
        | _ -> warnings.Add($"IoTag 매칭 실패: {path}")
```

`callIndex` 는 `mapToSystem` 내부에서 Call 생성과 동시에 구축한다 (별도 패스 X).

## 5. 페어 파일 발견(Discovery) 규칙

ProMaker 가 **파일 → 열기** 다이얼로그에서 `project.mmd` 를 받았을 때:

1. **동일 디렉터리 같은 stem** — `project.iotag.json` 우선 사용
2. `*.mmd` 헤더 주석(`%% iotag: ./custom.json`) 으로 경로 지정 가능
3. 둘 다 없으면: brownfield 추출기 결과를 메모리 상에서 자동 페어링 (file system 미저장)
4. 페어 부재 + brownfield 부재 → 기존 동작(구조만)

## 6. LLM 측 (ReverseChatOrchestrator) 변경

- 시스템 프롬프트에서 **“IO 주소를 추론하지 말 것”** 강제
- mermaid 안에는 `Device.Api` + 한국어 코멘트만 허용 (`<br>` 두 번째 줄)
- 출력 파일 확장자 `*.mmd` 로 통일
- IO 주소가 필요한 컨텍스트가 있으면 별도 채팅 라운드에서 `*.iotag.json` 만 생성하는 모드 추가 — 단, **brownfield 추출 결과를 컨텍스트로 강제 주입** 후 (LLM 은 매칭만 수행)

## 7. UI/UX (ProMaker)

### 7.1 추가

- Call 노드 우측 패널: Address / Symbol / Comment / DataType (IoTagSidecar 조회)
- 페어 누락 시 노드 테두리 점선 + “IO 미바인딩” 배지
- 단축키 `Ctrl+T`: iotag.json 다시 로드

### 7.2 제거

- Flow 우클릭 메뉴의 **“Mermaid 불러오기...”** 항목 (§0.3 참조)
- Work 우클릭의 mermaid 부분 임포트 (있는 경우)
- 캔버스 드롭존의 mermaid 직접 임포트 (있는 경우 — 프로젝트 교체 confirm 다이얼로그로 대체)

### 7.3 파일 다이얼로그 필터

```
All Supported (*.sdf;*.json;*.aasx;*.md;*.mmd;*.yaml;*.yml)
SDF Files (*.sdf)
JSON Files (*.json)
AASX Files (*.aasx)
Mermaid Files (*.md;*.mmd)        ← .mmd 추가
YAML Files — lossy 공유 포맷 (*.yaml;*.yml)
```

## 8. 마이그레이션

- 기존 `*.md` mermaid 파일 무수정 — 다이얼로그 필터 호환 유지
- iotag.json 부재 시 동작 변화 없음 (완전 backward compatible)
- Flow-level 임포트 제거: 기존 사용자 워크플로우 영향 — 릴리즈 노트에 명시 + “프로젝트 단위로 다시 열어주세요” 안내

## 9. 작업 항목 (TODO)

### Phase 1 — 진입점 단일화 (UI 정리)

- [ ] Flow 컨텍스트 메뉴 `Mermaid 불러오기...` 삭제 (`Promaker/Views/...`)
- [ ] `ImportMermaidIntoFlowCommand` 및 관련 ViewModel 삭제
- [ ] 파일 열기 다이얼로그 필터에 `*.mmd` 추가
- [ ] `ImportLevel.FlowLevel / WorkLevel` 호출 site 제거

### Phase 2 — Sidecar (Ds2.Core 무수정)

- [ ] `IoTagRecord` 타입 정의 — `Ds2.Mermaid/Import/IoTagSidecar.fs`
- [ ] `IoTagSidecar.fs` 신규 작성 (in-memory dictionary)
- [ ] `IoTagPairLoader.fs` 신규 작성 (System.Text.Json)
- [ ] `IoTagBinder.fs` 신규 작성 (escape-aware callPath split)
- [ ] `mapToSystem` 안 callIndex 구축 — Call 생성 시점에 path 누적

### Phase 3 — ProMaker 통합

- [ ] ProMaker — Call 패널 IO 영역 위젯 (Sidecar 조회)
- [ ] ProMaker — IO 미바인딩 배지 렌더
- [ ] ProMaker — `Ctrl+T` 핸들러 (iotag.json reload)

### Phase 4 — LLM / Brownfield

- [ ] ReverseChatOrchestrator — “IO 추론 금지” 프롬프트 추가
- [ ] PlantDoctorAI — XgwxLdExtractor 결과를 `iotag.json` 으로 export 하는 1버튼
- [ ] LSXGTAdapter / FujiAdapter 동일 export 지원

### Phase 5 — Cleanup

- [ ] `Ds2.Mermaid` 의 `mapToFlow` / `mapToFlowFlat` / `mapToWork` 함수 삭제
- [ ] `buildPreview` 의 `FlowLevel` / `WorkLevel` 분기 삭제
- [ ] `ImportLevel` 열거형을 `SystemLevel` 한 케이스만 남기거나 단순 type alias 로 축소

## 10. 미결 (Open Questions)

1. iotag.json 을 `*.aasx` Submodel 로도 직렬화 vs. 별도 파일만 — 표준화 시점
2. 멀티 PLC (master + slave) 매핑 — `vendor` 다중화 스키마
3. iotag 사용자 보정값 vs. 추출기 재실행 시 충돌 해결 정책 (3-way merge)
4. 데이터 타입 BOOL 외 WORD/REAL 의 비트 인덱스 표기 (`%IW10.3` 의 `.3`)
5. Flow-level 임포트 제거 후 “기존 프로젝트에 추가 임포트” 시나리오 대안 (mermaid → 임시 프로젝트 → 복사/붙여넣기?)

---

**검토 후 §9 Phase 1 부터 순서대로 구현 진행 예정. 의견 / 우선순위 조정 회신 부탁드립니다.**
