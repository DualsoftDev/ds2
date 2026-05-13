# Promaker HMI 자동 생성 + LLM agent 확장 todo

> 본 문서는 모델링용 LLM agent (`todo-promaker-llm-agent.md` / `done-promaker-llm-agent.md`, Phase 1 MVP 완료 상태) 의 인프라를 **HMI (HTML 키오스크) 자동 생성 + 자연어 편집** 으로 확장하기 위한 후속 작업이다. 모델링 작업과 별개 phase 군 (Hmi-0 ~ Hmi-1f) 으로 진행하나, **같은 LLM Chat panel / 같은 MCP server / 같은 ImportPlan 1 turn = 1 undo 패턴 / 같은 DsStore SSOT** 를 재사용한다.

---

## 작업 목표

Promaker (WPF 데스크탑) 안에서:
1. Ds2 모델 (DsSystem / Flow / Work / Call 등) 로부터 **터치 기반 HMI HTML 폴더** 를 정적 export
2. 사용자가 **시각 편집기 (drag/resize/color)** 로 layout 수정
3. 동일한 HMI SSOT 를 **LLM 자연어 명령** 으로도 수정 (예: "Start 버튼 빨갛게 키워줘") — 같은 chat, 문맥에 따라 자동 modeling/hmi 모드 switching
4. 산출물 = 정적 `index.html + hmi-runtime.js + assets/` 폴더. 외부 호스팅 / 별도 server 없음
5. Runtime 동작 = `Ev2.Backend.SignalManager` (Windows service, 이미 운영 중) 와 **SignalR + REST** 로 통신해 PLC 신호 read/write/push

---

## 배경 / 맥락

### 현재 모델링 LLM agent 상태 (`done-promaker-llm-agent.md` Phase 1d 완료)

- F# DLL `Ds2.LlmAgent` (LlmEvent / StreamJsonParser / ClaudeCliProvider / ToolOperations / ImportPlanBuilder / UiDispatcher / Logging)
- Promaker C# 측: `McpHostService` (in-process Kestrel + handshake nonce) / `McpConfigWriter` (Owner-only ACL + sweep) / `ChildProcessTracker` (Job Object cascade kill) / `LlmConsent` / `LlmTurnContext` (turn-scoped + 500ms validate cache) / `ModelTools` ([McpServerToolType] 11개 tool) / `LlmChatPanel` (dock UserControl, MainWindow column 5/6)
- Mutation 경로: tool → ImportPlanBuilder 누적 → turn end `store.ApplyImportPlan(label, plan)` 1회 → 단일 `WithTransaction` + `EmitRefreshAndHistory` → **1 LLM turn = 1 undo step**
- 확정 결정 9개: dock panel / F# DLL+C# binding / Claude CLI 1st-class / **HTTP MCP transport** / 인스턴스 격리 5.0 / **ImportPlan (d)** / **`InvokeAsync` Background dispatcher** / `IAsyncEnumerable<LlmEvent>` / 결정 6 흡수
- 현재 mcp tool 11개: `mcp__promaker__add_system / add_flow / add_work / add_call / add_arrow / add_api_def / list_systems / describe_system / describe_subtree / find_by_name / validate_model`

### Ev2.Backend.SignalManager — HMI runtime 의 통신 대상

위치: `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Backend.SignalManager/` (별도 repo, 직접 통합 X — contract 만 참조)

- **F# ASP.NET Core 8.0** (Giraffe + SignalR), Windows service 로 운영 중
- 기본 endpoint: `http://localhost:5000` (HTTPS 5001), Hub: `/hubs/communication`
- **인증**: `X-Api-Key` 헤더 또는 `api_key` query — 모든 REST/SignalR 호출 필수
- **REST API** (`/api/communication/...`):
  - `GET /tags` — 모든 tag id 조회
  - `GET /tags/{tagId}` — 값 read (Redis 우선 → PLC fallback)
  - `POST /tags/{tagId}` body `{"value": ...}` — 값 write (Cpu + Redis)
  - `POST /redis/connect` / `POST /monitoring/start|stop` 등 보조
- **SignalR Hub** (`/hubs/communication`) 메서드:
  - `SubscribeToTag(tagId)` / `SubscribeToTags(tagIds[])` / `UnsubscribeFromTag` / `UnsubscribeFromAllTags`
- **Server → Client events**:
  - `OnDataUpdated(tagId, value)` — tag 단위 push (그룹 구독자만)
  - `OnWorkStatusChanged(WorkStatusDto)` / `OnCallStatusChanged(CallStatusDto)` — entity 단위 broadcast all
  - `OnSensorUpdated(SensorDto)` / `OnAlarmRaised` / `OnSystemStatusChanged`
- **Tag ID 형식**: `"{connectionName}/{tagName}"` (PLC 연결명 + 태그명)
- 보조 의존성 layer: `Ev2.Backend.Communication` (OpcUaClient / RedisClient / RedisService) → `Ev2.Communication` (CommunicationSubject 추상화)
- 참조 소스 (read-only):
  - `Hubs.fs` — `CommunicationHub` + broadcast 헬퍼 모듈
  - `WebApi.fs` — `getTagValue` / `writeTagValue` / `getAllTags` / `getSystemStatus`
  - `Security.fs` — API key 인증
  - `README.md` — JS 클라이언트 예제 (`@microsoft/signalr` 사용)

### 사용자 핵심 요청 (본 세션 답변)

1. 산출물 = **정적 HTML 폴더**
2. 자동 생성 < **사람 개입 (시각 편집) + LLM 자연어 명령**
3. SignalManager 는 **Redis + REST + SignalR** 지원, 본 작업은 REST + SignalR 사용
4. 모델 → HMI 매핑 = 일정 부분 정해짐 (예: Work → button + lamp 자동 seed) + **유연성** 필요 (사용자가 자연어로 변경)
5. **HmiModel 따로 없음** — 새로 설계 필요
6. **signal binding picker 제외** — 모델 entity 가 SSOT, 자유 tag 주소 입력 X
7. **LLM 모드 switching = 단일 chat, 문맥 자동 판별** (사용자가 modeling/hmi 모드 의식 안 함)
8. **ID 가 아니라 tag 이름** 으로 SignalManager 와 통신. **Ds2 entity → tagId 변환 함수는 추후 제공 가정** — phase 1 stub 으로 진행 가능

---

## 현재까지 결정된 설계 방향 (확정)

### 결정 H1 — HmiModel 위치: DsStore 통합 (옵션 P1)

3 옵션 중 **P1 채택** (사용자 확인):

| 옵션 | 핵심 | 채택 여부 |
|---|---|---|
| **P1. DsStore 통합** | `HmiPage`/`HmiControl` 새 entity + `ImportPlanOperation` DU 9 → 9+M 확장. 한 store + 한 Undo + 한 EditorEvent + JSON/SDF/AASX I/O 자연 흡수 | ✅ 확정 |
| P2. 별 store + 별 `.hmi.json` 파일 | Ds2.Core 무수정. 단 Undo 분리 / EditorEvent 분리 → 사용자 혼란 | ❌ |
| P3. DsStore metadata field | layout/binding 표현 부족 | ❌ |

**시사**:
- Ds2.Core 의 `ImportPlan.fs` 의 `ImportPlanOperation` DU 확장 필요 (현재 9종 → 9+M 종)
- Ds2.Core 의 도메인 entity 추가 — `HmiPage`, `HmiPanel`, `HmiControl`, `HmiBinding`
- Ds2.Core 의 JSON formatter 확장 (Newtonsoft 직렬화 path)
- `Ds2.Editor/Editor/ImportPlanApply.fs` 의 `applyOperationDirect` / `applyOperationTracked` 에 새 op 분기 추가
- `Ds2.Editor/Store/Nodes/Nodes.fs` 의 `[<Extension>]` 표면 확장 (옵션, mutation extension API 가 필요한 GUI 측 호출이 있다면)

### 결정 H2 — Signal binding 표현: entity ID + aspect (tagId 추상화)

```fsharp
// Ds2.Hmi 또는 Ds2.Core 안 (위치는 H4 결정)
type WorkAspect =      // SignalManager WorkStatusDto 의 status enum 참조 (Hmi-0 spike 에서 확정)
    | Run | Idle | Error | Completed | Reset | Pause | ...

type CallAspect =      // SignalManager CallStatusDto 의 status enum 참조 (Hmi-0 spike 에서 확정)
    | On | Off | Busy | Done | Fault | ...

type HmiControlBinding =
    | WorkBinding of workId: Guid * aspect: WorkAspect
    | CallBinding of callId: Guid * aspect: CallAspect
    | NoBinding   // label / decoration
```

**핵심 원칙**:
- HmiControl 은 **entity ID + aspect** 만 들고, 실제 PLC tagId 는 절대 안 들고 있음
- LLM 자연어 명령은 entity 의미로만 이해 (사용자가 tag 주소 몰라도 됨)
- 산출물 export 시점에 generator 가 `EntityToTagId` 함수로 변환 → `mapping.json` + HTML `data-tag-id` 로 emit

### 결정 H3 — `EntityToTagId` 추상화

```fsharp
type IEntityToTagIdResolver =
    abstract Resolve : binding: HmiControlBinding -> string option   // None = 매핑 없음 (warn)
```

- **위치 결정 필요** (다음 결정 요청 1번): `Ds2.Core` (cross-domain abstraction, runtime/시뮬레이션 등 다른 모듈도 사용 가능) vs `Ds2.Hmi` (HMI 전용)
  - 권장: **Ds2.Core** — 향후 다른 모듈도 같은 변환 사용 가능
- Phase 1 = stub 구현 (placeholder 형식 e.g. `$"{workId}/{aspect}"`), 실제 함수 wired 시점에 generator 만 수정
- HmiHtmlGenerator 가 DI 로 받음

### 결정 H4 — LLM agent 통합 형태: 단일 chat + 자동 모드 switching

3 옵션 중 **A 채택**:

| 옵션 | 핵심 | 채택 |
|---|---|---|
| **A. 기존 promaker MCP 에 hmi tool 추가** | 인프라 (Kestrel/nonce/Job Object/consent/sweep) 100% 재사용. allowlist 확장만. system prompt = modeling+hmi 통합. LLM 이 문맥 따라 tool 자동 선택 | ✅ 확정 |
| B. 두 번째 MCP server | 모드 격리 명확 vs 인프라 중복 | ❌ |
| C. MCP 미사용, generator 직접 호출만 | 자연어 UX 포기 (사용자 답 2 거부) | ❌ |

**구조**:
- `LlmTurnContext` 의 plan = `ImportPlanBuilder` 1개 (DU 가 modeling/hmi op 모두 포함하므로 자연 통합) → 1 turn 안에 model 추가 + hmi 편집 섞여도 1 undo step
- `PromakerToolNames.cs` 확장: `mcp__promaker__*` (현재 11) + `mcp__promaker__hmi_*` (~10~15)
- `SystemPrompt.cs` 의 `Phase1c` 상수에 hmi schema + tool + 의도 분석 rule 추가
- `--allowed-tools` 화이트리스트 자동 확장
- LlmChatPanel UI 변경 없음 (모드 토글 X)

### 결정 H5 — Default seed 매핑 + 유연성

- 첫 generation 시 **단순 규칙** (Hmi-1b 에서 확정):
  - Work → button (실행 트리거) 1개 + lamp (Run aspect) 1개를 같은 panel 에 자동 배치
  - 또는 단순화: Work 1개당 panel 1개, panel 안 button + lamp 자동
  - Call → 별도 lamp (option, default disabled — 사용자가 enable)
  - Flow → page section / panel grouping
- 사용자가 시각 편집기 또는 LLM 자연어로 자유 변경 가능
- default 매핑은 **첫 생성 시 1회 seed** 만 — 이후엔 사용자 의도가 SSOT

### 결정 H6 — HMI runtime client lib

- 파일: `hmi-runtime.js` (정적 export 폴더에 동봉)
- 의존성: `@microsoft/signalr` JavaScript lib (CDN 또는 vendor.js 동봉, Hmi-0 spike 에서 결정)
- 동작:
  - 첫 실행: API key 입력 → `localStorage` 저장 (또는 export 시 동봉, 사용자 옵션)
  - SignalR 연결 + `SubscribeToTags` (mapping.json 안 모든 tagId)
  - `OnDataUpdated` / `OnWorkStatusChanged` / `OnCallStatusChanged` listen → control 의 `data-entity-id`/`data-aspect` 매칭 → 시각 갱신
  - button click → `pointerdown/up` → REST `POST /api/communication/tags/{tagId}` body `{"value":1}` (또는 toggle)
- 산출물 폴더 구조:
```
out/
├─ index.html             // generator 결과, data-tag-id + data-entity-id + data-aspect attribute
├─ mapping.json           // entity ↔ tagId 사전 변환 결과 (runtime 의 reverse lookup)
├─ hmi-runtime.js         // SignalR client + binding logic
├─ base.css               // 키오스크 친화 (touch target ≥48dp / no zoom / no context menu)
├─ assets/                // 사용자 추가 이미지 등 (옵션)
└─ vendor/signalr.min.js  // CDN 사용 시 생략
```

### 결정 H7 — Touch base 제약

- Pointer Events (`pointerdown`/`pointerup`) — mouse/touch unified
- Viewport meta tag, `touch-action: manipulation` (더블탭 줌 차단), `user-select: none`, `-webkit-tap-highlight-color: transparent`
- 컨텍스트 메뉴 차단 / 가상 키보드 차단 / 키오스크 모드 가정
- 최소 터치 타겟 48x48dp (HIG)
- `:active` 시각 피드백 + 색/크기 즉시 반응 (haptic 없음)

---

## 남은 할 일

### Phase Hmi-0 — Spike (사전 실증, 1~2시간 분량)

> 사용자 답변으로 ID 매핑 / Tag id 매핑 spike 가 축소됨. 남은 항목:

- [ ] **0a — `EntityToTagId` interface signature 합의** (위치 = Ds2.Core 권장, 사용자 확인 필요): `IEntityToTagIdResolver.Resolve(HmiControlBinding) → string option` — 다음 결정 요청 1번 처리
- [ ] **0b — WorkAspect / CallAspect 값 set 확정**: `Ev2.Backend.Common.DTO.WorkStatusDto` / `CallStatusDto` 의 status enum 값을 직접 확인 (별도 repo `/f/Git/ev2/master/solutions/Ev2.Backend/...`). 결과로 H2 의 DU 값 채움. 다음 결정 요청 2번
- [ ] **0c — SignalR JS lib 배포 방식 결정**: CDN (외부 인터넷 의존) vs vendor bundle (오프라인 가능, 산출물 폴더 ↑). 키오스크 환경 가정 시 vendor 권장
- [ ] **0d — API key UX 설계**: ① HMI 첫 실행 시 입력 → localStorage / ② Promaker export 다이얼로그에서 동봉 옵션 (보안 경고 동반) / ③ 외부 reverse-proxy. 사용자 환경에 따라 결정
- [ ] **0e — 토이 RTT 측정**: 1 button + 1 lamp + 실제 SignalManager 실행 환경에서 latency. button click → REST POST → PLC → OnWorkStatusChanged → lamp 색 변경 round-trip 측정. 이 결과로 polling fallback 필요 여부 판단
- [ ] **(축소) 0a' — ID 매핑**: ❌ Skip (사용자가 entity→tagId 함수 추후 제공 가정으로 해소)

### Phase Hmi-1a — `Ds2.Hmi` 도메인 + ImportPlan 확장 (PR 1)

- [ ] **Ds2.Core 의 도메인 entity 추가**:
  - `HmiPage { Id, Name, ProjectId, Layout (size/grid) }`
  - `HmiPanel { Id, PageId, Name, Bounds (x/y/w/h), GroupingHint (option SystemId/FlowId) }`
  - `HmiControl { Id, PanelId, Kind (Button/Lamp/Label/Group), Bounds, Style (color/font/border), Binding: HmiControlBinding option }`
- [ ] `WorkAspect` / `CallAspect` DU (0b 결과 반영)
- [ ] `HmiControlBinding` DU (`WorkBinding` / `CallBinding` / `NoBinding`)
- [ ] **`ImportPlanOperation` DU 확장** (`Solutions/Core/Ds2.Core/Store/ImportPlan.fs`):
  - `AddHmiPage of HmiPage`
  - `AddHmiPanel of HmiPanel`
  - `AddHmiControl of HmiControl`
  - `MoveHmiControl of controlId * Bounds`
  - `ResizeHmiPanel of panelId * Bounds`
  - `RecolorHmiControl of controlId * Style`
  - `RebindHmiControl of controlId * HmiControlBinding option`
  - `RemoveHmiPage of pageId` / `RemoveHmiPanel of panelId` / `RemoveHmiControl of controlId`
  - `RenameHmiPage` / `RenameHmiPanel` / `RenameHmiControl` (옵션)
- [ ] `ImportPlanApply.fs` 의 `applyOperationDirect` + `applyOperationTracked` 에 신규 op 분기 — 단일 `WithTransaction` 패턴 보존 (1 turn = 1 undo)
- [ ] DsStore 의 dictionary field 추가 (`HmiPages`, `HmiPanels`, `HmiControls` — 또는 `HmiPage` 가 자식들을 들고 있는 식 — Ds2 의 기존 패턴 따라 결정)
- [ ] `Ds2.Core.Store.Queries` (`[<AutoOpen>] module Queries`) 에 read 함수 추가 — `getHmiPage`, `panelsOfPage`, `controlsOfPanel`, `controlsOfWork (workId)` 등
- [ ] JSON formatter 확장 (`Ds2.JsonFormatter` 또는 동등 위치) — Newtonsoft.Json 사용 일관
- [ ] `IEntityToTagIdResolver` interface (Ds2.Core, H3 위치 결정 후)
- [ ] 빌드 검증 — 경고 0, 오류 0
- [ ] Undo 검증 (model add + hmi add 가 같은 turn 에서 1 undo step 되는지 확인)

### Phase Hmi-1b — 결정적 generator (PR 2)

- [ ] `Ds2.Hmi.Generator` (F# 신규 module — `Solutions/Core/Ds2.Hmi/` 또는 `Solutions/Convert/Ds2.Hmi.Generator/` — 위치 결정)
- [ ] `HmiOptions` record (page size / theme / API base url / API key 동봉 여부 / signalr lib 배포 방식)
- [ ] `generate : HmiPage seq -> IEntityToTagIdResolver -> HmiOptions -> Files` (Files = 폴더 manifest)
  - HTML emit (Pointer Events / data-attribute / 키오스크 meta)
  - mapping.json (entity ↔ tagId 사전 변환)
  - hmi-runtime.js (embedded resource)
  - base.css (키오스크 친화)
  - vendor/signalr.min.js (옵션)
- [ ] `EntityToTagId` stub 구현 (phase 1, e.g. `$"{workId}/{aspect}"` 형식)
- [ ] **default seed 매핑** (H5 단순 규칙): Project → 빈 HmiPage 1개 / System → Panel 1개 / Work → button + lamp 1쌍 / Call → optional lamp
- [ ] Promaker UI 의 export 메뉴 — `File / Export → HMI HTML` + 다이얼로그 (대상 system 선택 + HmiOptions)

### Phase Hmi-1c — Promaker HMI Designer panel (PR 3)

- [ ] `Apps/Promaker/Promaker/Controls/Hmi/HmiDesignerPanel.xaml(.cs)` UserControl
- [ ] `Apps/Promaker/Promaker/ViewModels/HmiDesignerViewModel.cs`
- [ ] MainWindow 에 dock column 추가 (현재 LlmChat 의 column 5/6 패턴 따라 — 또는 LlmChat 과 같은 영역 안에서 탭 전환)
- [ ] 시각 편집기 기능 (1차):
  - HmiPage / HmiPanel / HmiControl 트리 표시
  - 캔버스 위 drag/resize (Bounds 변경 → ImportPlanOperation 1 op)
  - Color picker (Style.Color → Recolor op)
  - Control 추가/삭제 컨텍스트 메뉴
  - **매 액션 = ImportPlan 1 op = 1 undo step** (LLM turn 의 batch 와는 다름 — 사용자 1 액션 = 1 undo)
- [ ] EditorEvent 구독 → 캔버스 자동 갱신 (LLM 자연어 명령으로 hmi 변경 시에도 동일 path)

### Phase Hmi-1d — LLM agent HMI tool 통합 (PR 4)

- [ ] `Solutions/Core/Ds2.LlmAgent/HmiToolOperations.fs` (F# helper, `ToolOperations.fs` 와 형제):
  - `queueAddHmiPage` / `queueAddHmiPanel` / `queueAddHmiControl`
  - `queueMoveHmiControl` / `queueResizeHmiPanel` / `queueRecolorHmiControl` / `queueRebindHmiControl`
  - `queueRemoveHmi*`
  - `describeHmi` (HmiPage tree dump) / `findHmiControl` / `validateHmi` (orphan binding / bounds overflow / duplicate name 등)
- [ ] `Apps/Promaker/Promaker/LlmAgent/Tools/HmiTools.cs` `[McpServerToolType] static class`:
  - mutation: `add_hmi_page`, `add_hmi_panel`, `add_hmi_control`, `move_hmi_control`, `resize_hmi_panel`, `recolor_hmi_control`, `rebind_hmi_control`, `remove_hmi_*`
  - read: `describe_hmi_page`, `find_hmi_control`, `validate_hmi_model`
  - 7 책임 일관 헬퍼 (`Sanitize`/`ParseGuid`/`RunMutation`/`RunRead`) 재사용
- [ ] `PromakerToolNames.cs` 확장 — 11 → 11+N개. Servername=`promaker` 그대로
- [ ] `SystemPrompt.cs` 보강:
  - HMI schema 트리 도식 (Page → Panel → Control + Binding)
  - HMI tool 시그니처 + 의미
  - **modeling vs hmi 의도 자동 판별 rule**: "사용자가 entity (Work/Call/Flow) 의 시각 표현 / 색 / 크기 / 배치 / lamp 표시 / button 추가 등을 언급하면 hmi tool, 모델 구조 (시스템 / Flow / Work / Call / Arrow / ApiDef 추가/수정) 면 modeling tool"
  - 모호 시 1 clarifying question
- [ ] `LlmTurnContext` 변경:
  - 현재: `Plan : ImportPlanBuilder` (modeling op 만)
  - 변경: 통합 ImportPlanBuilder (modeling + hmi op DU 가 같이 들어감 — H1 의 통합 결과로 자연 합류)
  - mutation quota (현 50) 는 통합 카운터 그대로
- [ ] turn end `ApplyImportPlan` 호출 그대로 (model + hmi op 가 한 plan 안에서 단일 `WithTransaction`)

### Phase Hmi-1e — HMI runtime + SignalManager 통신 (PR 5)

- [ ] `Apps/Promaker/Promaker/LlmAgent/HmiRuntime/` 또는 `Solutions/Core/Ds2.Hmi/runtime/` 의 embedded resource:
  - `hmi-runtime.js` — SignalR connection + tag subscribe + event listener + REST write helper
  - `base.css` — 키오스크 친화 reset + utility (touch target / 색 token)
- [ ] HMI runtime API 표면:
  - `init({ baseUrl, apiKey, mappingJson })` — 자동으로 SignalR 연결 + 모든 tagId 구독
  - control 자동 binding: HTML 의 `[data-entity-id][data-aspect]` 검색 → reverse lookup → tag 단위 listen
  - button: `pointerdown` → `pointerup` 묶음 → REST POST. 빠른 연속 입력 debounce 옵션
  - lamp: `OnDataUpdated` 또는 `OnWorkStatusChanged` 의 entity ID 매칭 → CSS class 토글
  - reconnect 정책: SignalR 의 `withAutomaticReconnect` 사용
- [ ] API key UX (0d 결정 결과 반영):
  - 첫 진입 modal 입력 + localStorage / 또는 동봉 옵션
- [ ] Promaker export 다이얼로그 옵션 추가:
  - SignalManager base url
  - API key 동봉 (off by default + 보안 경고)
  - SignalR lib (CDN / vendor)
- [ ] HmiHtmlGenerator 가 mapping.json 을 emit (entity ID + aspect → tagId, `IEntityToTagIdResolver` 호출 결과 직렬화)

### Phase Hmi-1f — 회귀 테스트 (PR 6)

- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/HmiToolArgsTests.fs` — `--allowed-tools` 에 hmi tool 11~15 포함되는지 / drift 검출
- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/HmiGeneratorTests.fs` — 결정적 generator 의 golden text 비교 (HmiPage 1개 / Panel 1개 / Button 1개 / Lamp 1개 → 고정 HTML output)
- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/HmiValidateTests.fs` — `validate_hmi_model` 의 카테고리 출력 안정성 (orphan binding / bounds overflow / duplicate name)
- [ ] (옵션) `Solutions/Tests/Ds2.Hmi.Tests/` 신규 — Ds2.Hmi 도메인 단독 테스트 (`HmiPage` 직렬화 round-trip 등)
- [ ] 사용자 e2e 시나리오 (수동, done 문서에 분산 기록):
  - Promaker 에서 Press 시스템 + Work 2개 모델 생성 → HMI export → 정적 폴더 열기 → SignalManager 와 통신 동작
  - LLM 자연어 "Press 시스템의 Adv work 의 button 을 빨강으로 키워줘" → hmi_recolor_control + resize 자동 호출
  - LLM 모드 자동 switching: 같은 chat 안 "Press 시스템에 Bwd work 추가" → modeling tool / "Bwd 의 lamp 도 추가해줘" → hmi tool
  - 1 turn = 1 undo step (model + hmi 혼합 operation)

---

## 관련 파일 / 경로

### 본 작업 신규 (예정)

- `Solutions/Core/Ds2.Hmi/` (F# 신규 도메인 또는 `Ds2.Core` 안 흡수 — H1 의 P1 결과로 후자 가능성 높음)
- `Solutions/Core/Ds2.LlmAgent/HmiToolOperations.fs`
- `Apps/Promaker/Promaker/Controls/Hmi/HmiDesignerPanel.xaml(.cs)`
- `Apps/Promaker/Promaker/ViewModels/HmiDesignerViewModel.cs`
- `Apps/Promaker/Promaker/LlmAgent/Tools/HmiTools.cs`
- `Apps/Promaker/Promaker/LlmAgent/HmiRuntime/hmi-runtime.js + base.css` (또는 Ds2.Hmi/runtime/)
- `Solutions/Tests/Ds2.LlmAgent.Tests/Hmi*Tests.fs`

### 본 작업 수정 (예정)

- `Solutions/Core/Ds2.Core/Store/ImportPlan.fs` — `ImportPlanOperation` DU 확장
- `Solutions/Core/Ds2.Core/Store/DsStore.fs` 또는 dictionary field 위치 — Hmi entity 들 추가
- `Solutions/Core/Ds2.Core/Store/DsQuery/Queries.fs` — Hmi read 함수 추가
- `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs` — 신규 op 분기
- `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` — `HmiToolOperations.fs` Compile Include
- `Apps/Promaker/Promaker/Promaker.csproj` — `Ds2.Hmi.fsproj` 추가 (Ds2.Hmi 가 별 fsproj 인 경우)
- `Apps/Promaker/Promaker/MainWindow.xaml(.cs)` — HmiDesignerPanel dock 추가 (또는 LlmChat 영역 안 탭)
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — hmi tool 이름 추가
- `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` — HMI schema/tool/rule 추가
- `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` — plan 통합 (이미 ImportPlanBuilder 라 자연 합류 가능)

### 참조 (read-only, 별도 repo)

- `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Backend.SignalManager/` — Hub / WebApi / Security / README
- `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Backend.Common/DTO/` (추정) — `WorkStatusDto`, `CallStatusDto`, `SensorDto`, `SystemStatusDto` (Hmi-0 0b 에서 확인)

### 모델링 LLM agent 측 동기 갱신 필요

- `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` — Architecture 섹션에 HMI 영역 추가 (현재 모델링만 기술)
- `Solutions/Core/Ds2.LlmAgent/todo-promaker-llm-agent.md` — Phase 2 영역에 본 작업 cross-reference 추가
- `Solutions/Core/Ds2.LlmAgent/done-promaker-llm-agent.md` — 본 작업 완료 시 Phase 2 절 추가

---

## 미해결 결정 지점 (다음 세션 시작 시 사용자 확인 필요)

1. **`IEntityToTagIdResolver` 위치** — Ds2.Core (cross-domain) vs Ds2.Hmi (전용). **권장 = Ds2.Core**. 사용자 확인 필요
2. **`WorkAspect` / `CallAspect` 값 set** — `Ev2.Backend.Common.DTO.WorkStatusDto` / `CallStatusDto` 의 status enum 직접 확인 (`/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Backend.Common/DTO/`). Hmi-0 0b 에서 처리 또는 사용자가 알고 있는 값 직접 알려주면 spike 단축
3. **Hmi-0 spike 전체 진행 vs Hmi-1a 병행** — spike 가 가벼워졌으니 1a 도메인 골격과 병행 가능. 사용자 의향
4. **Ds2.Hmi 가 별 fsproj 인지 vs Ds2.Core 흡수** — H1 의 P1 (DsStore 통합) 채택했으므로 entity 자체는 Ds2.Core 안에 들어감. 다만 generator / runtime resource / 렌더 도우미는 Ds2.Hmi 별 fsproj 가 자연. 사용자 확인 필요
5. **HMI Designer panel 위치** — MainWindow 의 새 column (LlmChat column 5/6 와 형제) vs LlmChat panel 안 탭 vs 별 dialog window. 화면 공간 trade-off
6. **API key UX 정책** — Hmi-0 0d 결과 결정 (입력 modal / 동봉 옵션 / proxy)
7. **default seed 매핑 의 정확한 규칙** — H5 의 단순 규칙 (Work=button+lamp / Call=optional lamp) 가 사용자 의도와 일치하는지 Hmi-1b 진입 시 재확인

---

## 주의 사항

1. **DsStore SSOT 통합 = Ctrl+Z 가 model + hmi 양쪽 합쳐 동작**. 사용자가 의도한 시점에 분리된 undo 가 필요한 시나리오가 있는지 사전 합의 필요. 현재 가정 = "혼합 LLM turn 1 step rollback 이 더 자연스러움"
2. **모델링 turn 의 quota (50) 가 hmi tool 호출에도 합산** — LLM 이 1 turn 안에 100개 control resize 요청 시 차단. 별 quota 필요한지 1d 시점 재평가
3. **JSON formatter 확장 시 기존 모델 파일 호환성** — Hmi entity 가 비어있는 기존 .json 도 정상 load 되어야 함. backward compat 테스트 필수
4. **HMI runtime 의 API key 보안** — 정적 폴더에 박는 건 안티패턴. 첫 진입 입력 + localStorage 가 default. 동봉 옵션은 명시적 사용자 선택 + 경고
5. **Touch target 자동 검증** — HmiHtmlGenerator 가 control bounds < 48dp 시 경고 emit. validate_hmi_model 도 같이 검사
6. **LLM 의 모드 자동 switching 신뢰성** — 첫 PR 후 사용자 e2e 에서 오분류 빈도 측정 필수 (예: "Press 시스템의 Run 상태가 빨간색으로 보이게 해줘" → modeling vs hmi 모호). 모호 시 clarification 요청 prompt rule 적극 활용
7. **시각 편집기 vs LLM 자연어 명령 의 동시 사용** — 같은 control 을 사용자가 drag 하는 도중 LLM 이 resize 호출하면 race. dispatcher InvokeAsync Background 패턴 그대로 적용 (모델링 측 결정 8 재사용)
8. **HMI runtime 의 reconnect / offline 동작** — SignalR 연결 끊김 시 lamp 가 stale 표시 (회색 + "disconnected" overlay). 재연결 후 재구독은 SignalR `withAutomaticReconnect` 자동
9. **사용자 글로벌 규칙 준수**: F# > C# / 정석 해결 / 기존 재활용 90점 / try/catch 자제 (fail-fast) / log4net `logDebug` etc / camelCase=private,internal,F# 함수 / PascalCase=Property,public

---

## 다음 작업 진입 권장 순서

1. **사용자에게 미해결 결정 지점 1, 2, 3, 4 확인** — 가장 critical = 1 (interface 위치) + 2 (Aspect 값 set)
2. **Hmi-0 spike 의 0a, 0b 처리** (사용자 답변 또는 짧은 source 검토)
3. **Hmi-1a 도메인 entity + ImportPlan DU 확장** — backward compat 위해 JSON formatter 변경 minimal 부터
4. **Hmi-0 의 0c/0d/0e 는 Hmi-1e 직전에 처리해도 무방** (1a/1b/1c/1d 가 SignalManager 통신 없이 진행 가능하므로)
5. **Hmi-1b generator** — `EntityToTagId` stub 으로 정적 export 까지 동작
6. **Hmi-1c 시각 편집기** + **Hmi-1d LLM tool 통합** 은 sibling — 둘 중 어느 것 먼저든 무방하나, 1d 가 인프라 재사용 비율 높아 1d 먼저 권장 (modeling agent 의 검증된 패턴 직접 따라가기)
7. **Hmi-1e runtime** — SignalManager 와의 실제 round-trip
8. **Hmi-1f 회귀** — 자동화 가능한 부분만, 사용자 e2e 는 done 문서에 분산

---

## 새 세션 진입 시 sanity check 체크리스트

1. 본 문서 (`doc/todo-promaker-hmi-llm-agent.md`) 통독
2. 모델링 LLM agent 의 현 상태 확인:
   - `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` Architecture 섹션
   - `Solutions/Core/Ds2.LlmAgent/done-promaker-llm-agent.md` 의 Phase 1d / Pass A 절
   - `Solutions/Core/Ds2.LlmAgent/todo-promaker-llm-agent.md` 의 진행 상태
3. SignalManager source 1차 확인:
   - `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Backend.SignalManager/README.md` (전체)
   - `Hubs.fs` (broadcast 헬퍼 모듈)
   - `WebApi.fs` (REST handler 시그니처)
4. 검증된 사실 표 source line 1~2개 sanity check (`ImportPlan.fs:5-15`, `ImportPlanApply.fs:34-38`)
5. 미해결 결정 지점 1, 2 사용자 확인 후 Hmi-0 spike → Hmi-1a 진입
