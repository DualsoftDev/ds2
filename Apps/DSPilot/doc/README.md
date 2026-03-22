# DSPilot.Engine 리팩토링 문서

**작성일**: 2025-03-22
**목적**: DSPilot.Engine을 Projection 패턴 기반으로 재설계

---

## 📚 문서 목록

현재 작성된 문서:

1. **[00_INDEX.md](./00_INDEX.md)** - 전체 문서 인덱스 및 읽는 순서
2. **[01_ARCHITECTURE.md](./01_ARCHITECTURE.md)** - 전체 아키텍처 및 원칙
3. **[02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md)** - dspFlow/dspCall 스키마 설계
4. **[03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md)** - Projection 패턴 상세 설명
5. **[04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md)** - 이벤트 처리 파이프라인
6. **[09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md)** - 단계별 리팩토링 계획
7. **[10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md)** - 데이터베이스 마이그레이션 가이드
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

## 🔧 구현 상태

### ✅ 완료

- 문서 작성 (9개 문서)
- 아키텍처 설계
- 스키마 설계
- Projection 패턴 정의
- Repository API 설계
- 마이그레이션 가이드

### 🚧 진행 예정

- Phase 1: Database Schema Migration
- Phase 2: Repository Patch Methods
- Phase 3: WorkName Bug Fix
- Phase 4: Bootstrap Module Redesign
- Phase 5: Runtime Event Handler
- Phase 6: Statistics Calculator
- Phase 7: Flow Aggregation
- Phase 8: C# Service Simplification

---

## 📞 참조

### 원본 설계 문서

이 문서들은 다음 설계 문서를 기반으로 작성되었습니다:

- ~~`DSP_TABLE_FEATURE_UPDATE_PLAN.md`~~ (삭제됨, 내용은 이 문서들에 반영)
- ~~기타 .md 파일들~~ (doc 폴더로 이동 및 재구성)

### 관련 코드

- `DSPilot.Engine/Database/` - F# Repository 구현
- `DSPilot.Engine/Bootstrap/` - AASX 로드 로직
- `DSPilot/Services/` - C# 서비스 계층

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

- **2025-03-22**: 초안 작성 (9개 문서 완료)

---

## 💡 기여 방법

문서 개선 제안이나 오류 발견 시:
1. 해당 문서를 직접 수정
2. 변경 이유를 커밋 메시지에 명시
3. 관련 문서도 함께 업데이트

---

**Happy Coding! 🚀**
