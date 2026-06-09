# Simulation 이벤트 로그 → 통합 Log 탭 병합 계획

작성일: 2026-06-09
목적: Promaker 의 SimulationPanel 안 "이벤트 로그" 탭을 제거하고 MainWindow 하단 도크 anchor 인 통합 **Log** 탭으로 일원화한다.

## 1. 현재 구조

### 1.1 통합 Log (SSOT 후보)
- [Logging/WpfObservableAppender.cs](../Logging/WpfObservableAppender.cs) — log4net appender · 모든 `Log.Info/Warn/Error/...` 호출이 본 path 로 수렴.
- [ViewModels/Logging/AppLogState.cs](../ViewModels/Logging/AppLogState.cs) — singleton · 16ms coalesce · 5000-entry ring buffer · UI dispatcher marshal.
- [ViewModels/Logging/AppLogEntry.cs](../ViewModels/Logging/AppLogEntry.cs) — `{ seq, timestamp:DateTime, Level:log4net.Level, Logger, Message, Display }`.
- [ViewModels/Logging/LogLevelChoice.cs](../ViewModels/Logging/LogLevelChoice.cs) — Debug / Info / Warn 필터 (Error · Fatal 상시 표시).
- [Controls/Logging/AppLogView.xaml(.cs)](../Controls/Logging/) — `log` 도크 anchor (MainWindow.xaml.cs:79 등록).

### 1.2 Simulation 이벤트 로그 (제거 대상)
- 탭 위치: [Controls/Simulation/SimulationPanel.xaml:82-139](../Controls/Simulation/SimulationPanel.xaml) — TabControl 내 "이벤트 로그" TabItem.
- 상태:
  - [ViewModels/Simulation/Core/SimulationPanelState.cs:21](../ViewModels/Simulation/Core/SimulationPanelState.cs#L21) — `enum LogSeverity { Info, Warn, Error, Timeout, Ready, Going, Finish, Homing, System }` (9개).
  - [ViewModels/Simulation/Core/SimulationPanelState.cs:23](../ViewModels/Simulation/Core/SimulationPanelState.cs#L23) — `class SimLogEntry(message, severity)` (timestamp 별도 없음).
  - [ViewModels/Simulation/Core/SimulationPanelState.cs:501](../ViewModels/Simulation/Core/SimulationPanelState.cs#L501) — `ObservableCollection<SimLogEntry> SimEventLog`.
- 시간: 엔진 클럭 `TimeSpan` 을 prepend ("[HH:mm:ss.fff]") — wall clock 아님.
- 디스크: `Lifecycle.cs:162-176` 의 `AddSimLog` 가 `MyDocuments/ds2_eventlog_{RuntimeMode}.txt` 에 background append.
- 호출처 **74개** (11 파일):
  - `Runner/Runner.Start.cs` (16) · `Runner/Runner.Homing.cs` (21) · `Core/Lifecycle.cs` (10)
  - `Core/Events.cs` (3) · `Core/RuntimeMode.cs` (4) · `Runner/Runner.Step.cs` (1)
  - `Interaction/Token.cs` / `ForceWork.cs` / `Hub/Manual.cs` (합 7)
  - `Hub/SimulationHubBridge.Lifecycle.cs` (Agent 위임 안내 등)
  - 클리어: `SimulationPanel.xaml.cs` 의 reset 핸들러 + `SimulationPanelState.ResetForNewStore`.

## 2. 스키마 비교

| 필드 | AppLogEntry (통합) | SimLogEntry (현행) |
|---|---|---|
| timestamp | `DateTime` (wall) | 없음 — `AddSimLog` 가 엔진 `TimeSpan` 을 메시지 prefix 로 박제 |
| level | log4net.Level (5단) | LogSeverity enum (9단 — 일부는 상태값) |
| logger/category | string ("Promaker.X") | 없음 |
| message | string | string |
| 표시 색상 | Level 기반 (DataTrigger) | Severity 기반 (Ready/Going/Finish/Homing 등 도메인 상태 색) |

**결정 필요**: Ready/Going/Finish/Homing 같은 시뮬레이션 상태 색상을 통합 Log 에서도 유지할지. 가능 → `AppLogEntry` 에 `Category` 필드 추가 + `AppLogView` 색 DataTrigger 확장.

## 3. 접근 방안 비교

### 안 A · 완전 병합 (권장)
`AddSimLog(msg, severity)` 를 통합 `Log.Info/Warn/Error(...)` (logger=`"Simulation"`) 호출로 전환.

| 장점 | 단점 |
|---|---|
| SSOT 단일화, 유지보수 단순 | Severity 9단 → Level 5단 매핑 손실 (상태 색 별도 처리 필요) |
| 호출 site 자동 fan-in (74개 → 1개 appender) | wall clock 으로 통일 (엔진 클럭 prefix 별도 라인 추가 필요) |
| 파일 백업은 RollingFileAppender 가 자동 처리 | per-RuntimeMode 분리 파일 손실 (사용자 영향 평가 필요) |
| SimLogEntry / DiagnosticLogWriter 통째 삭제 | 테스트 2건 재작성 |

### 안 B · Filtered View
SimEventLog 를 `AppLogState` 의 `logger=="Simulation"` 필터 view 로 대체. 컬렉션 2중화는 유지.

| 장점 | 단점 |
|---|---|
| Sim 색상 / 클리어 정책 보존 | "통합" 목표 미달 — TabItem 만 사라지고 클래스 그대로 |
| 74개 호출 site 무변경 | 메모리 2중 표시 / 동기화 race 위험 |

→ **안 A 채택.** 상태색 손실은 `AppLogEntry` 에 optional `Category` 필드 + `AppLogView` 의 Sim 색 DataTrigger 확장으로 보존.

## 4. 마이그레이션 단계

### Phase 1 — AppLog 측 확장
1. `AppLogEntry` 에 `Category` (string?) 필드 추가. 기본값 null → 기존 Display 동일.
2. `AppLogState.Enqueue` 가 `Category` 인자 받도록 overload.
3. `AppLogView.xaml` 에 Category="Simulation" + Severity-Like prefix 별 색 DataTrigger 추가 (Ready/Going/Finish/Homing/Timeout).
4. `AppLogView` 툴바: "Simulation 만 보기" 필터 체크박스 1개 (Logger=="Simulation").

### Phase 2 — Sim 측 routing 전환
1. `AddSimLog(msg, severity)` 본문을:
   ```csharp
   var elapsed = engineClockSpan;
   var prefixed = $"[{elapsed:hh\\:mm\\:ss\\.fff}] {msg}";
   var level = severity switch {
       LogSeverity.Error or LogSeverity.Timeout => Level.Error,
       LogSeverity.Warn  => Level.Warn,
       LogSeverity.System => Level.Info,        // [SYSTEM] prefix
       _ => Level.Info                          // Ready/Going/Finish/Homing → Category 로 색만
   };
   AppLogState.Instance.Enqueue(level, "Simulation", prefixed, category: severity.ToString());
   ```
2. `SimEventLog` 컬렉션 + `SimLogEntry` 클래스 제거.
3. `DiagnosticLogWriter` 삭제 — log4net RollingFileAppender 가 디스크 백업 담당.
4. `SimulationPanelState.ResetForNewStore` 의 `SimEventLog.Clear()` 제거 (통합 log 는 세션 무관).

### Phase 3 — UI 정리
1. `SimulationPanel.xaml` 에서 "이벤트 로그" TabItem 제거 (line 82~139).
2. `SimulationPanel.xaml.cs` 의 reset / clear 핸들러 제거.
3. 단일 탭이 되면 TabControl → 그냥 컨테이너로 swap (선택 사항).

### Phase 4 — 테스트 갱신
1. `SimulationPanelStateTests.cs` — SimEventLog 검증 제거 또는 `AppLogState` 검증으로 전환.
2. `MainViewModelTests.cs` — 동일.
3. 신규: `AppLogStateSimulationCategoryTests.cs` — Sim 카테고리 진입 확인.

### Phase 5 — 빌드/검증
- `dotnet build solutions/Ds2.sln`
- `dotnet test Solutions/Tests/Promaker.Tests`
- 수동 시뮬 실행 → 통합 Log 탭에서 Simulation 이벤트 표시 + 색상 / 필터 동작 확인.

## 5. 호환성 · 위험

| 항목 | 영향 | 대응 |
|---|---|---|
| 진단 파일 (`ds2_eventlog_*.txt`) 경로 변경 | 외부 분석 도구 / QA 워크플로우 사용 가능성 | log4net.config 의 RollingFileAppender 경로를 `MyDocuments/ds2_eventlog.txt` 로 명시 |
| 엔진 클럭 prefix vs wall clock 혼용 | UI 시야 변동 | prefix 유지 — wall-clock 컬럼은 AppLogEntry.timestamp 가 별도 표시 |
| 색상 도메인 (Ready/Going/Finish/Homing) | Category DataTrigger 추가 필요 | Phase 1.3 |
| Promaker.Agent (별도 프로세스) 의 SimLog | Agent 는 별도 SignalR 채널 — 본 변경 무관 | 영향 없음 |

## 6. 산출물

- 코드 변경: 약 80 lines edit (74 call sites · pattern 동일 대체 가능 → sed/regex)
- 신규 파일: 0 (테스트 1개 신규)
- 삭제 파일: `SimLogEntry` / `DiagnosticLogWriter` (있다면) 클래스 — 별도 파일 아니면 부분 삭제
- 작업량: ~2~3 시간

## 7. 후속 작업 (선택)

- 통합 Log 탭에 **검색** 박스 (현재 필터는 Level 만) — Simulation 이벤트 합류로 양 증가.
- 통합 Log 탭 **CSV export** — QA 가 시뮬 보고서에 첨부.
- 도크 anchor "Log" 의 caption 에 미확인 ERROR 건수 badge (사용자가 안 봐도 발견 가능).
