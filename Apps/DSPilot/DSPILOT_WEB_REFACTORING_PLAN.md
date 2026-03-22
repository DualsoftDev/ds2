# DSPilot Web 리팩토링 마스터 플랜

**작성일**: 2026-03-22  
**목표**: DSPilot.Engine 완전 통합 + UX 강화 + 실시간 모니터링 구현

---

## 📊 현재 상태 분석

### ✅ 완료된 항목
1. **DSPilot.Engine 핵심 모듈 100% 완성**
   - TagStateTracker (Edge Detection)
   - StateTransition (Direction 기반 상태 전이)
   - RuntimeStatsCollector (통계 수집)
   - PlcToCallMapperService (태그 매핑)

2. **기존 페이지**
   - Dashboard (Flow 카드, 통계)
   - FlowWorkspace (Call 상태 테이블)
   - CycleAnalysis (사이클 분석)
   - Heatmap (동작편차)
   - PlcDebug (PLC 디버그)

3. **백그라운드 서비스**
   - PlcCaptureService (PLC → DB)
   - PlcDataReaderService (DB 읽기)
   - FlowMetricsService (Flow 메트릭)
   - DspDatabaseService (DSP 테이블)

### ⚠️ 개선 필요 항목
1. **StateTransition F# 로직 미사용**
   - PlcEventProcessorService가 주석 처리됨
   - Direction 기반 상태 전이 미적용
   - 실시간 Call 상태 업데이트 없음

2. **실시간 모니터링 부족**
   - PLC 태그 실시간 변화 시각화 없음
   - Call 상태 전이 애니메이션 없음
   - 병목/에러 실시간 알림 없음

3. **UX 개선 필요**
   - 네비게이션 구조 개선
   - 반응형 디자인 미흡
   - 다크모드 미지원

---

## 🎯 리팩토링 목표

### 1. DSPilot.Engine 완전 통합
- F# StateTransition 로직 활용
- Direction별 상태 전이 자동화
- 실시간 통계 수집 및 표시

### 2. 실시간 모니터링 강화
- SignalR 기반 실시간 업데이트
- PLC 태그 라이브 모니터링
- Call 상태 변화 시각화

### 3. UX/UI 개선
- 직관적인 네비게이션
- 모던한 디자인 시스템
- 반응형 레이아웃

### 4. 성능 최적화
- DB 쿼리 최적화
- 렌더링 성능 개선
- 메모리 사용 최적화

---

## 📅 Phase별 실행 계획

각 Phase는 독립적으로 실행 가능하며, 순차적으로 진행합니다.

### Phase 1: DSPilot.Engine 핵심 통합 (우선순위: 최상)
**목표**: F# StateTransition 로직을 활용한 실시간 Call 상태 관리

**작업 항목**:
1. PlcEventProcessorService 리팩토링
2. StateTransition.fs 통합
3. Direction 기반 상태 전이 구현
4. RuntimeStatsCollector 전면 활용

**예상 기간**: 2-3일  
**상세 문서**: `PHASE1_ENGINE_INTEGRATION.md`

### Phase 2: 실시간 모니터링 대시보드 (우선순위: 상)
**목표**: SignalR 기반 실시간 모니터링 페이지 구축

**작업 항목**:
1. SignalR Hub 구현
2. Real-time Monitor 페이지 생성
3. PLC 태그 실시간 표시
4. Call 상태 애니메이션

**예상 기간**: 3-4일  
**상세 문서**: `PHASE2_REALTIME_MONITORING.md`

### Phase 3: Dashboard 위젯 개선 (우선순위: 중)
**목표**: Dashboard에 실시간 통계 및 상태 위젯 추가

**작업 항목**:
1. Direction별 Call 상태 위젯
2. 실시간 통계 차트
3. 병목 감지 알림 위젯
4. Flow 상태 히트맵

**예상 기간**: 2-3일  
**상세 문서**: `PHASE3_DASHBOARD_WIDGETS.md`

### Phase 4: UX/UI 개선 (우선순위: 중)
**목표**: 사용자 경험 전면 개선

**작업 항목**:
1. 네비게이션 재구성
2. 다크모드 구현
3. 반응형 디자인
4. 알림 시스템

**예상 기간**: 3-4일  
**상세 문서**: `PHASE4_UX_UI_IMPROVEMENT.md`

### Phase 5: 성능 최적화 및 정리 (우선순위: 하)
**목표**: 코드 품질 향상 및 성능 최적화

**작업 항목**:
1. 사용하지 않는 서비스 제거
2. DB 쿼리 최적화
3. 렌더링 최적화
4. 에러 처리 강화

**예상 기간**: 2-3일  
**상세 문서**: `PHASE5_OPTIMIZATION.md`

---

## 🗂️ 파일 구조

리팩토링 계획서는 다음 파일들로 구성됩니다:

```
Apps/DSPilot/
├── DSPILOT_WEB_REFACTORING_PLAN.md          (이 파일 - 마스터 플랜)
├── PHASE1_ENGINE_INTEGRATION.md             (Phase 1 상세)
├── PHASE2_REALTIME_MONITORING.md            (Phase 2 상세)
├── PHASE3_DASHBOARD_WIDGETS.md              (Phase 3 상세)
├── PHASE4_UX_UI_IMPROVEMENT.md              (Phase 4 상세)
├── PHASE5_OPTIMIZATION.md                   (Phase 5 상세)
└── REFACTORING_PROGRESS.md                  (진행 상황 추적)
```

---

## ✅ 성공 기준

### Phase 1 완료 기준
- [ ] PlcEventProcessorService가 StateTransition.fs 사용
- [ ] Direction별 상태 전이 동작 확인
- [ ] DB에 상태 변경 기록 확인
- [ ] 통계 수집 및 조회 가능

### Phase 2 완료 기준
- [ ] SignalR Hub 구현 및 연결 확인
- [ ] Real-time Monitor 페이지 동작
- [ ] PLC 태그 실시간 업데이트 확인
- [ ] Call 상태 변화 시각화 동작

### Phase 3 완료 기준
- [ ] Dashboard에 새 위젯 4개 추가
- [ ] 실시간 데이터 업데이트 확인
- [ ] 병목 감지 알림 동작

### Phase 4 완료 기준
- [ ] 다크모드 토글 동작
- [ ] 모바일/태블릿 반응형 확인
- [ ] 새 네비게이션 구조 적용

### Phase 5 완료 기준
- [ ] 사용하지 않는 코드 제거 완료
- [ ] 빌드 0 warnings 달성
- [ ] 성능 벤치마크 개선 확인

---

## 🚀 시작하기

1. 각 Phase 문서를 순서대로 읽고 이해합니다
2. Phase 1부터 순차적으로 구현합니다
3. 각 Phase 완료 후 테스트를 진행합니다
4. REFACTORING_PROGRESS.md에 진행 상황을 기록합니다

**다음 단계**: `PHASE1_ENGINE_INTEGRATION.md` 읽기
