# Main Log 창 추가 작업 (TODO)

> **새 세션이 이 문서로 작업을 이어받는 경우, 먼저 아래 §0 인수인계 정보 부터 확인할 것.**

---

## §0 새 세션 인수인계 정보

### 현재 진행 상태 (스냅샷 기준)
- **구현 코드는 아직 작성되지 않음.** 본 문서는 **설계만 확정된 상태** (총 3회 메타 리뷰 누적 반영).
- 다음 단계는 §구현 순서 의 step 1 부터 진행.

### 작업 환경
- **저장소**: `F:\Git\ds2` (git multi-worktree, `.bare` + `bKwak` + `main` 구성)
- **작업 worktree**: `F:\Git\ds2\bKwak`
- **현재 branch**: `bKwak`
- **현재 작업 디렉토리** (Claude Code 시작 시): `F:\Git\ds2\bKwak\Apps\Promaker\Promaker`
- **본 문서 위치**: `F:\Git\ds2\bKwak\Apps\Promaker\Docs\todo-main-log-window.md` (untracked, git add 안 됨)
- **`bKwak` ↔ `main` 관계 메모**: 이전 세션 시작 시 `bKwak` 이 `main` 보다 95개 commit 뒤처져 있었고, `git merge --ff-only main` 으로 fast-forward 완료. `bKwak` 은 `main` 의 strict ancestor 상태였음 → 현재는 `main` HEAD `70ca0ac` 와 동일.
- 새 세션에서 git status / branch 확인 권장:
  ```
  git -C F:/Git/ds2/bKwak status -s
  git -C F:/Git/ds2/bKwak rev-parse --abbrev-ref HEAD
  ```

### 사용자 환경 / 선호 (CLAUDE.md 핵심 요약)
- **언어**: 모든 대화 / 응답 / 사고를 한국어로. 반말 금지.
- **`AskUserQuestion` 도구 사용 금지** — 선택지는 일반 텍스트 번호 목록으로 제시 후 자유 입력 대기.
- **git commit 명시 지시 없이 자동 commit 금지** — 본 작업도 사용자가 `--git-commit` 명령 줄 때까지 commit 하지 않음.
- **자가 검열 강제 절차**: 본 작업은 단일 파일 100 line 이상 변경 + 다중 파일 동시 변경 + 신규 type 3개 이상 신설 trigger 충족 → 코드 수정 → 빌드/테스트 검증 후 **commit / 다음 phase 진입 전에 sub-agent (general-purpose 또는 code-review skill) 위임으로 `git diff` review 필수**.
- **예외 처리 자제**: try/catch 는 boundary 에서만 (본 작업에서는 appender Append 한 곳만 정당화됨).
- **DotNet 환경**: 코드 생성 후 build 해서 컴파일 오류 없는지 확인.
- **언어/버전**: F# 8.0 / C# 12.0 / .NET 9.0.301 / log4net.

### 사용자의 핵심 결정 (대화 누적)
1. 안 **A** — log4net 전체 GUI 표시창 (기존 `Event Log` 시뮬 도메인 이벤트와 분리).
2. VM 소속 **(a)** — 별도 `AppLogState` singleton, `SimulationPanelState` 에 proxy.
3. UI 위치 — `SimulationPanel` 의 첫 번째 tab ('Gantt Chart' 앞).
4. 필터 UI — **ComboBox** (DEBUG / INFO / WARN). 선택값 이상 표시 + ERROR/FATAL 항상 표시.
5. 필터 선택값 **세션 간 저장**.

### 새 세션 시작 시 권장 액션
1. 본 문서 전체 1회 통독.
2. §관련 파일 / 경로 의 "참고 (기존 패턴)" 들 (특히 `SimulationPanel.xaml.cs:33-47`, `AppSettingStore.cs`, `SettingsPaths.cs`, `ObservableObject` 사용 예) 을 직접 Read 하여 현행 코드 확인 — 본 문서의 인용이 stale 일 수 있음.
3. `App.xaml.cs` 의 실제 line 번호 재확인 (본 문서는 `ThemeManager.ApplySavedTheme()` 직후를 prefetch 지점으로 명시했으나 그 사이 commit 으로 줄번호가 바뀌었을 수 있음).
4. §구현 순서 step 1 부터 진행.

### 메타 리뷰 누적
3회 메타 리뷰 통과. 마지막 회차에서 다음 사실 오인을 정정했음:
- 기존 `CopyToClipboard(IList)` helper 미파악 → 신규 helper 작성 지시 철회.
- `INotifyPropertyChanged` 수기 + `Lazy<>` outlier → `ObservableObject` + `[ObservableProperty]` 컨벤션 채택.
- `x:Static` VM 인스턴스 binding outlier (코드베이스 사례 0건) → `SimulationPanelState.AppLog` proxy + DataContext 경유로 변경.
- prefetch 위치 fatal handler 이후로 이동.
- `MainViewModel.AppLog` proxy 삭제 (dead API).

---

## 작업 목표
`SimulationPanel.xaml` 의 `MainTabControl` 에 **앱 전역 log4net 출력**을 보여주는 신규 'Log' tab 을 'Gantt Chart' tab **앞**(첫 번째 위치)에 추가한다.

## 배경 / 맥락
- 기존 `Event Log` tab(`SimEventLog`)은 시뮬레이션 도메인 이벤트 전용 (Status4 RGFH 상태 결합). 앱 전역 log4net 출력을 GUI 로 보는 수단 없음.
- 두 추상화는 **의도적으로 통합하지 않는다** — 시뮬 도메인 상태(Ready/Going/Finish/Homing)와 일반 시스템 로그 레벨은 직교.
- `log4net.config` 에 현재 `RollingFile` + `DebugAppender` 만 등록.
- 사용자 결정 사항:
  - 안 **A** 채택 (log4net 전체 로그 GUI 표시창).
  - VM 소속 **(a)** 채택 — 별도 `AppLogState` singleton, `SimulationPanelState` 에 proxy property 로 노출.
  - 필터 UI: **ComboBox** (DEBUG / INFO / WARN). 선택값 **이상** 레벨만 표시 + **ERROR / FATAL 은 선택과 무관하게 항상 표시**.
  - 필터 선택값 **세션 간 저장**.
  - UI 위치: `SimulationPanel` 의 첫 번째 tab.

## 결정된 설계

### 신규 파일

#### 1. `ViewModels\Logging\AppLogState.cs` — singleton VM
- namespace: `Promaker.ViewModels.Logging`.
- 코드베이스 컨벤션 준수 — `CommunityToolkit.Mvvm.ComponentModel.ObservableObject` + `[ObservableProperty]` (이전 회차의 `INotifyPropertyChanged` 수기 구현은 outlier 였음).
- Singleton: `public static AppLogState Instance { get; } = new();` — App.OnStartup 의 prefetch 로 UI thread 초기화를 보장하므로 `Lazy<>` 불필요.
- 핵심 멤버:
  ```csharp
  public sealed partial class AppLogState : ObservableObject
  {
      public static AppLogState Instance { get; } = new();

      public ObservableCollection<AppLogEntry> Entries { get; } = new();
      public ICollectionView View { get; }
      public IReadOnlyList<LogLevelChoice> LevelChoices { get; } =
          (LogLevelChoice[])Enum.GetValues(typeof(LogLevelChoice));

      [ObservableProperty]
      private LogLevelChoice _selectedLevel;   // 초기값은 ctor 에서 backing field 로 직접 설정

      private const int MaxEntries = 5000;
      private const int PreInitCap = 200;

      private readonly object _gate = new();
      private readonly Queue<AppLogEntry> _pending = new();
      private System.Threading.Timer? _flushTimer;
      private long _seqCounter;

      private AppLogState()
      {
          View = CollectionViewSource.GetDefaultView(Entries);
          View.Filter = o => Filter((AppLogEntry)o!, _selectedLevel);
          // 디스크 → backing field 직접 (setter 우회 → SaveEnum 의 idempotent 재기록 회피).
          _selectedLevel = AppSettingStore.LoadEnumOrDefault(
              SettingsPaths.LogFilterLevel, LogLevelChoice.Info);
      }

      partial void OnSelectedLevelChanged(LogLevelChoice value)
      {
          AppSettingStore.SaveEnum(SettingsPaths.LogFilterLevel, value);
          View.Refresh();
      }

      public void Enqueue(AppLogEntry entry) { /* lock + _pending + flush schedule */ }
      public void Clear() { /* UI thread guard + Entries.Clear() */ }

      // log4net Level Value: DEBUG=30000, INFO=40000, WARN=60000, ERROR=70000, FATAL=110000.
      // 첫 항은 ERROR/FATAL 무조건 표시 의도 표명 (SelectedLevel ∈ {Debug,Info,Warn} 한정에선 둘째 항만으로도 포함되지만,
      // 향후 ERROR/FATAL 도 ComboBox 후보로 들어가는 확장에 대비한 의도 고정).
      private static bool Filter(AppLogEntry e, LogLevelChoice selected) =>
          e.Level.Value >= log4net.Core.Level.Error.Value
          || e.Level.Value >= ChoiceToLog4Net(selected).Value;
  }
  ```
- `LevelChoices` 는 `Enum.GetValues` 직접 사용 (별도 list 보일러 없음).
- 동시성/lifecycle:
  - 모든 `Entries` mutation 은 **UI thread** 에서만. → `BindingOperations.EnableCollectionSynchronization` 는 **호출하지 않음** (UI-only mutation 전제에서는 불필요). 만약 추후 producer fast-path 가 도입되면 그때 `_gate` 로 `Entries` mutation 까지 감싸야 함.
  - `_pending` 만 `_gate` 보호.
  - flush timer: `System.Threading.Timer`. 첫 `Enqueue` 시 lazy start (`Change(16, Timeout.Infinite)`), tick 시 `Application.Current?.Dispatcher.BeginInvoke(...)` 로 UI thread 에 flush. `Dispatcher.HasShutdownStarted` 가드.
  - singleton 은 process 종료 시 자동 정리. timer dispose 별도 호출 없음.

#### 2. `ViewModels\Logging\AppLogEntry.cs`
- namespace: `Promaker.ViewModels.Logging`.
- `public sealed class AppLogEntry` (record 아님 — value equality 가 ListBox SelectionMode=Extended 와 결합 시 동일 메시지 중복에서 selection 혼동. reference equality 가 안전).
- 속성: `long Seq`, `DateTime Timestamp`, `log4net.Core.Level Level`, `string Logger`, `string Message`.
- `Seq` 는 `AppLogState._seqCounter` 가 `Interlocked.Increment` 로 부여.
- 포맷 SSOT:
  ```csharp
  public const string Format = "[{0:HH:mm:ss.fff}] {1,-5} {2} — {3}";
  public override string ToString() =>
      string.Format(CultureInfo.InvariantCulture, Format, Timestamp, Level.Name, Logger, Message);
  ```
- `Format` 은 XAML `MultiBinding StringFormat` 도 동일 문자열 참조 권장 (또는 동일 문자열 inline + 코드 주석으로 SSOT 의도 명시).

#### 3. `ViewModels\Logging\LogLevelChoice.cs`
- `public enum LogLevelChoice { Debug, Info, Warn }`.

#### 4. `Logging\WpfObservableAppender.cs` — log4net appender
- namespace: `Promaker.Logging`.
- `public sealed class WpfObservableAppender : log4net.Appender.AppenderSkeleton`.
- Append 구현:
  ```csharp
  protected override void Append(LoggingEvent e)
  {
      try
      {
          // LoggingEvent 의 LocationInformation/ThreadName 등 lazy property 는 caller thread context 의존.
          // marshal 너머로 raw 를 넘기지 말고 호출 thread 에서 즉시 snapshot.
          // RenderedMessage 는 일부 layout 에서 null 가능 → ?? "" 보강.
          var loggerShort = ShortenLogger(e.LoggerName);
          var entry = new AppLogEntry(
              AppLogState.Instance.NextSeq(),
              e.TimeStamp,
              e.Level,
              loggerShort,
              e.RenderedMessage ?? string.Empty);
          AppLogState.Instance.Enqueue(entry);
      }
      catch (Exception ex)
      {
          // log4net appender boundary catch — Append 실패가 caller 로 cascade 되어 추가 Log.Fatal 을 유발하면 deadlock 위험.
          // 사용자 CLAUDE.md "꼭 필요한 경우만 catch" 의 boundary 해당.
          ErrorHandler?.Error("WpfObservableAppender.Append failed", ex);
      }
  }

  // log4net RollingFile layout 이 %logger{1} 로 동일한 단축을 이미 수행하나,
  // appender 가 raw LoggerName 을 받으므로 GUI 에서도 동일 정책으로 명시적 단축.
  private static string ShortenLogger(string name)
  {
      var i = name.LastIndexOf('.');
      return i < 0 ? name : name.Substring(i + 1);
  }
  ```

### 수정 파일

#### 1. `log4net.config`
- 신규 appender 등록:
  ```xml
  <appender name="Wpf" type="Promaker.Logging.WpfObservableAppender, Promaker">
    <!-- root <level value="DEBUG"/> 와 사실상 중복이나 — 추후 root level 변경에도
         Wpf appender 는 항상 모든 레벨을 수신하도록 명시적 floor 로 보존. 필터링은 ICollectionView 담당. -->
    <threshold value="DEBUG" />
  </appender>
  ```
- `<root>` 에 `<appender-ref ref="Wpf" />` 추가.

#### 2. `Services\SettingsPaths.cs`
- 한 줄 추가:
  ```csharp
  public static string LogFilterLevel => Of("logFilterLevel.txt");
  ```

#### 3. `App.xaml.cs`
- `XmlConfigurator.Configure(...)` 는 line **92-94** (line 88-90 아님 — 이전 회차 줄번호 오기 정정).
- prefetch 한 줄을 **fatal handler 등록 이후 + `ThemeManager.ApplySavedTheme()` 인접 (line 121 이후)** 에 배치:
  ```csharp
  ThemeManager.ApplySavedTheme();

  // CollectionView 가 UI thread SynchronizationContext 에 묶이도록 UI thread 에서 강제 prefetch.
  // (worker thread 의 첫 log 호출이 lazy 생성을 trigger 하면 view 가 worker 에 묶임.)
  // 위치 근거: fatal handler (line 98 / 112) 등록 이후여야 ctor 예외 시 진단 손실 없음.
  _ = Promaker.ViewModels.Logging.AppLogState.Instance;
  ```

#### 4. `ViewModels\Simulation\SimulationPanelState.cs` (또는 partial 적절 위치)
- proxy property 추가 — DataContext 경유 binding 의 정석:
  ```csharp
  public AppLogState AppLog { get; } = AppLogState.Instance;
  ```

#### 5. `Controls\Simulation\SimulationPanel.xaml`
- xmlns 추가/삭제 없음 (이전 회차의 `xmlns:logvm` 도입은 철회 — 코드베이스에 VM 인스턴스 x:Static binding 사례 0건).
- `MainTabControl` 의 **첫 번째** 위치에 `<TabItem Header="Log">` 삽입.
- 기존 `<TabItem Header="Gantt Chart">` 에 `IsSelected="True"` 명시.
- `UserControl.Resources` 에 brush 추가:
  ```xml
  <SolidColorBrush x:Key="LogWarnBrush"  Color="#FFA500"/>
  <SolidColorBrush x:Key="LogErrorBrush" Color="#FF4444"/>
  <SolidColorBrush x:Key="LogFatalBrush" Color="#FF00FF"/>
  ```
- Log tab 본문 (DataContext = `SimulationPanelState` 이므로 `AppLog.*` 로 자연 binding):
  ```xml
  <TabItem Header="Log">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
      </Grid.RowDefinitions>

      <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="8,4">
        <TextBlock Text="Level:" VerticalAlignment="Center"
                   Foreground="{DynamicResource SecondaryTextBrush}"/>
        <ComboBox ItemsSource="{Binding AppLog.LevelChoices}"
                  SelectedItem="{Binding AppLog.SelectedLevel, Mode=TwoWay}"
                  Width="80" Margin="6,0"/>
        <TextBlock Text="(ERROR 항상 표시)" Margin="8,0"
                   Foreground="{DynamicResource SecondaryTextBrush}"
                   VerticalAlignment="Center" FontStyle="Italic"/>
      </StackPanel>

      <ListBox Grid.Row="1" x:Name="AppLogListBox"
               ItemsSource="{Binding AppLog.View}"
               Background="{DynamicResource SecondaryBackgroundBrush}"
               Foreground="{DynamicResource PrimaryTextBrush}"
               FontFamily="Consolas" FontSize="11"
               BorderThickness="0" SelectionMode="Extended"
               VirtualizingStackPanel.IsVirtualizing="True"
               VirtualizingStackPanel.VirtualizationMode="Recycling"
               ScrollViewer.IsDeferredScrollingEnabled="True">
        <ListBox.ContextMenu>
          <ContextMenu>
            <MenuItem Header="전체 복사"   Click="AppLogCopyAll_Click"/>
            <MenuItem Header="선택 복사"   Click="AppLogCopySelected_Click"/>
            <Separator/>
            <MenuItem Header="로그 지우기" Click="AppLogClear_Click"/>
          </ContextMenu>
        </ListBox.ContextMenu>
        <ListBox.ItemTemplate>
          <DataTemplate>
            <TextBlock>
              <TextBlock.Text>
                <MultiBinding StringFormat="[{0:HH:mm:ss.fff}] {1,-5} {2} — {3}"
                              ConverterCulture="en-US">
                  <Binding Path="Timestamp"/>
                  <Binding Path="Level.Name"/>
                  <Binding Path="Logger"/>
                  <Binding Path="Message"/>
                </MultiBinding>
              </TextBlock.Text>
              <TextBlock.Style>
                <Style TargetType="TextBlock">
                  <Setter Property="Foreground" Value="{DynamicResource PrimaryTextBrush}"/>
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding Level.Name}" Value="DEBUG">
                      <Setter Property="Foreground" Value="{DynamicResource SecondaryTextBrush}"/>
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Level.Name}" Value="WARN">
                      <Setter Property="Foreground" Value="{StaticResource LogWarnBrush}"/>
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Level.Name}" Value="ERROR">
                      <Setter Property="Foreground" Value="{StaticResource LogErrorBrush}"/>
                      <Setter Property="FontWeight" Value="Bold"/>
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Level.Name}" Value="FATAL">
                      <Setter Property="Foreground" Value="{StaticResource LogFatalBrush}"/>
                      <Setter Property="FontWeight" Value="Bold"/>
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </TextBlock.Style>
            </TextBlock>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </Grid>
  </TabItem>
  ```

#### 6. `Controls\Simulation\SimulationPanel.xaml.cs`
- 기존 `CopyToClipboard(IList)` helper (line 33-47) **그대로 재활용**. 신규 helper 추가 없음.
- AppLog 핸들러 3개 추가:
  ```csharp
  private void AppLogCopyAll_Click(object sender, RoutedEventArgs e)
      => CopyToClipboard(AppLogListBox.Items);

  private void AppLogCopySelected_Click(object sender, RoutedEventArgs e)
      => CopyToClipboard(AppLogListBox.SelectedItems.Count > 0
          ? AppLogListBox.SelectedItems
          : AppLogListBox.Items);

  private void AppLogClear_Click(object sender, RoutedEventArgs e)
  {
      if (DataContext is ViewModels.SimulationPanelState vm)
          vm.AppLog.Clear();
  }
  ```
- 기존 `EventLog*_Click` 은 이미 `CopyToClipboard` 사용 중 → 추가 단순화 작업 없음.

### 삭제된 이전 회차 결정
- `MainViewModel.AppLog` proxy property → **삭제** (사용처 0. DataContext 경유는 `SimulationPanelState.AppLog` 로 충분).
- `xmlns:logvm` namespace 선언 → **삭제** (x:Static VM 인스턴스 binding 은 코드베이스 컨벤션 위배).
- 신규 `CopyAllToClipboard`/`CopySelectedToClipboard` helper → **삭제** (기존 `CopyToClipboard(IList)` 재활용).
- `INotifyPropertyChanged` 수기 구현 + `Lazy<>` → **삭제** (`ObservableObject` + `[ObservableProperty]` 로 대체).
- `BindingOperations.EnableCollectionSynchronization` 호출 → **삭제** (UI-only mutation 전제에서 불필요).

## 구현 순서
1. `ViewModels\Logging\LogLevelChoice.cs` + `AppLogEntry.cs` + `AppLogState.cs` 작성.
2. `Logging\WpfObservableAppender.cs` 작성 (Append 의 try/catch + ErrorHandler.Error 포함).
3. `Services\SettingsPaths.cs` 에 `LogFilterLevel` 한 줄 추가.
4. `log4net.config` 수정 — Wpf appender 등록 + root ref 추가.
5. `App.xaml.cs` 의 `ThemeManager.ApplySavedTheme()` 직후에 prefetch 한 줄 추가.
   - **주의**: step 4 와 step 5 는 **동일 commit** 으로 처리. step 4 만 적용된 상태에서 첫 `Log.*` 호출이 worker thread 면 `AppLogState.Instance` 가 worker SynchronizationContext 에 묶일 위험.
6. `ViewModels\Simulation\SimulationPanelState.cs` (또는 적절한 partial) 에 `AppLog` proxy 추가.
7. `SimulationPanel.xaml` 수정 — brush, Log tab 삽입, Gantt Chart `IsSelected="True"`.
8. `SimulationPanel.xaml.cs` 에 AppLog 핸들러 3개 추가 (기존 helper 재활용).
9. 빌드 → 실행 → 다음 항목 검증:
   - `Log.Info(...)` / `Log.Warn(...)` / `Log.Error(...)` 가 Log tab 에 표시
   - 필터 변경 시 즉시 반영 + ERROR/FATAL 무조건 표시
   - 앱 재기동 시 필터값 복원
   - burst 시 UI freeze 없음 (16ms coalesce 동작)
   - 5000건 초과 시 FIFO 정상
   - **`Microsoft.AspNetCore` / `Microsoft.Hosting` INFO 메시지가 GUI 에 안 보이는 것이 정상** (logger-level override 적용)
10. 자가 검열 (sub-agent code review) — 100 line 이상 변경 + 다중 파일 → trigger 충족.

## 주의 사항

### Threading / 초기화
- **UI thread prefetch 필수**: `AppLogState.Instance` 의 첫 생성이 worker thread 에서 일어나면 `CollectionView` 가 worker SynchronizationContext 에 묶여 이후 binding 시 `NotSupportedException`. → `App.OnStartup` 의 fatal handler 등록 이후 시점에 `_ = AppLogState.Instance;` 로 강제.
- **prefetch 위치 근거**: `XmlConfigurator.Configure` 직후가 아니라 `AppDomain.UnhandledException` / `DispatcherUnhandledException` 등록 이후에 두는 이유 = ctor 안에서 예외 발생 시 진단 가능하도록.
- **LoggingEvent snapshot**: `LocationInformation`, `ThreadName` 등 lazy property 는 caller thread context 의존. appender `Append` 에서 즉시 `AppLogEntry` 로 변환 후 marshal. raw `LoggingEvent` 를 marshal 너머로 절대 전달 금지.
- **Appender try/catch boundary**: `Append` 전체를 try/catch 후 `ErrorHandler?.Error(...)`. cascade 시 `DispatcherUnhandledException` → `Log.Fatal` → `Append` → 재 throw 로 deadlock/loop 위험 방지.
- **Pre-init drop policy**: `Application.Current` 가 null 인 startup 극초기 호출은 in-memory `_pending` queue 누적 (cap 200). cap 초과 시 oldest drop. 의도를 코드 주석으로 명시.
- **DispatcherUnhandledException 시점 호출**: fatal handler 에서 `Log.Fatal(...)` 가 호출되어 appender 가 깨진 dispatcher 에 BeginInvoke 시도하면 swallow. 로그 자체는 RollingFile 에 남음.
- **`EnableCollectionSynchronization` 미사용 사유**: 모든 `Entries` mutation 은 UI thread 의 flush 에서만 수행. worker thread 가 collection 을 enumerate 하지 않으므로 호출 불필요. 추후 producer fast-path 도입 시 `_gate` 가 `Entries` mutation 도 감싸도록 함께 보강.

### Timer lifecycle
- `System.Threading.Timer` 사용. ctor 에서 dummy 생성 (`Timeout.Infinite`), 첫 `Enqueue` 시 `Change(16, Timeout.Infinite)` 로 single-shot 예약.
- flush 콜백:
  ```csharp
  if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted)
      d.BeginInvoke(FlushOnUI);
  ```
- singleton 이므로 process 종료 시 자동 정리. 별도 dispose 호출 없음.

### Performance / Concurrency
- **Batching**: lock + `_pending` + 16ms coalesce 로 burst flatten.
- **VirtualizationMode="Recycling"** + `IsDeferredScrollingEnabled="True"` 로 5000건 list 성능 확보.
- **MaxEntries 초과**: UI thread flush 에서 Add 후 5000 초과분 `RemoveAt(0)` FIFO trim. 16ms batch 안에서 단일 묶음으로 통지.
- **FIFO trim 시 selection 깜빡임**: 사용자가 오래된 entry 를 선택 중인데 trim 으로 제거되면 `SelectionChanged` 발생. acceptable (1차) — 자주 발생하지 않음. 우려 시 `Entries.RemoveAt` 직전 `lb.SelectedItems.Remove(entry)` 가드 가능하지만 over-engineering.
- **필터 변경 비용**: 5000건 ICollectionView Refresh 일시 100~200ms 가능. acceptable.
- **MultiBinding reflection 비용**: 가시 영역 (~60행) 만 평가되므로 virtualizing 으로 영향 미미.

### log4net 설정 영향
- **Microsoft.* logger override 적용**: `log4net.config` 의 `<logger name="Microsoft.AspNetCore" ...>` WARN 등 logger-level cut 은 **모든 appender 에 일괄 적용** → GUI Log tab 에서 DEBUG 로 내려도 ASP.NET hosting INFO 는 안 보임. GUI 만 풀려면 별도 logger override 필요. 정상 동작이므로 검증 항목에 명시.
- **`<threshold value="DEBUG"/>` vs root `<level value="DEBUG"/>` 중복**: 의도 — root level 변경에도 Wpf appender 가 항상 모든 레벨 수신하도록 floor 명시. 주석으로 의도 보존.

### 도메인 분리
- `SimEventLog` (`SimLogEntry` / `LogSeverity`) 와 `AppLogEntry` 는 **의도적으로 통합하지 않는다**. 전자는 시뮬 도메인 상태 (Status4 RGFH) 결합 의미값, 후자는 log4net 시스템 레벨. 두 추상화는 직교.

### Logger 이름 단축
- `Ds2.Runtime.Engine.SimulationEngine` → `SimulationEngine`. RollingFile layout `%logger{1}` 과 같은 정책 (GUI 도 동일성 유지). 주석으로 의도 명시.

### Namespace 결정
- `Promaker.ViewModels.Logging` 신설 (`SimLogEntry` / `LogSeverity` 가 flat `Promaker.ViewModels` 인 점과 일관성 trade-off 있음). 사유: log4net 관련 타입군이 응집도 높고 향후 검색 등 부가 항목이 늘어날 가능성. 한 폴더로 모으는 게 유지보수에 유리.

### 포맷 SSOT
- `AppLogEntry.Format` const + `ToString()` 의 `CultureInfo.InvariantCulture`. XAML `MultiBinding StringFormat` 도 동일 문자열 (또는 const 참조) — 화면과 clipboard 출력 일관.

## 관련 파일 / 경로

### 변경 대상
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\log4net.config`
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\App.xaml.cs` (line 121 인근, `ThemeManager.ApplySavedTheme()` 직후)
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\Services\SettingsPaths.cs` (한 줄 추가)
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\ViewModels\Simulation\SimulationPanelState.cs` (또는 적절한 partial — `AppLog` proxy property)
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\Controls\Simulation\SimulationPanel.xaml`
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\Controls\Simulation\SimulationPanel.xaml.cs`

### 신규
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\ViewModels\Logging\LogLevelChoice.cs`
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\ViewModels\Logging\AppLogEntry.cs`
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\ViewModels\Logging\AppLogState.cs`
- `F:\Git\ds2\bKwak\Apps\Promaker\Promaker\Logging\WpfObservableAppender.cs`

### 참고 (기존 패턴)
- `SimulationPanel.xaml` line 68-125 (Event Log TabItem, DataTrigger 색상)
- `SimulationPanel.xaml.cs:33-47` (`CopyToClipboard(IList)` helper — 재활용)
- `ViewModels\Simulation\SimulationPanelState.cs:20-25` (`SimLogEntry` / `LogSeverity`)
- `Presentation\AppSettingStore.cs` — `LoadEnumOrDefault<TEnum>` / `SaveEnum<TEnum>` SSOT
- `Services\SettingsPaths.cs` — 설정 파일 단일 경로 출처
- `Presentation\ThemeManager.cs`, `Presentation\LanguageManager.cs`, `Dialogs\ApplicationSettingsDialog.cs` — 동일 영속화 패턴
- `ViewModels\Manual\ManualControlState.cs`, `ViewModels\CanvasWorkspaceState.cs` 등 — `ObservableObject` + `[ObservableProperty]` 컨벤션 예 (69 occurrences / 10+ 파일)

## 메타 리뷰 반영 (N=3 회차 누적)

### 1회차 → 2회차 변경
- C-A binding: `RelativeSource AncestorType=Window` → `x:Static` 으로 변경 (1회차)
- C-C 세션 저장 SSOT 준수, C-D lazy thread prefetch, M-A enum binding, M-B helper, M-D snapshot, M-E batching, M-F 위치 분리, m-1 sealed class+Seq, m-3 FATAL 색상 등 채택
- M-G `IAppLogSink` 거절 (YAGNI), M-C/M-H UserControl 부분 채택

### 2회차 → 3회차 변경 (이번 회차)
- **C-1**: `CopyToClipboard(IList)` helper 가 이미 존재 (SimulationPanel.xaml.cs:33-47) → 신규 helper 작성 지시 철회.
- **C-2**: prefetch 위치를 `XmlConfigurator.Configure` 직후 → `ThemeManager.ApplySavedTheme()` 직후 (fatal handler 등록 이후) 로 이동.
- **C-3**: `INotifyPropertyChanged` 수기 + `Lazy<>` → `ObservableObject` + `[ObservableProperty]` + `static Instance = new()`. 코드베이스 컨벤션 (69 occurrences) 준수.
- **C-4**: `x:Static` VM 인스턴스 binding → `SimulationPanelState.AppLog` proxy + DataContext 경유. 코드베이스에 VM x:Static binding 사례 0건 확인.
- **C-5**: ctor 의 load → setter 우회, backing field 직접 초기화.
- **M-1**: `BindingOperations.EnableCollectionSynchronization` 제거 (UI-only mutation 전제).
- **M-2**: timer lifecycle 명시 (`System.Threading.Timer` + `HasShutdownStarted` 가드).
- **M-3**: `MainViewModel.AppLog` 삭제 (dead API).
- **M-4**: Microsoft.* logger override 가 GUI 에도 적용된다는 점 검증 항목 / 주의사항 추가.
- **M-5**: log4net.config + prefetch 변경의 atomic commit 명시.
- **M-6**: `ToString` 의 `InvariantCulture` + `Format` const SSOT.
- **M-7**: appender Append 의 try/catch + `ErrorHandler.Error`.
- m-1 line 정정 (88-90 → 92-94), m-2 namespace 명시, m-3 `Enum.GetValues`, m-4 `?? ""` null 보강, m-7 `ShortenLogger` 의도 주석, m-14 trim selection 깜빡임, m-15 threshold 중복 의도 주석 채택.
- m-5 View.Refresh throttle, m-8 brush Themes 이동, m-10 unit test — 추후 확장으로 이동.

## 첫 버전 제외 (추후 확장)
- 검색 박스
- 자동 스크롤 toggle
- 별도 dock window 분리 + `Controls\Logging\AppLogView.xaml` UserControl 추출
- Logger 컬럼 필터
- `IAppLogSink` interface 도입 (테스트 / 다중 sink 필요 시)
- ERROR / FATAL background tint
- View.Refresh throttle (ComboBox 빠른 전환 시)
- Brush 를 `Themes\Theme.Brushes.xaml` / `Theme.Light.Brushes.xaml` 양쪽으로 이동 (EventLog hardcoded 색상도 함께 정리 시점에)
- Unit test — `Filter` predicate / `ShortenLogger` 등 pure function
- `XmlConfigurator.Configure` 실패 시 visible indicator
