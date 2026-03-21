# 사이클 분석 페이지 설계

## 📋 목적

생산 라인의 **동작 순서 분석**을 통해 병목 구간과 최적화 포인트를 찾기 위한 분석 도구

## 🎯 핵심 기능

### 1. 시간 기반 동작 순서 정렬
- **목적**: 실제 동작 순서를 시간순으로 시각화
- **표시 정보**:
  - Call 이름
  - 시작 시간 (절대 시간)
  - 종료 시간 (절대 시간)
  - 소요 시간 (Duration)
  - 상태 (Running/Completed/Error)

### 2. 대기 시간(Gap) 분석
- **목적**: 동작 간 대기 시간이 긴 구간 찾기
- **분석 항목**:
  - **Top 3 긴 Gap**: 가장 긴 대기 시간 1위, 2위, 3위
  - Gap 발생 위치 (이전 Call → 다음 Call)
  - Gap 지속 시간
  - Gap 발생 빈도 (반복 패턴 감지)

### 3. 장치별 상대 시간 분석
- **목적**: 각 장치(Device/Station)의 동작 시간 비율 분석
- **분석 항목**:
  - 장치별 총 동작 시간
  - 전체 사이클 시간 대비 비율 (%)
  - 장치별 평균 동작 시간
  - 장치별 최소/최대 동작 시간

### 4. 병목 구간 탐지
- **목적**: 사이클 타임을 지연시키는 주요 원인 찾기
- **탐지 기준**:
  - 평균보다 2배 이상 긴 동작
  - 반복적으로 지연되는 구간
  - Critical Path 상의 긴 동작

### 5. 사이클 비교
- **목적**: 정상 사이클 vs 느린 사이클 비교
- **비교 항목**:
  - 각 Call별 시간 차이
  - 추가된 대기 시간
  - 빠진 동작 탐지

## 🎨 UI 레이아웃

```
┌─────────────────────────────────────────────────────────┐
│ 사이클 분석                                              │
├─────────────────────────────────────────────────────────┤
│ [사이클 선택] [시작시간 ~ 종료시간]  [분석 실행]        │
├─────────────────────────────────────────────────────────┤
│                                                           │
│ 📊 사이클 요약                                            │
│ ┌──────────┬──────────┬──────────┬──────────┐           │
│ │총 시간    │Call 수   │평균 시간  │Gap 총합  │           │
│ │  45.2s   │   24     │  1.88s   │  8.5s   │           │
│ └──────────┴──────────┴──────────┴──────────┘           │
│                                                           │
│ 🔴 Top 3 긴 대기 시간 (Gap)                               │
│ ┌────┬─────────────────────────┬─────────┐              │
│ │순위│ 구간                     │ 시간     │              │
│ ├────┼─────────────────────────┼─────────┤              │
│ │ 1  │ Call_A → Call_B         │ 5.2s    │              │
│ │ 2  │ Call_C → Call_D         │ 2.1s    │              │
│ │ 3  │ Call_E → Call_F         │ 1.2s    │              │
│ └────┴─────────────────────────┴─────────┘              │
│                                                           │
│ 📈 동작 순서 타임라인 (Gantt Chart)                        │
│ ┌─────────────────────────────────────────────────┐     │
│ │  0s        10s       20s       30s       40s     │     │
│ │  ├─────────┼─────────┼─────────┼─────────┤       │     │
│ │ Call_A  ████                                      │     │
│ │ Call_B      ███                                   │     │
│ │ Call_C         ██████                             │     │
│ │ ...                                               │     │
│ └─────────────────────────────────────────────────┘     │
│                                                           │
│ 📊 장치별 시간 분포                                        │
│ ┌──────────────────────────────────────────┐            │
│ │ Station 1: ████████████ 45%              │            │
│ │ Station 2: ████████ 30%                  │            │
│ │ Station 3: █████ 25%                     │            │
│ └──────────────────────────────────────────┘            │
│                                                           │
│ 📋 상세 Call 목록 (시간순 정렬)                            │
│ ┌────┬──────────┬──────────┬──────────┬────────┐        │
│ │순서│Call 이름  │시작 시간  │소요 시간  │Gap     │        │
│ ├────┼──────────┼──────────┼──────────┼────────┤        │
│ │ 1  │ Call_A   │ 00:00.0  │  2.5s    │ -      │        │
│ │ 2  │ Call_B   │ 00:02.5  │  1.8s    │ 0s     │        │
│ │ 3  │ Call_C   │ 00:09.5  │  3.2s    │ 5.2s ⚠│        │
│ │ ...│ ...      │ ...      │ ...      │ ...    │        │
│ └────┴──────────┴──────────┴──────────┴────────┘        │
└─────────────────────────────────────────────────────────┘
```

## 📊 데이터 구조

### CycleAnalysisData
```csharp
public class CycleAnalysisData
{
    // 사이클 기본 정보
    public DateTime CycleStartTime { get; set; }
    public DateTime CycleEndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public int CallCount { get; set; }

    // 동작 목록 (시간순 정렬)
    public List<CallExecutionInfo> CallSequence { get; set; }

    // Gap 분석
    public List<GapInfo> Gaps { get; set; }
    public List<GapInfo> TopLongGaps { get; set; }  // Top 3
    public TimeSpan TotalGapDuration { get; set; }

    // 장치별 분석
    public Dictionary<string, DeviceTimeInfo> DeviceStats { get; set; }

    // 병목 탐지
    public List<BottleneckInfo> Bottlenecks { get; set; }
}
```

### CallExecutionInfo
```csharp
public class CallExecutionInfo
{
    public int SequenceNumber { get; set; }        // 실행 순서 (1, 2, 3...)
    public string CallName { get; set; }
    public string FlowName { get; set; }
    public string DeviceName { get; set; }         // Station/Device 이름

    public DateTime StartTime { get; set; }        // 절대 시작 시간
    public DateTime EndTime { get; set; }          // 절대 종료 시간
    public TimeSpan Duration { get; set; }         // 소요 시간

    public TimeSpan RelativeStartTime { get; set; } // 사이클 시작 대비 상대 시간
    public TimeSpan GapFromPrevious { get; set; }   // 이전 Call과의 Gap

    public CallState State { get; set; }            // Running/Completed/Error
}
```

### GapInfo
```csharp
public class GapInfo
{
    public int Rank { get; set; }                   // 순위 (1, 2, 3...)
    public string PreviousCall { get; set; }
    public string NextCall { get; set; }
    public TimeSpan GapDuration { get; set; }
    public DateTime GapStartTime { get; set; }
    public DateTime GapEndTime { get; set; }
    public bool IsBottleneck { get; set; }          // 병목 여부
}
```

### DeviceTimeInfo
```csharp
public class DeviceTimeInfo
{
    public string DeviceName { get; set; }
    public TimeSpan TotalTime { get; set; }         // 총 동작 시간
    public double PercentageOfCycle { get; set; }   // 전체 사이클 대비 %
    public int CallCount { get; set; }              // 동작 횟수
    public TimeSpan AverageTime { get; set; }       // 평균 시간
    public TimeSpan MinTime { get; set; }
    public TimeSpan MaxTime { get; set; }
}
```

### BottleneckInfo
```csharp
public class BottleneckInfo
{
    public string CallName { get; set; }
    public string Reason { get; set; }              // "2배 이상 긴 동작", "반복 지연"
    public TimeSpan Duration { get; set; }
    public TimeSpan ExpectedDuration { get; set; }  // 평균 또는 목표 시간
    public double DelayRatio { get; set; }          // 지연 배율
}
```

## 🔧 서비스 구조

### CycleAnalysisService
```csharp
public class CycleAnalysisService
{
    // 사이클 데이터 로드
    Task<CycleAnalysisData> AnalyzeCycleAsync(
        DateTime cycleStart,
        DateTime cycleEnd);

    // Gap 분석
    List<GapInfo> AnalyzeGaps(List<CallExecutionInfo> sequence);
    List<GapInfo> GetTopLongGaps(List<GapInfo> gaps, int topN = 3);

    // 장치별 분석
    Dictionary<string, DeviceTimeInfo> AnalyzeDeviceTime(
        List<CallExecutionInfo> sequence);

    // 병목 탐지
    List<BottleneckInfo> DetectBottlenecks(
        List<CallExecutionInfo> sequence);

    // 사이클 비교
    CycleComparisonResult CompareCycles(
        CycleAnalysisData cycle1,
        CycleAnalysisData cycle2);
}
```

## 📈 분석 알고리즘

### 1. Gap 계산
```
For i = 0 to CallSequence.Count - 2:
    Gap[i] = CallSequence[i+1].StartTime - CallSequence[i].EndTime
    If Gap[i] > threshold:
        Mark as potential bottleneck
```

### 2. Top 3 긴 Gap 선정
```
1. Gap 리스트를 시간 기준 내림차순 정렬
2. 상위 3개 선택
3. Rank 부여 (1, 2, 3)
```

### 3. 병목 탐지
```
평균 Duration 계산
For each Call:
    If Call.Duration > Average * 2:
        Add to Bottleneck list
        Reason = "평균보다 2배 이상 긴 동작"
```

### 4. 장치별 시간 집계
```
For each Device:
    TotalTime = Sum(Call.Duration where Call.Device == Device)
    Percentage = TotalTime / CycleTotalTime * 100
    Average = TotalTime / CallCount
```

## 🎨 차트 종류

### 1. Gantt Chart (필수)
- X축: 시간 (0s ~ 사이클 종료)
- Y축: Call 이름
- 막대: Call 실행 구간
- 색상: 장치별 구분
- Gap: 빈 공간으로 표시 (빨간색 테두리)

### 2. Gap 순위 바 차트
- X축: Gap 순위 (1, 2, 3)
- Y축: Gap 시간 (초)
- 색상: 빨간색 (경고)

### 3. 장치별 시간 파이 차트
- 각 장치의 시간 비율 표시
- 호버 시 상세 정보 (총 시간, %, 횟수)

## 🔍 필터 및 옵션

### 필터
- **시간 범위**: 특정 시간대의 사이클만 분석
- **Flow 선택**: 특정 Flow의 Call만 표시
- **장치 선택**: 특정 장치의 동작만 분석

### 옵션
- **Gap 임계값**: N초 이상의 Gap만 표시 (기본: 0.5초)
- **병목 기준**: 평균의 N배 이상 (기본: 2배)
- **정렬 기준**: 시간순/Duration순/Gap순

## 🚀 구현 단계

### Phase 1: 기본 분석 (MVP)
1. ✅ CycleAnalysisData 모델 정의
2. ✅ 시간순 Call 정렬
3. ✅ Gap 계산 및 Top 3 선정
4. ✅ Gantt Chart 표시

### Phase 2: 고급 분석
1. ✅ 장치별 시간 분석
2. ✅ 병목 탐지 알고리즘
3. ✅ 파이 차트 추가

### Phase 3: 비교 및 최적화
1. ⬜ 사이클 간 비교
2. ⬜ 추세 분석 (여러 사이클)
3. ⬜ 최적화 제안

## 💡 사용 시나리오

### 시나리오 1: 느린 사이클 분석
```
1. 사용자가 평소보다 느린 사이클 선택
2. Top 3 Gap 확인 → "Call_C → Call_D 사이에 5.2초 대기"
3. Gantt Chart에서 해당 구간 확인
4. 원인 파악: Call_C 완료 후 Call_D 시작 신호 지연
5. 개선: PLC 로직 수정 또는 센서 위치 조정
```

### 시나리오 2: 장치 부하 분석
```
1. 장치별 시간 분포 차트 확인
2. Station 1이 45% 차지 → 과부하 의심
3. Station 1의 Call 목록 상세 분석
4. 일부 동작을 Station 2로 이동 고려
```

### 시나리오 3: 병목 구간 찾기
```
1. 병목 리스트 확인
2. "Call_X: 평균보다 3배 긴 동작" 발견
3. 해당 Call의 히스토리 분석
4. 특정 조건에서만 느려지는 패턴 발견
5. 조건부 최적화 적용
```

## 📝 참고사항

### 데이터 소스
- `dspFlow` 테이블: Flow 정보
- `dspCall` 테이블: Call 정보 (이름, Device 등)
- Call 상태 변경 이벤트: 시작/종료 시간 추적

### 성능 고려사항
- 사이클당 Call 수: 평균 20~50개
- 분석 대상: 1개 사이클 (실시간)
- 응답 시간 목표: < 500ms

### 제약사항
- 사이클 경계 정의 필요 (시작/종료 조건)
- Call 실행 순서가 항상 동일하지 않을 수 있음
- 병렬 실행되는 Call 처리 필요

## 🎯 성공 지표

1. **분석 정확도**: Gap 탐지율 > 95%
2. **사용 편의성**: 3클릭 이내에 병목 구간 확인 가능
3. **응답 속도**: 사이클 분석 < 500ms
4. **개선 효과**: 사이클 타임 10% 이상 단축 달성
