# DSPilot.Engine 리팩토링 문서 인덱스

## 📚 문서 구성

이 문서들은 DSPilot.Engine의 리팩토링 계획을 담고 있습니다.
**프론트엔드 구현은 나중에 진행**하며, 백엔드(Engine) 설계에 집중합니다.

---

## 📋 문서 목록

### 1. 기본 설계 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처 및 원칙
- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - dspFlow/dspCall 스키마 설계
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴 상세

### 2. 이벤트 처리

- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 이벤트 처리 파이프라인
- [05_FEATURE_IMPLEMENTATION.md](./05_FEATURE_IMPLEMENTATION.md) - 기능별 구현 가이드

### 3. 계산 로직

- [06_AGGREGATION.md](./06_AGGREGATION.md) - 집계 계산 규칙
- [07_STATISTICS.md](./07_STATISTICS.md) - 통계 계산 알고리즘
- [08_FOCUS_SCORE.md](./08_FOCUS_SCORE.md) - Focus Score 계산

### 4. 리팩토링 계획

- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 단계별 리팩토링 계획
- [10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md) - 데이터 마이그레이션 가이드

### 5. F# 모듈 설계

- [11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md) - F# 모듈 구조 및 책임
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - DspRepository API 설계

### 6. 단계별 구현 가이드 (Step-by-Step)

- [13_STEP_BY_STEP_IMPLEMENTATION.md](./13_STEP_BY_STEP_IMPLEMENTATION.md) - 전체 로드맵 및 Step 0, 1
- [14_STEP_02_PLC_EVENT.md](./14_STEP_02_PLC_EVENT.md) - Step 2: PLC Event Handling
- [15_STEP_03_STATISTICS.md](./15_STEP_03_STATISTICS.md) - Step 3: Statistics Calculation
- [16_STEP_04_BOTTLENECK_DETECTION.md](./16_STEP_04_BOTTLENECK_DETECTION.md) - Step 4: Bottleneck Detection
- [17_STEP_05_GANTT_CHART.md](./17_STEP_05_GANTT_CHART.md) - Step 5: Gantt Chart (Cycle Analysis)
- [18_STEP_06_HEATMAP.md](./18_STEP_06_HEATMAP.md) - Step 6: Heatmap (Deviation Analysis)

---

## 🎯 핵심 원칙 (참조: DSP_TABLE_FEATURE_UPDATE_PLAN.md)

1. **Projection 테이블**
   - dspFlow, dspCall은 화면용 읽기 모델
   - UI는 계산 금지, 순수 표시만

2. **계산 책임 분리**
   - Static Bootstrap: AASX 로드 시
   - Runtime Event: PLC 이벤트 시
   - Aggregate Recompute: 집계 계산

3. **DROP TABLE 금지**
   - Migration 기반 스키마 관리
   - 누적 통계 보존

4. **WorkName 정확도**
   - flow.Name 아님, work.Name 사용

---

## 📖 읽는 순서

### 처음 읽는 사람
1. 01_ARCHITECTURE.md (전체 구조 파악)
2. 03_PROJECTION_PATTERN.md (핵심 패턴 이해)
3. 09_REFACTORING_PLAN.md (구현 계획 확인)

### 구현하는 사람
1. 13_STEP_BY_STEP_IMPLEMENTATION.md (전체 로드맵 파악)
2. 14_STEP_02_PLC_EVENT.md (Step 2부터 시작)
3. 15_STEP_03_STATISTICS.md (Step 3 통계)
4. 16-18 (Step 4-6: Bottleneck, Gantt, Heatmap)

### F# 개발자
1. 11_FSHARP_MODULES.md (모듈 구조)
2. 12_REPOSITORY_API.md (API 설계)
3. 06_AGGREGATION.md (집계 로직)

---

## 🔗 외부 참조

- [DSP_TABLE_FEATURE_UPDATE_PLAN.md](../DSP_TABLE_FEATURE_UPDATE_PLAN.md) - 기능 중심 설계 원본
- [DSPilot.Engine/Database/](../DSPilot.Engine/Database/) - 현재 코드
- [DSPilot/Services/](../DSPilot/Services/) - C# 서비스 계층

---

## ⚠️ 주의사항

- 이 문서들은 **계획 문서**이며, 실제 구현과 다를 수 있습니다
- 구현 전 최신 코드 상태를 확인하세요
- 변경 시 관련 문서도 함께 업데이트하세요

---

## 📅 버전 이력

- 2025-03-22: 초안 작성 (DSP_TABLE_FEATURE_UPDATE_PLAN.md 기반)
