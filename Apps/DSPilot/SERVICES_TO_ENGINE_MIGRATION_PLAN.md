# DSPilot Services -> DSPilot.Engine 이동 계획

작성일: 2026-03-20

## 1. 결론

`DSPilot/Services`를 `DSPilot.Engine`으로 **전부 옮기는 것은 권장하지 않는다**.

유리한 방향은 아래다.

1. `HostedService`, UI, 파일/설정, EV2 adapter는 `DSPilot/Services`에 남긴다
2. 순수 계산, 상태 전이, 분석, projection 생성 로직만 `DSPilot.Engine`으로 옮긴다
3. C# service 클래스는 얇은 orchestration wrapper로 줄인다

즉, **"서비스 전체 이동"이 아니라 "서비스 안의 코어 로직 추출"이 목표**다.

---

## 2. 판단 기준

### Engine으로 가야 하는 것

- 순수 함수
- 상태 전이 규칙
- edge detection
- 통계 계산
- cycle 분석
- flow 분석
- DB projection 생성 규칙
- EV2/DSP DB 스키마 bootstrap 규칙

### Services에 남아야 하는 것

- `BackgroundService`, `IHostedService`
- DI scope 생성
- `ILogger`, `IConfiguration`, `IWebHostEnvironment`, `IHostApplicationLifetime` 중심 코드
- 파일 입출력
- Blazor/UI snapshot 조립
- EV2 `GlobalCommunication`, `PLCBackendService` 구독/발행
- repository 호출 orchestration

---

## 3. 현재 구조 해석

현재 [DSPilot.Engine.fsproj](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot.Engine/DSPilot.Engine.fsproj) 에는 이미 아래가 들어 있다.

- `StateTransition.fs`
- `Statistics.fs`
- `EdgeDetection.fs`
- `CycleAnalysis.fs`
- `FlowAnalysis.fs`
- `DspRepository.fs`
- `DspDatabaseInit.fs`
- `Ev2Bootstrap.fs`

즉 `DSPilot.Engine`은 이미 "코어 로직/DB projection" 방향으로 가고 있다.

반면 [DSPilot/Services](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services) 에는 아래가 섞여 있다.

- 웹/앱 인프라
- 백그라운드 orchestration
- EV2 adapter
- UI projection
- 일부 도메인 계산 로직

따라서 해야 할 일은 **Services를 Engine으로 통째로 옮기는 것**이 아니라, **Services 안의 계산 로직을 Engine으로 점진적으로 빼는 것**이다.

---

## 4. 파일별 분류

## 4.1 그대로 Services에 남길 것

### 앱/웹 인프라

- [AppSettingsService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/AppSettingsService.cs)
- [BlueprintService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/BlueprintService.cs)
- `DashboardEditService.cs`
- [DspDbService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/DspDbService.cs)
- [IDatabasePathResolver.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/IDatabasePathResolver.cs)

이유:

- 파일, 환경, 웹 호스트, UI 상태에 강하게 결합됨
- Engine에 넣으면 참조 방향이 오염됨

### 프로젝트/외부 시스템 adapter

- [DsProjectService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/DsProjectService.cs)
- [Ev2PlcEventSource.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/Ev2PlcEventSource.cs)
- [Ev2PlcEventSource.Real.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/Ev2PlcEventSource.Real.cs)
- [PlcCaptureService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/PlcCaptureService.cs)
- [SqlitePlcHistorySource.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/SqlitePlcHistorySource.cs)

이유:

- EV2, DsStore, DB adapter 성격
- 외부 의존이 많고 순수 로직이 아님

### 백그라운드 orchestration

- [PlcDataReaderService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/PlcDataReaderService.cs)
- [PlcEventProcessorService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/PlcEventProcessorService.cs)
- [InMemoryCallStateStore.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/InMemoryCallStateStore.cs)

이유:

- 채널, cancellation, subscription, DI scope, repository orchestration 중심
- Engine보다는 application service 계층에 가까움

---

## 4.2 일부만 Engine으로 추출할 것

### `CallStatisticsService`

대상 파일:

- [CallStatisticsService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/CallStatisticsService.cs)

추출 대상:

- going time 샘플 누적 규칙
- 평균/표준편차/카운트 계산 규칙
- 세션 카운트 누적 정책

Services에 남길 부분:

- DB에서 base count 로드
- `IServiceScopeFactory` 사용
- 로그 출력

권장 Engine 모듈:

- `RuntimeStatistics.fs`
- `ExecutionCounter.fs`

### `FlowMetricsService`

대상 파일:

- [FlowMetricsService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/FlowMetricsService.cs)

추출 대상:

- head/tail call 기반 cycle 추적 규칙
- MT/WT/CT 계산 규칙
- single-call flow 처리 규칙

Services에 남길 부분:

- `DsProjectService` 호출
- repository update
- 초기화 orchestration

권장 Engine 모듈:

- `RuntimeFlowMetrics.fs`
- `FlowCycleTracker.fs`

### `CycleAnalysisService`

대상 파일:

- [CycleAnalysisService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/CycleAnalysisService.cs)

추출 대상:

- cycle boundary 조합 로직
- IO event -> gantt/projection 조합 로직
- call별 duration 매칭 규칙

Services에 남길 부분:

- PLC log 조회
- AASX 구조 조회
- mapper/repository orchestration

권장 Engine 모듈:

- `CycleBoundaryDetection.fs`
- `GanttProjection.fs`
- `IoEventCorrelation.fs`

### `PlcToCallMapperService`

대상 파일:

- [PlcToCallMapperService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/PlcToCallMapperService.cs)

추출 대상:

- tag key 선택 규칙 (`Address`/`Name`)
- mapping dictionary 생성 규칙
- tag -> call lookup 규칙

Services에 남길 부분:

- `DsProjectService`에서 데이터 읽기
- 설정 읽기
- 초기화 시점 관리

권장 Engine 모듈:

- `CallMapping.fs`
- `TagKeySelector.fs`

### `HeatmapService`

대상 파일:

- [HeatmapService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/HeatmapService.cs)

추출 대상:

- 통계 -> heatmap item 변환 규칙
- score/color assignment 조합 로직

Services에 남길 부분:

- repository 조회
- UI용 grouping

권장 Engine 모듈:

- `HeatmapProjection.fs`

---

## 4.3 거의 전부 Engine으로 옮겨도 되는 후보

### `PlcTagStateTrackerService`

대상 파일:

- [PlcTagStateTrackerService.cs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot/Services/PlcTagStateTrackerService.cs)

이유:

- 이미 [EdgeDetection.fs](/mnt/c/ds/ds2/Apps/DSPilot/DSPilot.Engine/EdgeDetection.fs) 를 호출하는 얇은 상태 추적기
- 외부 의존이 적음
- in-memory dictionary와 edge 계산 로직만 있음

권장 방식:

- Engine에 `TagStateTracker` 타입으로 이동
- C# wrapper는 삭제하거나 최소 façade만 남김

---

## 5. 이동하지 말아야 하는 것

다음은 Engine으로 보내면 오히려 구조가 나빠진다.

- `AppSettingsService`
- `BlueprintService`
- `DashboardEditService`
- `DspDbService`
- `DsProjectService`
- `PlcCaptureService`
- `PlcDataReaderService`
- `PlcEventProcessorService`
- `Ev2PlcEventSource*`

이유는 공통이다.

- 호스트 수명주기 의존
- ASP.NET/Blazor 의존
- DI/orchestration 중심
- 외부 API adapter 성격

이 클래스들을 Engine으로 옮기면 Engine이 도메인 엔진이 아니라 앱 런타임 레이어가 된다.

---

## 6. 권장 목표 구조

```text
DSPilot.Engine
├─ Domain
│  ├─ StateTransition
│  ├─ EdgeDetection
│  ├─ RuntimeStatistics
│  ├─ FlowCycleTracker
│  ├─ CallMapping
│  └─ HeatmapProjection
│
├─ Analysis
│  ├─ CycleAnalysis
│  ├─ FlowAnalysis
│  ├─ CycleBoundaryDetection
│  └─ GanttProjection
│
├─ Persistence
│  ├─ DatabaseTypes
│  ├─ DspRepository
│  ├─ DspDatabaseInit
│  └─ Ev2Bootstrap
│
└─ Models
```

```text
DSPilot/Services
├─ Application Services
│  ├─ PlcDataReaderService
│  ├─ PlcEventProcessorService
│  ├─ FlowMetricsService
│  ├─ CycleAnalysisService
│  └─ HeatmapService
│
├─ Adapters
│  ├─ Ev2PlcEventSource.Real
│  ├─ PlcCaptureService
│  ├─ SqlitePlcHistorySource
│  └─ DsProjectService
│
└─ Web/App Infrastructure
   ├─ AppSettingsService
   ├─ BlueprintService
   └─ DspDbService
```

핵심은 Services를 없애는 게 아니라 **얇게 만드는 것**이다.

---

## 7. 단계별 이동 계획

## 7.1 Phase 1 - 경계 고정

목표:

- Engine과 Services의 책임을 명확히 문서화
- 신규 로직은 Engine 우선 원칙 적용

작업:

- 서비스별 분류 확정
- `Engine = pure/domain`, `Services = orchestration/adapter` 규칙 고정
- 신규 코드 리뷰 기준 추가

완료 기준:

- 서비스 전체 이동 논의 종료
- 추출 단위 기준 합의

## 7.2 Phase 2 - 순수 로직 추출

목표:

- 서비스 내부 계산 로직을 F# Engine 모듈로 추출

우선순위:

1. `PlcTagStateTrackerService`
2. `CallStatisticsService`
3. `FlowMetricsService`
4. `CycleAnalysisService`
5. `PlcToCallMapperService`
6. `HeatmapService`

완료 기준:

- 각 서비스의 계산 핵심이 Engine 함수/타입으로 이동
- 서비스는 입력 수집과 결과 반영만 담당

## 7.3 Phase 3 - 서비스 슬림화

목표:

- orchestration wrapper만 남기기

예시:

- `PlcEventProcessorService`
  - 채널/구독/저장 orchestration만 남김
  - 상태 전이 판단은 Engine 호출

- `PlcDataReaderService`
  - 이벤트 수신/로그 변환/DB 호출만 남김
  - 로그 해석 규칙은 Engine 호출

완료 기준:

- 서비스 클래스의 private 계산 메서드 대폭 축소
- 테스트 가능한 순수 함수 비율 증가

## 7.4 Phase 4 - 테스트 재정비

목표:

- 핵심 규칙 테스트를 Engine 단위로 이동

권장:

- 상태 전이 테스트
- edge detection 테스트
- 통계 계산 테스트
- cycle boundary 테스트
- flow metrics 테스트

완료 기준:

- 호스트 없이 실행 가능한 테스트가 증가
- engine test가 application test보다 핵심 규칙을 더 많이 커버

---

## 8. 구현 순서 추천

가장 안전한 순서는 아래다.

1. `PlcTagStateTrackerService` 추출
2. `CallStatisticsService` 계산부 추출
3. `FlowMetricsService` cycle 계산부 추출
4. `CycleAnalysisService` projection 계산부 추출
5. `PlcToCallMapperService` 매핑 규칙 추출
6. `HeatmapService` projection 추출
7. 마지막에 `PlcDataReaderService`, `PlcEventProcessorService` 슬림화

이 순서가 좋은 이유:

- 영향 범위가 작은 것부터 시작함
- Engine API를 먼저 안정화할 수 있음
- 마지막에 orchestration service 정리를 할 수 있음

---

## 9. 금지 사항

다음은 하지 않는 것이 좋다.

### 1. 서비스 클래스를 통째로 Engine으로 복사

문제:

- Engine이 ASP.NET/HostedService 계층을 먹어버림
- 참조 방향이 꼬임
- 테스트는 쉬워지지 않고 구조만 커짐

### 2. C# service를 F#로 기계적으로 재작성

문제:

- 언어 변경 비용만 크고 경계가 개선되지 않을 수 있음
- adapter/orchestration 코드는 굳이 F#일 이유가 약함

### 3. UI/호스트 의존 코드를 Engine으로 이동

문제:

- Engine 재사용성이 떨어짐
- 결국 다시 app layer가 필요해짐

---

## 10. 완료 기준

이 계획의 완료 기준은 아래다.

1. `DSPilot.Engine`이 상태 전이/분석/통계/매핑 규칙의 단일 소스가 된다
2. `DSPilot/Services`는 호스팅, 외부 연동, orchestration 중심으로 얇아진다
3. 서비스 내부의 계산 private method가 줄어든다
4. 핵심 테스트가 Engine 단위에서 가능해진다

---

## 11. 최종 정리

질문에 대한 최종 답은 아래다.

- **전부 옮기는 것은 불리하다**
- **순수 로직만 Engine으로 빼는 것은 유리하다**

실행 원칙은 한 줄로 정리된다.

**"서비스는 흐름을 연결하고, Engine은 판단하고 계산한다."**
