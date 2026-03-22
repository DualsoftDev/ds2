# DSPilot.Engine 리팩토링 계획

## 🎯 목표

DSPilot.Engine을 **Projection 패턴 기반**으로 재설계하여:
1. UI 계산 부담 제거 (순수 읽기 전용)
2. 실시간 성능 향상 (사전 계산된 Projection)
3. 확장성 확보 (Patch 기반 업데이트)
4. 통계 손실 방지 (DROP TABLE 제거)

---

## 📋 리팩토링 우선순위

### Phase 1: 데이터베이스 스키마 확장 (필수)

**목표**: dspFlow/dspCall 테이블에 모든 Projection 필드 추가

**작업 항목**:

1. **Migration 스크립트 작성**
   ```sql
   -- Migration: 001_add_projection_fields.sql

   -- dspFlow 확장
   ALTER TABLE dspFlow ADD COLUMN SystemName TEXT;
   ALTER TABLE dspFlow ADD COLUMN WorkName TEXT;
   ALTER TABLE dspFlow ADD COLUMN SequenceNo INTEGER;
   ALTER TABLE dspFlow ADD COLUMN IsHead INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN IsTail INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN ErrorCallCount INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN LastCycleStartAt TEXT;
   ALTER TABLE dspFlow ADD COLUMN LastCycleEndAt TEXT;
   ALTER TABLE dspFlow ADD COLUMN LastCycleNo INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN LastCycleDurationMs REAL;
   ALTER TABLE dspFlow ADD COLUMN AverageCT REAL;
   ALTER TABLE dspFlow ADD COLUMN StdDevCT REAL;
   ALTER TABLE dspFlow ADD COLUMN MinCT REAL;
   ALTER TABLE dspFlow ADD COLUMN MaxCT REAL;
   ALTER TABLE dspFlow ADD COLUMN CompletedCycleCount INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN SlowCycleFlag INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN UnmappedCallCount INTEGER DEFAULT 0;
   ALTER TABLE dspFlow ADD COLUMN FocusScore INTEGER DEFAULT 0;

   -- dspCall 확장
   ALTER TABLE dspCall ADD COLUMN SystemName TEXT;
   ALTER TABLE dspCall ADD COLUMN IsHead INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN IsTail INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN SequenceNo INTEGER;
   ALTER TABLE dspCall ADD COLUMN InTag TEXT;
   ALTER TABLE dspCall ADD COLUMN OutTag TEXT;
   ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
   ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;
   ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL;
   ALTER TABLE dspCall ADD COLUMN CurrentCycleNo INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN MinGoingTime REAL;
   ALTER TABLE dspCall ADD COLUMN MaxGoingTime REAL;
   ALTER TABLE dspCall ADD COLUMN ErrorCount INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN SlowFlag INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN UnmappedFlag INTEGER DEFAULT 0;
   ALTER TABLE dspCall ADD COLUMN FocusScore INTEGER DEFAULT 0;
   ```

2. **F# Entity 타입 확장**
   - `DSPilot.Engine/Database/Entities.fs` 업데이트
   - 모든 새 필드 추가

3. **C# DTO 타입 확장**
   - `DSPilot/Models/Dsp/DspFlowEntity.cs` 업데이트
   - `DSPilot/Models/Dsp/DspCallEntity.cs` 업데이트

**검증**:
- Migration 실행 후 기존 통계 데이터 보존 확인
- 새 필드 기본값 확인

---

### Phase 2: Repository Patch 메서드 구현 (필수)

**목표**: 좁은 업데이트 메서드를 Patch 기반으로 교체

**작업 항목**:

1. **Patch 타입 정의**
   ```fsharp
   // DSPilot.Engine/Database/Dtos.fs

   type FlowPatch =
       { FlowName: string
         SystemName: string option
         WorkName: string option
         MovingStartName: string option
         MovingEndName: string option
         SequenceNo: int option
         IsHead: bool option
         IsTail: bool option
         State: string option
         ActiveCallCount: int option
         ErrorCallCount: int option
         LastCycleStartAt: DateTime option
         LastCycleEndAt: DateTime option
         LastCycleNo: int option
         MT: float option
         WT: float option
         CT: float option
         LastCycleDurationMs: float option
         AverageCT: float option
         StdDevCT: float option
         MinCT: float option
         MaxCT: float option
         CompletedCycleCount: int option
         SlowCycleFlag: bool option
         UnmappedCallCount: int option
         FocusScore: int option }

   type CallPatch =
       { CallId: Guid
         CallName: string option
         ApiCall: string option
         WorkName: string option
         FlowName: string option
         SystemName: string option
         Next: string option
         Prev: string option
         IsHead: bool option
         IsTail: bool option
         SequenceNo: int option
         Device: string option
         InTag: string option
         OutTag: string option
         State: string option
         ProgressRate: float option
         LastStartAt: DateTime option
         LastFinishAt: DateTime option
         LastDurationMs: float option
         CurrentCycleNo: int option
         AverageGoingTime: float option
         StdDevGoingTime: float option
         MinGoingTime: float option
         MaxGoingTime: float option
         GoingCount: int option
         ErrorCount: int option
         ErrorText: string option
         SlowFlag: bool option
         UnmappedFlag: bool option
         FocusScore: int option }
   ```

2. **Patch 메서드 구현**
   ```fsharp
   // DSPilot.Engine/Database/Repository.fs

   let patchFlow (patch: FlowPatch) : Async<unit> =
       async {
           let updates = [
               if patch.SystemName.IsSome then "SystemName = @SystemName"
               if patch.WorkName.IsSome then "WorkName = @WorkName"
               if patch.State.IsSome then "State = @State"
               if patch.ActiveCallCount.IsSome then "ActiveCallCount = @ActiveCallCount"
               if patch.MT.IsSome then "MT = @MT"
               if patch.WT.IsSome then "WT = @WT"
               if patch.CT.IsSome then "CT = @CT"
               // ... 모든 필드
           ]

           if updates.IsEmpty then
               return ()
           else
               let sql = sprintf "UPDATE dspFlow SET %s, UpdatedAt = datetime('now') WHERE FlowName = @FlowName"
                                (String.concat ", " updates)
               use conn = new SqliteConnection(getConnectionString())
               let! _ = conn.ExecuteAsync(sql, patch) |> Async.AwaitTask
               return ()
       }

   let patchCall (patch: CallPatch) : Async<unit> =
       // 동일한 패턴으로 구현
       async { /* ... */ }
   ```

3. **기존 메서드 Deprecate**
   - `updateFlowMetricsAsync` → `patchFlow` 사용
   - `updateCallWithStatisticsByIdAsync` → `patchCall` 사용

**검증**:
- Patch 메서드로 개별 필드 업데이트 테스트
- 변경되지 않은 필드 보존 확인

---

### Phase 3: WorkName 버그 수정 (중요)

**목표**: Call의 WorkName을 정확하게 설정

**현재 문제**:
```fsharp
// ❌ 잘못된 코드
{ WorkName = flow.Name }
```

**수정 방법**:
```fsharp
// ✅ 올바른 코드
{ WorkName = work.Name }
```

**작업 항목**:

1. **Bootstrap 코드 수정**
   ```fsharp
   // DSPilot.Engine/Ev2Bootstrap.fs

   let bootstrapFromAasx (aasxPath: string) =
       // AASX 파싱
       let systems = Ev2AasxParser.parseSystems aasxPath

       systems
       |> Seq.collect (fun sys ->
           sys.Flows
           |> Seq.collect (fun flow ->
               flow.Works  // Work 계층 순회
               |> Seq.collect (fun work ->
                   work.Calls
                   |> Seq.map (fun call ->
                       { CallId = call.Id
                         CallName = call.Name
                         WorkName = work.Name  // ⚠️ work.Name 사용!
                         FlowName = flow.Name
                         SystemName = sys.Name
                         // ...
                       }))))
       |> Seq.toList
       |> DspRepository.bulkInsertCalls
   ```

2. **기존 데이터 마이그레이션**
   ```sql
   -- 기존 잘못된 WorkName 수정 (프로젝트 재로드로 해결)
   -- 또는 별도 스크립트로 수정
   ```

**검증**:
- Bootstrap 후 dspCall.WorkName이 정확한지 확인
- Flow Detail View에서 Work 그룹핑 정상 동작 확인

---

### Phase 4: Bootstrap 모듈 재설계 (필수)

**목표**: AASX 로드 시 정적 메타데이터를 완전하게 Projection에 반영

**작업 항목**:

1. **Topology 계산 추가**
   ```fsharp
   // DSPilot.Engine/Bootstrap/TopologyCalculator.fs

   let calculateTopology (calls: Call list) =
       calls
       |> List.sortBy (fun c -> c.SequenceNo)
       |> List.mapi (fun i call ->
           let prev = if i > 0 then Some calls.[i-1].Name else None
           let next = if i < calls.Length - 1 then Some calls.[i+1].Name else None
           let isHead = i = 0
           let isTail = i = calls.Length - 1

           { call with
               Prev = prev
               Next = next
               IsHead = isHead
               IsTail = isTail })
   ```

2. **Tag Mapping 추가**
   ```fsharp
   // DSPilot.Engine/Bootstrap/TagMapper.fs

   let mapCallToTags (call: Call) (plcMapping: PlcMapping) =
       let inTag = plcMapping.FindInTag(call.Name)
       let outTag = plcMapping.FindOutTag(call.Name)

       { call with
           InTag = inTag
           OutTag = outTag
           UnmappedFlag = inTag.IsNone || outTag.IsNone }
   ```

3. **UPSERT 로직 개선**
   ```fsharp
   // 통계 보존하며 메타데이터 업데이트
   let upsertFlow (flow: DspFlowEntity) =
       async {
           use conn = new SqliteConnection(getConnectionString())
           let sql = """
               INSERT INTO dspFlow (FlowName, SystemName, WorkName, MovingStartName, MovingEndName,
                                     SequenceNo, IsHead, IsTail, State, CreatedAt, UpdatedAt)
               VALUES (@FlowName, @SystemName, @WorkName, @MovingStartName, @MovingEndName,
                       @SequenceNo, @IsHead, @IsTail, @State, datetime('now'), datetime('now'))
               ON CONFLICT(FlowName) DO UPDATE SET
                   SystemName = excluded.SystemName,
                   WorkName = excluded.WorkName,
                   MovingStartName = excluded.MovingStartName,
                   MovingEndName = excluded.MovingEndName,
                   SequenceNo = excluded.SequenceNo,
                   IsHead = excluded.IsHead,
                   IsTail = excluded.IsTail,
                   -- 통계는 COALESCE로 기존 값 유지
                   AverageCT = COALESCE(dspFlow.AverageCT, excluded.AverageCT),
                   CompletedCycleCount = COALESCE(dspFlow.CompletedCycleCount, excluded.CompletedCycleCount),
                   UpdatedAt = datetime('now')
           """
           let! _ = conn.ExecuteAsync(sql, flow) |> Async.AwaitTask
           return ()
       }
   ```

**검증**:
- Bootstrap 후 Topology 정보 확인 (Prev, Next, IsHead, IsTail)
- Tag Mapping 정보 확인 (InTag, OutTag, UnmappedFlag)
- 기존 통계 보존 확인

---

### Phase 5: Runtime Event 처리 재설계 (필수)

**목표**: PLC 이벤트를 Projection에 실시간 반영

**작업 항목**:

1. **StateTransition 모듈 생성**
   ```fsharp
   // DSPilot.Engine/Tracking/StateTransition.fs

   let handleInTagRisingEdge (callId: Guid) (timestamp: DateTime) =
       async {
           // State: Ready → Going
           let patch =
               { CallId = callId
                 State = Some "Going"
                 LastStartAt = Some timestamp
                 ProgressRate = Some 0.5
                 CurrentCycleNo = None  // Increment는 별도 로직
               }

           do! DspRepository.patchCall patch

           // ActiveCallCount 증가
           let call = DspRepository.getCallById callId
           do! FlowMetricsCalculator.incrementActiveCount call.FlowName
       }

   let handleOutTagRisingEdge (callId: Guid) (timestamp: DateTime) =
       async {
           // State: Going → Done
           let call = DspRepository.getCallById callId
           let duration = (timestamp - call.LastStartAt.Value).TotalMilliseconds

           let patch =
               { CallId = callId
                 State = Some "Done"
                 LastFinishAt = Some timestamp
                 LastDurationMs = Some duration
                 ProgressRate = Some 1.0
               }

           do! DspRepository.patchCall patch

           // 통계 업데이트 트리거
           do! StatisticsCalculator.updateCallStatistics callId duration

           // ActiveCallCount 감소
           do! FlowMetricsCalculator.decrementActiveCount call.FlowName
       }
   ```

2. **PlcEventProcessorService 수정**
   ```csharp
   // DSPilot/Services/PlcEventProcessorService.cs

   private async Task ProcessPlcEventAsync(PlcTagEvent evt)
   {
       // Rising Edge 감지
       if (!IsRisingEdge(evt)) return;

       // Tag → Call 매핑
       var callOpt = await _mapper.FindCallByTagAsync(evt.TagName);
       if (callOpt == null) return;

       // InTag vs OutTag 판정
       if (evt.TagName == callOpt.InTag)
       {
           // F# 모듈 호출
           await FSharpAsync.StartAsTask(
               StateTransition.handleInTagRisingEdge(callOpt.CallId, evt.Timestamp),
               null, null);
       }
       else if (evt.TagName == callOpt.OutTag)
       {
           await FSharpAsync.StartAsTask(
               StateTransition.handleOutTagRisingEdge(callOpt.CallId, evt.Timestamp),
               null, null);
       }
   }
   ```

**검증**:
- PLC 이벤트 수신 시 State 변경 확인
- LastStartAt, LastFinishAt 기록 확인
- ActiveCallCount 증감 확인

---

### Phase 6: Statistics 모듈 구현 (필수)

**목표**: Incremental Statistics 계산

**작업 항목**:

1. **Statistics 모듈 생성**
   ```fsharp
   // DSPilot.Engine/Statistics/Statistics.fs

   let updateCallStatistics (callId: Guid) (newDuration: float) =
       async {
           let! call = DspRepository.getCallById callId

           let n = call.GoingCount + 1
           let oldAvg = call.AverageGoingTime |> Option.defaultValue 0.0
           let oldStdDev = call.StdDevGoingTime |> Option.defaultValue 0.0
           let oldMin = call.MinGoingTime |> Option.defaultValue newDuration
           let oldMax = call.MaxGoingTime |> Option.defaultValue newDuration

           // Incremental Average
           let newAvg = (oldAvg * float(n - 1) + newDuration) / float(n)

           // Incremental StdDev (Welford's method)
           let oldM2 = oldStdDev * oldStdDev * float(n - 1)
           let delta = newDuration - oldAvg
           let newAvgAdjusted = oldAvg + delta / float(n)
           let delta2 = newDuration - newAvgAdjusted
           let newM2 = oldM2 + delta * delta2
           let newStdDev = sqrt(newM2 / float(n))

           // Min/Max
           let newMin = min oldMin newDuration
           let newMax = max oldMax newDuration

           // SlowFlag 판정
           let slowFlag = newDuration > (newAvg + 2.0 * newStdDev)

           // FocusScore 계산
           let focusScore = FocusScoreCalculator.calculate call slowFlag

           let patch =
               { CallId = callId
                 AverageGoingTime = Some newAvg
                 StdDevGoingTime = Some newStdDev
                 MinGoingTime = Some newMin
                 MaxGoingTime = Some newMax
                 GoingCount = Some n
                 SlowFlag = Some slowFlag
                 FocusScore = Some focusScore
               }

           do! DspRepository.patchCall patch
       }
   ```

2. **FocusScore 계산**
   ```fsharp
   // DSPilot.Engine/Statistics/FocusScore.fs

   let calculate (call: DspCallEntity) (slowFlag: bool) =
       let mutable score = 0

       // Error: +100
       if call.State = "Error" then score <- score + 100

       // Unmapped: +70
       if call.UnmappedFlag then score <- score + 70

       // Slow: +50
       if slowFlag then score <- score + 50

       // High StdDev: +30
       let stdDevRatio =
           match call.AverageGoingTime, call.StdDevGoingTime with
           | Some avg, Some stdDev when avg > 0.0 -> stdDev / avg
           | _ -> 0.0

       if stdDevRatio > 0.3 then score <- score + 30

       score
   ```

**검증**:
- Call 완료 시 통계 업데이트 확인
- SlowFlag 판정 정확도 확인
- FocusScore 계산 확인

---

### Phase 7: Flow Aggregation 모듈 구현 (필수)

**목표**: Flow 레벨 집계 계산

**작업 항목**:

1. **FlowMetricsCalculator 모듈 생성**
   ```fsharp
   // DSPilot.Engine/Statistics/FlowMetrics.fs

   let updateFlowMetrics (flowName: string) =
       async {
           let! calls = DspRepository.getCallsByFlow flowName

           // 집계 계산
           let activeCount = calls |> Seq.filter (fun c -> c.State = "Going") |> Seq.length
           let errorCount = calls |> Seq.filter (fun c -> c.State = "Error") |> Seq.length
           let unmappedCount = calls |> Seq.filter (fun c -> c.UnmappedFlag) |> Seq.length

           // Flow State 결정
           let flowState =
               if errorCount > 0 then "Error"
               elif activeCount > 0 then "Going"
               else "Ready"

           // Cycle Metrics (Tail Call 완료 시만)
           let tailCallOpt = calls |> Seq.tryFind (fun c -> c.IsTail)
           let headCallOpt = calls |> Seq.tryFind (fun c -> c.IsHead)

           let cycleMetrics =
               match tailCallOpt, headCallOpt with
               | Some tail, Some head when tail.State = "Done" && tail.LastFinishAt.IsSome && head.LastStartAt.IsSome ->
                   let mt = calls |> Seq.sumBy (fun c -> c.LastDurationMs |> Option.defaultValue 0.0)
                   let ct = (tail.LastFinishAt.Value - head.LastStartAt.Value).TotalMilliseconds
                   let wt = ct - mt
                   Some (mt, wt, ct)
               | _ ->
                   None

           // Projection Update
           let patch =
               { FlowName = flowName
                 State = Some flowState
                 ActiveCallCount = Some activeCount
                 ErrorCallCount = Some errorCount
                 UnmappedCallCount = Some unmappedCount
                 MT = cycleMetrics |> Option.map (fun (m,_,_) -> m)
                 WT = cycleMetrics |> Option.map (fun (_,w,_) -> w)
                 CT = cycleMetrics |> Option.map (fun (_,_,c) -> c)
                 // 나머지 필드는 None
               }

           do! DspRepository.patchFlow patch
       }

   let incrementActiveCount (flowName: string) =
       async {
           let! flow = DspRepository.getFlowByName flowName
           let patch =
               { FlowName = flowName
                 ActiveCallCount = Some (flow.ActiveCallCount + 1)
                 State = Some "Going"
               }
           do! DspRepository.patchFlow patch
       }

   let decrementActiveCount (flowName: string) =
       async {
           let! flow = DspRepository.getFlowByName flowName
           let newCount = max 0 (flow.ActiveCallCount - 1)
           let newState = if newCount = 0 then "Ready" else "Going"
           let patch =
               { FlowName = flowName
                 ActiveCallCount = Some newCount
                 State = Some newState
               }
           do! DspRepository.patchFlow patch
       }
   ```

**검증**:
- Tail Call 완료 시 MT/WT/CT 계산 확인
- ActiveCallCount 실시간 업데이트 확인
- Flow State 전이 확인

---

### Phase 8: C# Service Layer 단순화 (선택)

**목표**: C# 서비스 계층에서 계산 로직 제거, F# 모듈 호출만 수행

**작업 항목**:

1. **CallStatisticsService 삭제**
   - 통계 계산은 F# Statistics 모듈로 이동
   - C#에서는 단순 호출만

2. **FlowMetricsService 단순화**
   ```csharp
   // DSPilot/Services/FlowMetricsService.cs

   public class FlowMetricsService
   {
       public async Task UpdateFlowMetricsAsync(string flowName)
       {
           // F# 모듈 호출
           await FSharpAsync.StartAsTask(
               FlowMetricsCalculator.updateFlowMetrics(flowName),
               null, null);
       }
   }
   ```

3. **PlcEventProcessorService 단순화**
   - 이벤트 라우팅만 담당
   - 계산 로직은 F#로 이동

**검증**:
- C# 서비스가 F# 모듈을 정상 호출하는지 확인
- 기능 동작 변경 없음 확인

---

## 🚧 구현 순서

```
Phase 1: Database Schema
   ↓
Phase 2: Repository Patch Methods
   ↓
Phase 3: WorkName Bug Fix
   ↓
Phase 4: Bootstrap Module
   ↓
Phase 5: Runtime Event Handler
   ↓
Phase 6: Statistics Calculator
   ↓
Phase 7: Flow Aggregation
   ↓
Phase 8: C# Service Simplification
```

---

## ✅ 검증 체크리스트

### Phase 1 완료 기준
- [ ] Migration 스크립트 실행 성공
- [ ] 기존 통계 데이터 보존 확인
- [ ] F# Entity 타입 빌드 성공
- [ ] C# DTO 타입 빌드 성공

### Phase 2 완료 기준
- [ ] `patchFlow`, `patchCall` 메서드 구현
- [ ] 개별 필드 업데이트 테스트 통과
- [ ] 변경되지 않은 필드 보존 확인

### Phase 3 완료 기준
- [ ] Bootstrap 코드에서 `work.Name` 사용 확인
- [ ] dspCall.WorkName 정확도 검증
- [ ] Flow Detail View Work 그룹핑 동작 확인

### Phase 4 완료 기준
- [ ] Topology 계산 정확도 검증 (Prev, Next, IsHead, IsTail)
- [ ] Tag Mapping 정확도 검증 (InTag, OutTag)
- [ ] UPSERT 시 통계 보존 확인

### Phase 5 완료 기준
- [ ] InTag Rising Edge → State 전이 확인
- [ ] OutTag Rising Edge → State 전이 확인
- [ ] ActiveCallCount 증감 확인
- [ ] Timestamp 기록 확인

### Phase 6 완료 기준
- [ ] Incremental Average 정확도 검증
- [ ] Incremental StdDev 정확도 검증
- [ ] SlowFlag 판정 정확도 검증
- [ ] FocusScore 계산 정확도 검증

### Phase 7 완료 기준
- [ ] MT/WT/CT 계산 정확도 검증
- [ ] Flow State 전이 정확도 검증
- [ ] ActiveCallCount/ErrorCallCount 집계 정확도 검증

### Phase 8 완료 기준
- [ ] C# → F# 호출 정상 동작 확인
- [ ] 기능 회귀 테스트 통과
- [ ] 성능 저하 없음 확인

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 스키마 설계
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴
- [10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md) - 마이그레이션 가이드
- [11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md) - F# 모듈 구조
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository API
