# DSPilot 문서

**최종 업데이트**: 2026-03-23
**목적**: DSPilot 시스템 아키텍처 및 구현 가이드

---

## ℹ️ 문서 상태

이 문서들은 **설계 문서 (Design Documents)** 입니다. 실제 구현은 다음과 같이 진행되었습니다:

- ✅ **구현 완료**: C# Services 기반 PLC 이벤트 처리, 사이클 분석
- ⚠️ **부분 구현**: F# Engine (기본 구조만, 통계 미구현)
- 📝 **설계만 존재**: 일부 고급 기능 (Focus Score, 실시간 통계 등)

**중요**: 코드와 문서 간 차이가 있을 수 있습니다. 실제 구현은 코드를 참조하세요.

---

## 📚 주요 문서

### 핵심 아키텍처
1. **[00_INDEX.md](./00_INDEX.md)** - 전체 문서 인덱스 및 읽는 순서
2. **[01_ARCHITECTURE.md](./01_ARCHITECTURE.md)** - ✅ **업데이트 완료** - 현재 C#/F# 하이브리드 아키텍처
3. **[02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md)** - ✅ **업데이트 완료** - 실제 DB 스키마 (Migration 001 기준)
4. **[03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md)** - Projection 패턴 이론 (설계)
5. **[04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md)** - ✅ **업데이트 완료** - 실제 이벤트 처리 흐름

### 구현 가이드
6. **[09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md)** - 리팩토링 계획 (설계)
7. **[10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md)** - DB 마이그레이션 가이드
8. **[11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md)** - F# 모듈 구조
9. **[12_REPOSITORY_API.md](./12_REPOSITORY_API.md)** - Repository API 설계

---

## 🚀 빠른 시작

### 처음 읽는 분

1. [00_INDEX.md](./00_INDEX.md) - 문서 구성 파악
2. [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 그림 이해
3. [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - 핵심 패턴 학습

### 구현하려는 분

1. [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - Phase별 구현 순서 확인
2. [10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md) - DB 마이그레이션 실행
3. [11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md) - F# 모듈 구조 파악
4. [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository API 구현

---

## 🎯 핵심 원칙

### 1. Projection 패턴

dspFlow와 dspCall은 **UI가 즉시 사용 가능한 읽기 전용 모델**입니다.

```
UI는 계산 금지 → Projection에서 읽기만
모든 계산은 DSPilot.Engine에서 사전 수행
```

### 2. 세 가지 계산 책임

1. **Static Bootstrap**: AASX 로드 시 정적 메타데이터 설정
2. **Runtime Event**: PLC 이벤트 수신 시 실시간 상태 업데이트
3. **Aggregate Recompute**: Call 완료 시 통계 및 집계 계산

### 3. DROP TABLE 금지

```sql
-- ❌ 금지
DROP TABLE IF EXISTS dspFlow;

-- ✅ 허용
ALTER TABLE dspFlow ADD COLUMN NewField TEXT;
```

누적 통계를 보존하기 위해 Migration 기반 스키마 관리를 사용합니다.

### 4. WorkName 정확도

```fsharp
// ❌ 잘못된 코드
{ WorkName = flow.Name }

// ✅ 올바른 코드
{ WorkName = work.Name }
```

---

## 📋 아직 작성되지 않은 문서

다음 문서들은 추후 작성 예정입니다:

- `05_UPDATE_RULES.md` - 이벤트별 업데이트 규칙
- `06_AGGREGATION.md` - 집계 계산 규칙
- `07_STATISTICS.md` - 통계 계산 알고리즘
- `08_FOCUS_SCORE.md` - Focus Score 계산 로직

---

## 🔧 구현 상태 (2026-03-23 기준)

### ✅ 구현 완료

**C# Services Layer**:
- ✅ PlcEventProcessorService - Channel 기반 PLC 이벤트 처리
- ✅ PlcToCallMapperService - AASX Tag 매핑 (메모리 기반)
- ✅ CycleAnalysisService - 사이클 경계 탐지 및 분석
- ✅ DsProjectService - AASX 프로젝트 관리
- ✅ MonitoringBroadcastService - SignalR 실시간 브로드캐스트
- ✅ PlcDatabaseMonitorService - DB 폴링 및 UI 업데이트

**F# Engine**:
- ✅ DspRepository - Dapper 기반 SQLite I/O (bulkInsert만)
- ✅ TagStateTracker - Edge 감지 (Rising/Falling)
- ✅ StateTransition - 기본 구조 (실제 DB 업데이트 미구현)
- ✅ Entities - DspFlowEntity, DspCallEntity 정의

**Database**:
- ✅ Migration 001 - 최소 스키마 (dspFlow, dspCall 기본 필드만)
- ✅ Unified 모드 - EV2와 DSP가 하나의 DB 공유

### ⚠️ 부분 구현

- ⚠️ StateTransition - DB 업데이트 로직 미완성 (로그만 출력)
- ⚠️ 통계 계산 - 설계는 존재하나 미구현
- ⚠️ DB 스키마 - Migration 001만 존재 (전체 필드 미추가)

### 📝 설계만 존재 (미구현)

- 📝 Focus Score 계산
- 📝 실시간 통계 업데이트 (Incremental Average, StdDev)
- 📝 Flow-level 집계 (MT, WT, CT)
- 📝 SlowFlag, UnmappedFlag 자동 판정
- 📝 Cycle Metrics (사이클 완료 시 자동 계산)

---

## 📞 참조

### 관련 코드 위치

**C# Services** (DSPilot/Services/):
- `PlcEventProcessorService.cs` - PLC 이벤트 처리 메인 서비스
- `PlcToCallMapperService.cs` - Tag → Call 매핑
- `CycleAnalysisService.cs` - 사이클 분석 (1125 lines)
- `DsProjectService.cs` - AASX 프로젝트 관리
- `Ev2PlcEventSource.cs` - 실제 PLC 연결

**F# Engine** (DSPilot.Engine/):
- `Database/Entities.fs` - DspFlowEntity, DspCallEntity
- `Database/Repository.fs` - SQLite I/O (Dapper)
- `Tracking/StateTransition.fs` - 상태 전이 로직
- `Tracking/TagStateTracker.fs` - Edge 감지
- `Core/EdgeDetection.fs` - EdgeType 정의

**UI** (DSPilot/Components/Pages/):
- `CycleAnalysis.razor` - 사이클 분석 페이지
- `CycleTimeAnalysis.razor` - Gantt 차트 페이지
- `Dashboard.razor` - 대시보드

### 주요 기술 스택

- **언어**: C# 13 (.NET 9), F# 9
- **Database**: SQLite (Dapper ORM)
- **UI**: Blazor Server (SignalR)
- **PLC 통신**: Ev2.Backend.PLC (S7 Protocol)
- **Reactive**: System.Reactive (Rx.NET)

---

## ⚠️ 중요 공지

### 문서 vs 구현

- 이 문서들은 **계획 문서**입니다
- 실제 코드와 다를 수 있습니다
- 구현 전 최신 코드 상태를 확인하세요
- 변경 시 관련 문서도 함께 업데이트하세요

### 프론트엔드 구현

- **현재는 백엔드(DSPilot.Engine) 리팩토링에 집중**
- 프론트엔드(Blazor UI) 구현은 나중에 진행
- Flow Detail View 등 UI 작업은 Phase 8 이후

---

## 📅 버전 이력

- **2026-03-23**: 문서 업데이트 - 실제 구현 상태 반영
  - 01_ARCHITECTURE.md: C#/F# 하이브리드 구조 반영
  - 02_DATABASE_SCHEMA.md: Migration 001 실제 스키마 반영
  - 04_EVENT_PIPELINE.md: 실제 이벤트 처리 흐름 반영
  - README.md: 구현 상태 명시
- **2025-03-22**: 초안 작성 (9개 설계 문서 완료)

---

## 💡 기여 방법

문서 개선 제안이나 오류 발견 시:
1. 해당 문서를 직접 수정
2. 변경 이유를 커밋 메시지에 명시
3. 관련 문서도 함께 업데이트

---

**Happy Coding! 🚀**
