# Flow Workspace Design

## 목표

`상세정보(/chart)` 페이지를 없앤 뒤, 좌측 Tree에서 `Flow`를 클릭하면 그 Flow에 필요한 핵심 정보와 분석 도구를 한 화면에서 제공한다.

핵심 방향:

- `Flow`를 앱의 기본 분석 단위로 승격
- 기존 `대시보드`, `동작편차`, `Cycle 분석`, `PLC 디버그`의 Flow 관련 기능을 한 화면으로 통합
- 페이지를 여러 개 오가며 같은 Flow를 다시 고르지 않게 함

---

## 현재 재사용 가능한 기능

### 1. 실시간 상태 / 시간 요약

재사용 소스:

- `DSPilot/Components/Pages/Dashboard.razor`
- `DSPilot/Services/DspDbService.cs`

이미 가능한 정보:

- Flow 현재 상태
- MT / WT
- CT 계산
- 최대시간 / 최소시간 / 최대대기율 강조
- 도면 상 Flow 강조

관련 코드:

- `Dashboard.razor`의 `GetFlowMtWt`
- `Dashboard.razor`의 `GetFlowStateLabel`
- `Dashboard.razor`의 `GetFlowBadges`

### 2. Call 변동성 분석

재사용 소스:

- `DSPilot/Components/Pages/Heatmap.razor`
- `DSPilot/Services/HeatmapService.cs`

이미 가능한 정보:

- Flow 단위 Call 통계 그룹
- Average / StdDev / CV
- Work별 Call 매트릭스
- 이슈 Call 색상 분류

### 3. Cycle / Timeline 분석

재사용 소스:

- `DSPilot/Components/Pages/CycleAnalysis.razor`
- `DSPilot/Services/CycleAnalysisService.cs`

이미 가능한 정보:

- Flow head call 기반 cycle boundary 탐지
- 최근 N초 / custom range
- Gantt 기반 IO event 타임라인
- 선택 아이템 분석
- GAP 분석 / 정렬 / 확대

### 4. 원시 PLC 로그 추적

재사용 소스:

- `DSPilot/Components/Pages/PlcDebug.razor`
- `DSPilot/Services/PlcDebugService.cs`

이미 가능한 정보:

- DB 파일 업로드 기반 PLC 로그 분석
- Flow 필터
- 태그 검색
- 전체 기간 자동 설정
- 태그별 raw trace 확인

---

## 새 화면 개념

새 화면 이름:

- `Flow Workspace`

권장 라우트:

- `/flow`
- query string: `?name=<flowName>`

예:

- `/flow?name=Check%20Zone%231`

Tree에서 Flow 클릭 시 동작:

1. 해당 Flow 이름을 선택 상태로 기록
2. `/flow?name=...`로 이동
3. 화면 전체가 선택 Flow 컨텍스트로 갱신

---

## 화면 구성

## 1. Header

표시 항목:

- Flow 이름
- 현재 State
- 마지막 갱신 시각
- System 이름
- Work 수 / Call 수

우측 액션:

- `새로고침`
- `Cycle 열기`
- `PLC Trace 열기`
- `레이아웃에서 위치 보기`

## 2. Hero KPI Strip

최우선 핵심 정보:

- 현재 CT
- MT
- WT
- WT 비율
- Error Call 수
- Active Call 수
- 최근 완료 사이클 시간

색상 규칙:

- `Ready` 회색
- `Going` 녹색
- `Finish` 청색
- `Error` 적색

## 3. Flow Layout Panel

목적:

- 이 Flow가 설비 레이아웃 상 어디 있는지 바로 보여줌

구성:

- 도면 전체 축소 보기
- 선택 Flow만 강조
- 인접 Flow 희미하게 유지
- 상태 badge 표시

재사용 기반:

- `Dashboard.razor`
- `FlowLayoutSvg`

## 4. Performance Panel

목적:

- 선택 Flow 내부 Call들의 성능 편차를 바로 확인

구성:

- metric selector: `Average`, `StdDev`, `CV`
- Work 그룹별 Call matrix
- 이슈 Call Top N
- 평균 기준 정렬 / CV 기준 정렬

재사용 기반:

- `Heatmap.razor`
- `HeatmapService`

화면 원칙:

- 전체 Flow matrix는 제거
- 선택된 Flow만 렌더
- Work 그룹 경계가 강하게 보여야 함

## 5. Timeline Panel

목적:

- 선택 Flow의 실제 IO 진행 순서를 시간축에서 확인

구성:

- 최근 N초 / 사용자 지정 시간 범위
- Gantt timeline
- lane / call / work 정렬
- GAP highlight
- item click 분석

재사용 기반:

- `CycleAnalysis.razor`
- `CycleAnalysisService`

변경 권장:

- 현재 상세한 선택 아이템 패널은 timeline 바로 아래 고정
- `Flow 선택` 드롭다운 제거
- 상위 Flow 컨텍스트를 그대로 사용

## 6. PLC Trace Panel

목적:

- raw PLC 로그와 분석 결과를 같은 Flow 문맥에서 비교

구성:

- 현재 Flow에 매핑된 태그만 기본 선택
- 주소/이름 검색
- 기간 reset
- raw trace chart
- tag chip 목록

재사용 기반:

- `PlcDebug.razor`
- `PlcDebugService`

변경 권장:

- 독립 페이지 전체 레이아웃이 아니라 서브 패널로 축소
- `DB 파일 선택`은 유지하되 상단 hero는 제거
- Flow 필터는 숨기고 현재 선택 Flow를 고정 사용

## 7. Call Inventory Panel

새로 필요함.

목적:

- 선택 Flow 내부 Call 구조와 태그 매핑을 빠르게 점검

표시 항목:

- Work
- Call
- InTag
- OutTag
- 현재 State
- GoingCount
- 평균 시간
- 최근 이벤트 시각
- 매핑 누락 여부

이 패널은 기존 `/chart`의 단순 목록을 대체한다.

---

## 추천 레이아웃

### 기본 레이아웃

1열 전체폭:

- Header
- KPI Strip

2열 영역:

- 좌: Flow Layout
- 우: Performance Panel

1열 전체폭:

- Timeline Panel

하단 2열:

- 좌: PLC Trace Panel
- 우: Call Inventory Panel

이유:

- 사용자가 먼저 상태/성능을 보고
- 이상 징후가 있으면 바로 timeline
- 더 내려가서 raw PLC와 call mapping까지 확인하게 됨

---

## 데이터 소스 매핑

### Header / KPI

- `DspDbService.Snapshot.Flows`
- `DspDbService.Snapshot.Calls`

### Layout

- `BlueprintService.Layout.FlowPlacements`

### Performance

- `HeatmapService.GetHeatmapDataAsync()`
- 결과를 `FlowName`으로 필터

### Timeline

- `CycleAnalysisService.DetectRecentCyclesAsync(flowName, cycleCount)`
- `CycleAnalysisService.GetIOEventsInTimeRangeAsync(flowName, start, end)`

### PLC Trace

- `PlcDebugService`
- `MapperService`
- selected flow -> mapped tag addresses

### Call Inventory

- `DsProjectService.GetFlowByName`
- `GetWorks(flow.Id)`
- `GetCalls(work.Id)`
- `PlcToCallMapperService.GetCallTagsByCallId`
- `DspDbService.Snapshot.Calls`

---

## 구현 구조 제안

새 파일:

- `DSPilot/Components/Pages/FlowWorkspace.razor`

분리 컴포넌트:

- `FlowWorkspaceHeader.razor`
- `FlowKpiStrip.razor`
- `FlowLayoutPanel.razor`
- `FlowPerformancePanel.razor`
- `FlowTimelinePanel.razor`
- `FlowPlcTracePanel.razor`
- `FlowCallInventoryPanel.razor`

서비스 재사용:

- 기존 서비스는 최대한 유지
- 페이지 단위 코드를 패널 컴포넌트로 분리

하지 말아야 할 것:

- `Dashboard`, `Heatmap`, `Cycle`, `PlcDebug`를 그대로 iframe 식으로 감싸기
- 각 패널이 자기 Flow selector를 다시 가지기
- 패널마다 개별 새로고침 기준이 달라지는 구조

---

## 단계별 구현 순서

### 1단계

- `FlowWorkspace.razor` 생성
- `/flow?name=` 라우트 추가
- Tree 클릭을 `/flow?name=` navigation으로 연결

### 2단계

- Header + KPI Strip + Call Inventory 먼저 구현
- 최소 기능으로도 기존 `/chart` 대체 가능하게 함

### 3단계

- Heatmap Flow 패널 이식
- 선택 Flow 전용 성능 분석 붙이기

### 4단계

- Cycle timeline 패널 이식
- 기존 `Flow 선택` UI 제거

### 5단계

- PLC Trace 패널 이식
- Flow 기본 태그 선택 자동화

### 6단계

- Dashboard 도면 강조와 통합
- 선택 Flow 중심 workspace 완성

---

## 1차 구현 범위 권장

우선 아래까지만 구현하면 사용자 체감이 가장 크다.

- Tree Flow 클릭 -> `/flow?name=...`
- Header
- KPI Strip
- Call Inventory
- Flow 전용 Heatmap panel
- Flow 전용 Timeline panel

`PLC Trace`는 2차로 붙여도 된다.

이유:

- raw PLC trace는 무겁고 DB 파일 업로드 의존이 있음
- 반면 KPI / Call / Heatmap / Timeline은 현재 앱 데이터만으로 바로 동작 가능

---

## 결론

`상세정보`를 없앤 뒤의 대체 화면은 단순 목록 페이지가 아니라 `Flow Workspace`가 맞다.

즉:

- Tree 클릭 = Flow 분석 진입
- 한 화면에서 상태, 편차, timeline, 태그, call 구조까지 본다
- 기존 개별 분석 페이지는 재사용 가능한 패널 공급원으로 본다

다음 구현 시작점은:

- `FlowWorkspace.razor` 생성
- Tree 클릭 navigation 연결
- `Header + KPI + Call Inventory` 1차 구현
