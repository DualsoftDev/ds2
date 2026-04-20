# 사이클 타임 분석 페이지 설계

## 개요
PLC 데이터베이스의 태그 로그를 분석하여 Call별 사이클 타임을 계산하고 시각화하는 페이지

## 목적
- Flow 내 각 Call의 사이클 타임 모니터링
- 성능 병목 구간 식별
- 공정 최적화를 위한 데이터 제공

---

## 1. 페이지 구조

### 1.1 URL 및 기본 정보
- **Route**: `/cycle-time-analysis`
- **Page Title**: "Cycle Time Analysis - DSPilot"
- **Render Mode**: InteractiveServer

### 1.2 의존성 (Inject)
```csharp
@inject PlcToCallMapperService CallMapper
@inject IPlcRepository PlcRepository
@inject IJSRuntime JS
```

---

## 2. UI 레이아웃

### 2.1 페이지 헤더
```
┌─────────────────────────────────────────────────────┐
│ 사이클 타임 분석                                    │
│                                                     │
│  [Flow 선택 ▼]  [분석 시작]                       │
└─────────────────────────────────────────────────────┘
```

**구성 요소:**
- Flow 선택 드롭다운
  - PlaceholderISBN: "-- Flow 선택 --"
  - 데이터 소스: `CallMapper.GetAllMappings()` → 중복 제거된 FlowName 목록
- "분석 시작" 버튼
  - Flow 미선택 시 비활성화
  - 클릭 시 `LoadData()` 실행

### 2.2 로딩/에러 상태

**로딩 중:**
```
┌─────────────────────────────────────────────────────┐
│           ⟳ (스피너)                               │
│      데이터 로딩 중...                              │
└─────────────────────────────────────────────────────┘
```

**에러 발생:**
```
┌─────────────────────────────────────────────────────┐
│  ⚠ [에러 메시지]                                   │
└─────────────────────────────────────────────────────┘
```

### 2.3 Call별 사이클 타임 테이블

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Call별 사이클 타임                                                       │
├──────────────┬────────┬──────────┬──────────┬──────────┬──────────────┤
│ Call Name    │사이클수│ 평균(ms) │ 최소(ms) │ 최대(ms) │ 표준편차(ms) │
├──────────────┼────────┼──────────┼──────────┼──────────┼──────────────┤
│ Call A       │   150  │  234.5   │  210.0   │  280.0   │   12.3       │
│ Call B       │   148  │  189.2   │  175.0   │  220.0   │    8.7       │
│ ...          │   ...  │  ...     │  ...     │  ...     │   ...        │
└──────────────┴────────┴──────────┴──────────┴──────────┴──────────────┘
```

**테이블 특징:**
- 평균 사이클 타임 기준 내림차순 정렬
- 숫자는 monospace 폰트로 우측 정렬
- 소수점 1자리까지 표시
- Hover 시 배경색 변경

### 2.4 빈 상태
```
┌─────────────────────────────────────────────────────┐
│                                                     │
│    Flow를 선택하고 '분석 시작' 버튼을 클릭하세요   │
│                                                     │
└─────────────────────────────────────────────────────┘
```

---

## 3. 데이터 모델

### 3.1 상태 변수
```csharp
private List<string> _flows = new();              // Flow 목록
private string _selectedFlowName = "";             // 선택된 Flow 이름
private bool _isLoading = false;                   // 로딩 상태
private string? _errorMessage;                     // 에러 메시지
private List<CallCycleData> _callCycles = new();   // Call별 사이클 데이터
```

### 3.2 CallCycleData 클래스
```csharp
private class CallCycleData
{
    public string CallName { get; set; } = "";
    public List<double> CycleTimes { get; set; } = new();
    public int CycleCount { get; set; }
    public double AverageCycleTime { get; set; }
    public double MinCycleTime { get; set; }
    public double MaxCycleTime { get; set; }
    public double StdDeviation { get; set; }
}
```

---

## 4. 핵심 로직

### 4.1 Flow 목록 로드 (`LoadFlows`)
```csharp
private Task LoadFlows()
{
    // 1. CallMapper.GetAllMappings() 호출
    // 2. FlowName 추출 및 중복 제거
    // 3. 알파벳순 정렬
    // 4. _flows 리스트에 저장
}
```

### 4.2 사이클 데이터 로드 (`LoadData`)

**단계:**

1. **초기화**
   ```csharp
   _isLoading = true;
   _errorMessage = null;
   _callCycles.Clear();
   ```

2. **Call 매핑 조회**
   ```csharp
   var mappings = CallMapper.GetAllMappings()
       .Where(m => m.FlowName == _selectedFlowName)
       .GroupBy(m => m.Call.Id)
       .ToList();
   ```

3. **각 Call별 처리** (병렬)
   ```csharp
   foreach (var callGroup in mappings)
   {
       // a. Call 정보 추출
       var call = callGroup.First().Call;

       // b. InTag/OutTag 조회
       var tags = CallMapper.GetCallTagsByCallId(call.Id);
       var (inTag, outTag) = tags.Value;

       // c. 태그 로그 조회 (최근 24시간)
       var logs = await PlcRepository.GetTagLogsByTimeRangeAsync(
           inTag,
           DateTime.Now.AddHours(-24),
           DateTime.Now
       );

       // d. 사이클 타임 계산
       var cycleTimes = CalculateCycleTimes(logs);

       // e. 통계 계산
       return new CallCycleData {
           CallName = call.Name,
           CycleTimes = cycleTimes,
           CycleCount = cycleTimes.Count,
           AverageCycleTime = cycleTimes.Average(),
           MinCycleTime = cycleTimes.Min(),
           MaxCycleTime = cycleTimes.Max(),
           StdDeviation = CalculateStdDev(cycleTimes)
       };
   }
   ```

4. **결과 저장**
   ```csharp
   _callCycles = results.Where(r => r != null).ToList();
   _isLoading = false;
   ```

### 4.3 사이클 타임 계산 (`CalculateCycleTimes`)

**알고리즘:**
```
입력: PlcTagLogEntity 리스트 (시간순 정렬)
출력: 사이클 타임 리스트 (밀리초)

1. 초기화:
   - cycleTimes = []
   - lastRisingEdge = null

2. 로그 순회 (i = 1 부터):
   prevValue = logs[i-1].Value (정규화: "true"/"1" → true)
   currValue = logs[i].Value (정규화: "true"/"1" → true)

   IF Rising Edge 감지 (prevValue == false AND currValue == true):
       IF lastRisingEdge != null:
           cycleTime = (logs[i].DateTime - lastRisingEdge).TotalMilliseconds

           IF cycleTime > 0 AND cycleTime < 60000:  // 1분 미만 필터링
               cycleTimes.Add(cycleTime)

       lastRisingEdge = logs[i].DateTime

3. RETURN cycleTimes
```

**Rising Edge 정의:**
- 이전 값이 `false` (또는 "0")
- 현재 값이 `true` (또는 "1")
- InTag의 Rising Edge = Call 시작 시점

### 4.4 표준편차 계산 (`CalculateStdDev`)
```csharp
private double CalculateStdDev(List<double> values)
{
    if (values.Count < 2) return 0;

    var avg = values.Average();
    var sumOfSquares = values.Sum(v => Math.Pow(v - avg, 2));
    return Math.Sqrt(sumOfSquares / (values.Count - 1));
}
```

---

## 5. 데이터 흐름

```
사용자 Flow 선택
    ↓
[분석 시작] 클릭
    ↓
LoadData() 실행
    ↓
CallMapper → Flow의 모든 Call 조회
    ↓
각 Call별로:
    ↓
    CallId → InTag 주소 조회
    ↓
    PlcRepository → 태그 로그 조회 (24시간)
    ↓
    CalculateCycleTimes() → Rising Edge 감지
    ↓
    통계 계산 (평균, 최소, 최대, 표준편차)
    ↓
CallCycleData 리스트 생성
    ↓
테이블에 표시 (평균 기준 내림차순)
```

---

## 6. 기술 스택

### 6.1 백엔드 서비스
- **PlcToCallMapperService**: Call ↔ Tag 매핑
- **IPlcRepository**: PLC 태그 로그 조회

### 6.2 프론트엔드
- **Blazor Server**: InteractiveServer 렌더 모드
- **CSS Grid**: 테이블 레이아웃
- **Material Icons**: 아이콘

### 6.3 데이터베이스
- **SQLite**: `plcTagLog` 테이블
  - 컬럼: `TagAddress`, `Value`, `DateTime`

---

## 7. 성능 고려사항

### 7.1 최적화
- **병렬 처리**: `Task.WhenAll()` 사용하여 각 Call 병렬 분석
- **시간 범위 제한**: 최근 24시간 데이터만 조회
- **비현실적 값 필터링**: 60초 이상 사이클 타임 제외

### 7.2 제약사항
- InTag가 없는 Call은 분석 제외
- 사이클 데이터가 없는 Call은 결과에서 제외

---

## 8. UI/UX 가이드

### 8.1 색상 및 스타일
- **Primary Color**: CSS 변수 `--color-primary`
- **Surface Color**: CSS 변수 `--surface-color`
- **Border Color**: CSS 변수 `--border-color`
- **Text Colors**: `--text-primary`, `--text-secondary`

### 8.2 반응형 디자인
- **1024px 이하**: 최소/최대 컬럼 숨김
- **768px 이하**: 테이블 단일 컬럼 레이아웃

### 8.3 인터랙션
- **로딩 중**: 버튼 비활성화, 스피너 표시
- **에러 발생**: 빨간색 경고 박스 표시
- **Hover**: 테이블 행 하이라이트

---

## 9. 에러 핸들링

### 9.1 가능한 에러
1. **Flow 로딩 실패**: CallMapper 초기화 안됨
2. **태그 로그 조회 실패**: DB 연결 문제
3. **매핑 정보 없음**: Call에 InTag 미설정

### 9.2 에러 표시
```html
<div class="alert alert-error">
    <span class="material-icons">error</span>
    [에러 메시지]
</div>
```

---

## 10. 확장 가능성

### 10.1 향후 추가 가능 기능
- 시간 범위 선택 (1시간, 12시간, 7일 등)
- CSV/Excel 내보내기
- 사이클 타임 추세 차트
- Call별 상세 히스토그램
- 이상치(Outlier) 감지 및 표시
- 실시간 업데이트 (SignalR)

### 10.2 개선 포인트
- 페이지네이션 (Call 수가 많을 경우)
- 검색 및 필터링 기능
- 다중 Flow 비교 모드

---

## 11. 테스트 시나리오

### 11.1 기본 시나리오
1. 페이지 로드 → Flow 목록 표시 확인
2. Flow 선택 → "분석 시작" 버튼 활성화 확인
3. "분석 시작" 클릭 → 로딩 표시 확인
4. 분석 완료 → 테이블에 데이터 표시 확인
5. 데이터 정렬 → 평균 기준 내림차순 확인

### 11.2 엣지 케이스
1. InTag 없는 Call → 결과에서 제외
2. 사이클 데이터 없음 → 빈 상태 메시지 표시
3. 네트워크 에러 → 에러 메시지 표시
4. Flow 재선택 → 이전 데이터 클리어 확인

---

## 12. 구현 체크리스트

- [ ] CycleTimeAnalysis.razor 파일 생성
- [ ] CycleTimeAnalysis.razor.css 파일 생성
- [ ] NavMenu에 링크 추가
- [ ] LoadFlows() 구현
- [ ] LoadData() 구현
- [ ] CalculateCycleTimes() 구현
- [ ] CalculateStdDev() 구현
- [ ] CallCycleData 클래스 정의
- [ ] 테이블 UI 구현
- [ ] 로딩/에러 상태 UI 구현
- [ ] CSS 스타일링 완료
- [ ] 반응형 레이아웃 테스트
- [ ] 빌드 및 실행 테스트
- [ ] 실제 데이터로 검증

---

## 참고 문서
- PlcToCallMapperService API
- IPlcRepository 인터페이스
- PlcTagLogEntity 모델
- DSPilot 통합 데이터베이스 구조
