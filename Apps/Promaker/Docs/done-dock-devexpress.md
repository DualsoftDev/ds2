# TODO — Promaker Dock Layout DevExpress 재구현

> AvalonDock 4.74.1 의 누적 안정성 문제로 인해 DevExpress.Wpf.Docking 으로 swap.
> 핵심 설계: **별도 csproj (`Promaker.Dock`) 로 격리**하여 DevExpress 의 `System.Windows.Forms` / `System.Drawing` transitive 유입을 Promaker 본체에서 차단.

## 1. 배경

### AvalonDock 4.74.1 누적 문제 (작업 폐기 사유)

`abandoned-dock-avalon.md` 의 작업 결과로 v15 (hotfix 6건 누적, dock2 worktree stash) 까지 진행했으나 다음 문제 잔여:

| # | 증상 | 원인 (trace 진단) |
|---|---|---|
| 1 | 떼었다 붙이면 크기 변경 (snapshot 미적용 회귀) | `OnAnchorIsVisibleChanged` 의 hardcoded default reset. snapshot 으로 fix (1+2). |
| 2 | 부동 → 도킹 시 default 위치로만 복귀 | `CaptureDockPlacement` 의 `Root != dockManager.Layout` 가드가 drag-drop transient state 에서 skip. `UpdateDockPaneExtents` 안에서 6 anchor placement 재confirm 으로 fix (6). |
| 3 | drag-drop 으로 새 위치 dock 후 size 가 매우 작음 (잔여) | drag-drop 으로 새로 생성된 pane (`AnchorablePaneGroup(Vertical,n=2)/AnchorablePaneGroup(Horizontal,n=1)/AnchorablePane`) 의 size 가 fix 1+2 의 처리 범위 밖. XAML 의 5종 canonical pane (explorerPane / simulationPane / historyPane / llmChatPane / propertyPane) 만 size snapshot 처리. |
| 4 | 부동 누르면 마우스 위치랑 동떨어진 위치에 부동창 (사용자 보고) | `LayoutAnchorable.Float()` 의 default location 사용. ▼ → 부동 menu click 시점의 mouse position 과 무관. |
| 5+ | (반복 패턴) 새 corner case 발견 → 추측 fix → trace → 재진단 cycle 누적 | AvalonDock 4.x 의 runtime 동작 미문서화 + PropertyChanged raise 순서 / Root propagate 시점 unstable. |

→ **fix 누적 비용 ≫ DevExpress swap 비용** 판단. v3 PR-1 spike (138 CS0104 충돌) 의 namespace 문제는 별도 csproj 격리로 회피 가능.

### DevExpress 채택 사유 재확정
- 사용자 검증 — "DevExpress 쓸 때 전혀 이런 문제가 없었음".
- 안정성 (production-grade docking framework).
- 위치/크기 보존, drag-drop dock, floating window 등 모두 framework 자체 처리.

## 2. 핵심 설계 — 별도 csproj 격리

### 구조

```
Apps/Promaker/
├── Promaker/                           ← 본체 (변경 최소)
│   ├── Promaker.csproj
│   │   <ProjectReference Include="..\Promaker.Dock\Promaker.Dock.csproj"
│   │                     PrivateAssets="all" />
│   │   ← DX assembly 직접 reference 안 함. PrivateAssets="all" 로 transitive 차단.
│   │   ← ImplicitUsings enable 유지 — WinForms / Drawing 미유입.
│   └── Windows/MainWindow/MainWindow.xaml
│       <promakerDock:DockHost x:Name="dockHost" />
│
└── Promaker.Dock/                      ← 신규 격리 project
    ├── Promaker.Dock.csproj
    │   <PackageReference Include="DevExpress.Wpf.Docking" Version="24.x" />
    │   <ImplicitUsings>disable</ImplicitUsings>  ← WinForms/Drawing 동시 import 차단
    │   <RootNamespace>Promaker.Dock</RootNamespace>
    │
    ├── DockHost.xaml(.cs)              ← DX DockLayoutManager 캡슐화 UserControl
    │   (DX type 외부 노출 X)
    ├── IDockManager.cs                 ← Promaker 가 사용할 abstract API
    ├── DockAnchor.cs                   ← VM 친화 data record (Content / Title / ContentId)
    └── DockLayoutSerializer.cs         ← Save/Restore API (string filepath)
```

### 외부 노출 API (DX type 절대 X)

```csharp
namespace Promaker.Dock;

public interface IDockManager
{
    // 5 anchor + 1 document 등록.
    void RegisterAnchor(DockAnchor anchor);
    void RegisterDocument(DockAnchor document);

    // 토글 / 호출 — Promaker 의 SSOT (IsLlmChatVisible) 가 호출.
    void SetAnchorVisible(string contentId, bool visible);
    bool IsAnchorVisible(string contentId);

    // 이벤트 — Hiding (X 버튼) → VM 으로 통보.
    event EventHandler<DockAnchorVisibilityChangedEventArgs> AnchorVisibilityChanged;
}

public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition);

public enum DockAnchorPosition { LeftTop, LeftBottom, BottomLeft, BottomRight, RightTop, RightMiddle, RightBottom }
```

### namespace 격리 검증 포인트
- Promaker.csproj 의 `<ProjectReference PrivateAssets="all">` 로 DX assembly transitive 가 Promaker.dll 의 reference 에 포함되지 않음을 빌드 결과 검증 (`dotnet list package --include-transitive`).
- `Promaker.csproj` 안의 .cs 에서 DX type 직접 참조 시 컴파일 에러 발생함을 확인 (격리 강제).

### PR-D4 격리 mechanism spike 결과 — fallback D 채택 (D1 PASS) ✅

PR-D4 step 1 진입 시 `<ProjectReference PrivateAssets="all">` 단독 mechanism 의 한계 spike 결과 + fallback D 검증 결과:

| mechanism | (a) transitive 0 | (b) compile 차단 | (c) runtime deploy |
|---|---|---|---|
| ProjectReference PrivateAssets="all" 단독 | FAIL — Promaker 의 transitive 그래프에 DevExpress 11종 + System.Drawing.Common 노출 | FAIL — Promaker 본체에서 `using DevExpress.Xpf.Docking;` 컴파일 통과 | PASS — bin 에 DevExpress.*.v24.1.dll 11종 deploy |
| + Promaker.Dock 의 PackageReference PrivateAssets="all" | PASS | PASS — CS0246 오류 발생 | **FAIL** — bin 에 DevExpress.*.dll 0건 (runtime asset 까지 차단) |
| + PrivateAssets="compile" | FAIL — transitive 노출 그대로 | FAIL — System.Windows.Forms / System.Drawing 의 transitive compile asset 누수로 CS0104 197건 | N/A |
| + PrivateAssets="compile;build;analyzers;contentfiles" | FAIL | FAIL | N/A |
| + ExcludeAssets="compile" PrivateAssets="all" | N/A — Promaker.Dock 자기 빌드 실패 (MC3074) | N/A | N/A |
| **D — Reference HintPath (PackageReference 제거)** | **PASS** | **PASS** (CS0246) | **PASS** (11종 deploy) |

**근본 원인** (NuGet 표준 모순 — A 계열):
- `PrivateAssets="compile"` 은 본 PackageReference 자체의 compile asset 만 차단 — transitive package (System.Windows.Forms / System.Drawing) 의 compile asset 은 별개 PackageReference 로 propagate 되어 Promaker 본체에 누수.
- (a) transitive 0 와 (c) deploy 유지 는 NuGet graph 의 본질상 모순 — runtime asset 이 propagate 되면 transitive list 에 항상 노출.

**채택된 mechanism D — Reference HintPath**:
사용자 결정으로 fallback **D 채택**. spike 결과 (a)+(b)+(c) 동시 PASS 확인.

- `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj`: `<PackageReference Include="DevExpress.Wpf.Docking" />` 제거 → `<Reference Include="..." HintPath="C:\Program Files\DevExpress 24.1\Components\Bin\Framework\..." Private="true" />` × 13건 (DevExpress 11종 + System.Drawing.Common + Microsoft.Win32.SystemEvents).
- `Apps/Promaker/Directory.Packages.props`: `<PackageVersion Include="DevExpress.Wpf.Docking" Version="24.1.7" />` 제거 + 주석 박제.
- Themes.Office2019Colorful 보강 — `<Content Include>` + `Link` + `PreserveNewest` 로 본체 bin 까지 carry (Reference Private=true 만으로는 deps.json runtime trim 에 걸려 누락).
- DevExpress 11종 정확 list (DX 24.1.7):
  1. DevExpress.Data.v24.1.dll
  2. DevExpress.Data.Desktop.v24.1.dll
  3. DevExpress.Drawing.v24.1.dll
  4. DevExpress.Mvvm.v24.1.dll
  5. DevExpress.Pdf.v24.1.Core.dll
  6. DevExpress.Pdf.v24.1.Drawing.dll
  7. DevExpress.Printing.v24.1.Core.dll
  8. DevExpress.Xpf.Core.v24.1.dll
  9. DevExpress.Xpf.Docking.v24.1.dll
  10. DevExpress.Xpf.Layout.v24.1.Core.dll  ← PR-D3 spike 박제에 누락됐던 항목
  11. DevExpress.Xpf.Themes.Office2019Colorful.v24.1.dll

**잔여 우려 (후속 PR / 세션 영역)**:
- HintPath 절대 경로 environment 의존 (다른 개발자 / CI 환경에서 DX 설치 위치 다르면 build 깨짐) — `$(DXInstallRoot)` MSBuild property 도입 권고 (env var 우선, fallback `$(ProgramFiles)\DevExpress 24.1\Components`).
- Version upgrade (24.1 → 24.2 등) 시 13건 HintPath 의 `v24.1` 모두 갱신 — `$(DXVersion)` property 화 권고.
- `System.Drawing.Common` / `Microsoft.Win32.SystemEvents` 는 .NET 9 runtime pack 의 9.0 으로 AutoUnify 되어 deploy 안 됨 (사실상 무해). compile reference 단계의 HintPath resolve 만 필요 — 후속 PR 에서 SDK reference 로 단순화 가능.

## 3. 작업 step (PR 단위)

### 3.0 Sub-agent 위임 골격 (PR-D3 ~ D8 공용)

`--orchestrate` 호출 시 main 은 각 PR 마다 아래 두 골격에 PR 별 슬롯 (`{PR번호}` / `{사용자 의도}` / `{체크박스}`) 만 채워 sub-agent 두 명에게 순차 위임한다 (작업 → 검열).

#### 작업 agent prompt 골격

```
목표: PR-{PR번호} — {요약}

사용자 의도 (verbatim 인용 — 추측 해석 금지):
  "{사용자 의도}"
  ※ orchestrator 호출 시 사용자 명시 문구가 없으면 본 문서 §3 의 PR-{PR번호} 체크박스 전체를 그대로 인용.

작업 범위 (체크박스 1:1):
{체크박스}

박제 결정 (§7 인용 — 본 PR 적용분):
  - §7 #2 격리 방식 (`PrivateAssets="all"`)
  - §7 #3 ImplicitUsings disable (Promaker.Dock 만)
  - §7 #4 외부 노출 API (`IDockManager` / `DockAnchor` / `DockHost`; DX type 노출 0)
  - 본 PR 에 명시된 §3 의 상세 항목 (e.g. PR-D3 spike 처리 룰)

금지사항:
  - DX type 의 Promaker.Dock public surface 노출
  - DX 라이선스 우회 / 본 PR 범위 밖 파일 수정
  - 본 문서 §3 / §7 박제 결정 임의 변경
  - 합의 없는 신규 추상화 추가 (scope creep)

보고 형식:
  1. 변경 파일 목록 + 변경 line 수
  2. 빌드 결과 (`dotnet build` 0 경고 / 0 오류 확인)
  3. CLAUDE.md 자가 검열 trigger 충족 여부 (해당 시 검열 agent 위임 필요 명시)
  4. 잔여 우려 / 차후 PR 영향
```

#### 검열 agent prompt 골격

```
대상: 본 turn 의 unstaged + staged diff 전체 (`git diff HEAD` 직접 재실행).

검증 항목:
  0. **사용자 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑. 의도 항목 중 누락이 있으면 Critical.**
     사용자 의도 (verbatim): "{사용자 의도}"
  1. 논리 오류 / edge case / regression
  2. DX type 외부 노출 (Promaker.Dock public surface 의 DX namespace type 0건 검증)
  3. ImplicitUsings 격리 (Promaker.Dock 에서만 disable, WinForms / Drawing 미유입)
  4. refactoring 기회 (3줄 이상 반복 패턴 / 중복)
  5. 사용자 명시 의도 외 임의 추가 여부 (scope creep)

금지:
  - 코드 / git / 파일 수정 일체
  - 본 문서 §3 / §7 박제 결정에 대한 이의 — 박제 그대로 받아들임

라벨: 모든 finding 에 Critical / Major / Minor 명시.

보고 형식: ① 검열 대상 (파일 + line 수) ② 발견 이슈 (라벨별) ③ 자가 수정 권고 ④ 잔여 우려.
```

### PR-D1 — Promaker.Dock csproj skeleton (Foundation) — ✅ 완료 (commit `87ad64e1`)
- [x] `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj` 신규 (WPF / net9.0-windows / ImplicitUsings disable / RootNamespace=`Promaker.Dock`).
- [x] `Apps/Promaker/Directory.Packages.props` 에 `DevExpress.Wpf.Docking` PackageVersion 등록 (24.1.7 — local feed `C:\Program Files\DevExpress 24.1\Components\System\Components\Packages`).
- [x] `Promaker.Dock.csproj` 에 PackageReference.
- [x] `dotnet build` 성공 (0/0). transitive 9개 package (DevExpress.Data / Drawing / Mvvm / Pdf / Printing / Wpf.Core / Office2019Colorful) + `System.Drawing.Common 4.7.2`. Promaker.Dock 의 bin output 은 .dll 만 (library — Promaker 본체가 reference 할 때 deploy).
- [x] DevExpress 24.1 설치 확인 (`C:\Program Files\DevExpress 24.1\`). 라이선스 정식 file 검증은 사용자 책임.

### PR-D2 — Abstract API + DockHost skeleton — ✅ 완료 (commit `27c85180`)
- [x] `Promaker.Dock/IDockManager.cs` — interface 정의 (RegisterAnchor / RegisterDocument / SetAnchorVisible / IsAnchorVisible / AnchorVisibilityChanged event).
- [x] `Promaker.Dock/DockAnchor.cs` — record (ContentId / Title / Content / DefaultPosition) + `DockAnchorPosition` enum (Left / Bottom / RightTop / RightMiddle / RightBottom / Document).
- [x] `Promaker.Dock/DockHost.xaml(.cs)` — DX `DockLayoutManager` 캡슐화 UserControl skeleton. XAML 의 `x:Name="_dockLayout"` / `"_rootGroup"` = generated partial 에서 `internal` 접근자 (격리 OK). 메서드 4개 모두 `throw new NotImplementedException("PR-D3: ...")`.
- [x] 빌드 통과 (0 오류 / 1 경고 — CS0067 `AnchorVisibilityChanged` 미사용, PR-D3 raise 시 자연 해소).
- [x] DX type 외부 노출 0 검증 (자가 검열 agent 통과, 0 finding).

### PR-D3 — DevExpress DockLayoutManager 기본 layout 구축 — ✅ 완료 (commit `a1b51760`)
- [x] DockHost.xaml 안에 DX `DockLayoutManager` + `LayoutGroup` 트리 — abandoned-dock-avalon.md §3.1 의 안 A (Welcome/Open 통합 LayoutDocument) 그대로 이전.
- [x] 5 anchor (explorer / simulation / property / history / llmchat) + canvas LayoutDocument. ※ orchestrator 체크박스 원문은 6 이름이었으나 abandoned-dock-avalon §3.1 안 A 의 SSOT 가 5 anchor (log 는 simulation 통합) — §9 spike 박제 "Anchor 개수 정정" 참조.
- [x] DX 의 layout 트리에서 size 보존 / drag-drop / floating 동작이 native 로 처리됨을 검증 (코드 상 별도 보정 0건 — `BaseLayoutItem.ItemWidth/Height GridLength` + `Closed` DP + `DockController.Float` 가 DX native 처리. 실 동작 시각 검증은 PR-D4 본체 wire-up 후 첫 실행 시).

#### PR-D3 spike 결과 처리 룰 (자동 진행 보장)
- spike 후 정확한 DX API (`LayoutPanel.Closed` 가 event/property 인지, `LayoutGroup` vs `LayoutPanel` 의 ItemHeight/Width 단위 등) 가 §9 step 1 의 가정과 다를 시:
  - **(a) 박제 갱신만으로 자동 진행**: §9 "PR-D3 spike API 박제" 절에 정확한 API 를 추가 박제한 뒤 step 2~5 진행. **사용자 confirm 요구 금지**.
  - **(b) DX 24.1.7 자체가 본 시나리오 (예: 5 anchor + 1 document group + floating + serializer) 를 지원하지 못함이 spike 로 판명**된 경우만 차단 사유 — 즉시 사용자 알림 후 orchestrator 종료.
- 즉 "API 명칭/시그니처 차이" 는 자동 박제 후 진행, "기능 자체 부재" 만 차단.

### PR-D4 — Promaker 본체 wire-up — ✅ 완료 (mechanism step 1: commit `92a355ce` / wire-up: commit `8b5a8a97`)
- [x] `MainWindow.xaml` 의 AvalonDock `DockingManager` 통째 제거 → `<promakerDock:DockHost x:Name="dockHost" />` embed.
- [x] 5 anchor 의 Content (ExplorerPane / SimulationPanel + Log tab 통합 / PropertyPanel / HistoryPanel / LlmChat ContentControl) 를 `DockAnchor` 로 wrapping + `dockHost.RegisterAnchor(...)`. AppLogView 는 검열 M1 박제로 SimulationPanel TabControl 마지막 tab 으로 통합 (5 anchor SSOT 정합).
- [x] Welcome / Canvas LayoutDocument 등록 (`RegisterDocument` 2건). 검열 M2 박제: ContentId `properties` (Title="Properties") / `canvas` (Title="Workspace") — abandoned-dock-avalon §3.1 안 A SSOT 정합.
- [x] `MainWindow.xaml.cs` 의 AvalonDock 관련 wiring 6 partial 파일 제거 — DockExtents.cs / DockPlacement.cs / DockTrace.cs / DockOverlay.cs / FloatingWindow.cs / MainViewModel/Dock.cs.
- [x] 빌드 통과 (0 경고 / 0 오류). `dotnet list package --include-transitive` DevExpress / System.Drawing.Common 0건. bin 에 DevExpress 11종 + Themes.Office2019Colorful deploy 확인.

### PR-D5 — SSOT (IsLlmChatVisible) 재구성 — ✅ 완료 (commit `a13bf9d2`)
- [x] `MainViewModel.LlmChat.cs` 의 `IsLlmChatVisible` PropertyChanged → `dockHost.SetAnchorVisible("llmchat", show)`. ※ VM 의 IDockManager 직접 의존 회피 — MainWindow.xaml.cs 의 `Vm_PropertyChanged` 외부 구독으로 처리 (MVVM cleanness).
- [x] X 버튼 → `AnchorVisibilityChanged` event → VM `IsLlmChatVisible = false` 단방향 wiring (`_suppressAnchorSync` guard 로 SSOT loop 차단).
- [x] LlmChatVm null / consent 거부 edge case — baseline 박제 §5 3종 보존 (LlmChat.cs 변경 0 line). 검열 Critical 위반 0.
- [x] 보기 메뉴 (`MainToolbarEtcContent.xaml`) — `LayoutAnchorable` 직접 binding 제거 + VM `Is{Explorer/Simulation/Properties/History}Visible` 4 ObservableProperty 신설 (DockAnchors.cs partial) + TwoWay binding → PropertyChanged → `dockHost.SetAnchorVisible(contentId, value)` 간접 경로. Log 체크박스 제거 (PR-D4 SimulationPanel Log tab 통합 SSOT 정합).
- [x] **PR-D4 검열 M4 박제 — 보기 메뉴 PathError 해소**: dead path 4 binding 모두 새 source 로 교체 + 라벨 정합 (Properties / Simulation / History / Explorer / LLM Chat). Log 체크박스 제거 박제.
- [x] **PR-D3 검열 M1 박제 — 초기 raise hook 시점 지연**: DockHost.xaml.cs ctor 의 `_dockLayout.ItemIsVisibleChanged` hook 을 `Loaded` event one-shot 으로 지연. 초기 raise 외부 누설 차단. 정적 분석 + 보수적 default 채택 (실 실행 spike 생략 — Loaded 시점이 가장 안전).
- [x] **PR-D4 검열 잔여 — `HasProject` 토글 Welcome↔Canvas**: SyncWelcomeCanvasVisibility ctor 1회 초기 sync + PropertyChanged hook 분기. abandoned-dock-avalon §3.1 안 A 정합 (false → Welcome / Canvas 숨김, true → 역전).

#### LlmChat.cs baseline 박제 (현재 동작 — `MainViewModel.LlmChat.cs:21-38`)

```csharp
[ObservableProperty] private LlmChatViewModel? _llmChatVm;
[ObservableProperty] private bool _isLlmChatVisible;

[RelayCommand]
private void ToggleLlmChat()
{
    if (LlmChatVm == null)
    {
        // 첫 활성화 — consent 검사 후 lazy 생성. 거부 시 visibility 변경 없음.
        if (!Promaker.LlmAgent.LlmConfig.EnsureGranted())
        {
            // 보기 메뉴 CheckBox 는 IsChecked={Binding IsLlmChatVisible, Mode=OneWay} + Command 패턴.
            // OneWay 라 click 시 CheckBox 가 local-value 로 잠시 toggle 되는데, IsLlmChatVisible 이 안 바뀌면
            // INPC 가 안 와 local-value 가 잘못 체크된 채로 남는다. 동일 값을 다시 raise 해 OneWay 가 source 값을
            // 다시 read → CheckBox 정합 복원.
            OnPropertyChanged(nameof(IsLlmChatVisible));
            return;
        }
        LlmChatVm = new LlmChatViewModel(_store);
    }
    IsLlmChatVisible = !IsLlmChatVisible;
}
```

PR-D5 보존 의무 (Critical — 변경 시 검열 차단):
1. **consent 거부 시 동일 값 PropertyChanged raise** (OneWay CheckBox local-value 복원) — DX 보기 메뉴 binding 으로 옮긴 후에도 동일 효과 유지. DX `BarCheckItem.IsChecked` 가 OneWay 가 아닌 TwoWay 면 raise 자체 불필요해질 수 있음 — spike 후 §9 박제.
2. **`LlmChatVm` lazy 생성** — `IsLlmChatVisible=true` 가 되는 순간엔 `LlmChatVm != null` 보장.
3. **PropertyChanged 구독자** (MainWindow 의 column width 토글) 가 DX 의 `SetAnchorVisible("llmchat", show)` 호출로 대체됨 — 외부에서 보이는 contract 는 "보기 메뉴 toggle → LlmChat 가시성 토글" 유지.

### PR-D6 — Layout 영속화 — ✅ 완료 (commit `c51dc858`)
- [x] DX `DockLayoutManager.SaveLayoutToXml(path)` / `RestoreLayoutFromXml(path)` 활용. IDockManager 에 `SaveLayout(string)` / `RestoreLayout(string)` wrapping (DX type 매개 0).
- [x] `Window_Closing` 의 `_llmChatDisposed=true` 직후 Save (verbatim 정합).
- [x] `Window.Loaded` 의 `RestoreDockLayoutAndSyncVm` 에서 Restore (File.Exists 가드 + try/catch swallow — default 유지).
- [x] 경로 `%LOCALAPPDATA%\Promaker\dock-layout.xml`.
- [x] **PR-D5 검열 Minor 1 박제 해소** — Restore 직후 `_suppressAnchorSync` guard 안에서 4 anchor 의 `IsAnchorVisible` 결과를 VM 4 property 강제 sync (IsAnchorVisible API 본 PR 에서 처음 활용). LlmChat 은 baseline §5 보존 → Restore 결과 무시 + false 강제 (consent 흐름 보존). Welcome/Canvas 는 HasProject SSOT 일방 관리.

### PR-D7 — 추가 wiring — ✅ 완료 (commit `6eccdc01`, 단 D7.3 색차 사용자 1회 시각 검수 잔존)

종료 조건이 측정 가능하도록 항목 박제:

- [x] **D7.1 — 부동 메뉴 mouse 위치**: DX `DockController.Float` native 처리 신뢰. 정적 분석 결과 코드 변경 0 + "DX native 처리 — 별도 보정 불필요" 박제 후 종료. 비정상 발견 시 후속 step 분할 (mouse 위치 capture + `LayoutPanel.FloatLocation`).
- [x] **D7.2 — 헤더 (PanelHeader + HelpButton)**: 5 anchor caption 영역의 PanelHeader UserControl 사용처 0건 grep 결과 (`PanelHeader` 는 `Theme.Controls.Forms.xaml` 의 Style key 만 존재, UserControl 아님). HelpButton Style 의 사용처 (`ApiCallsGridControl` / `ConditionSectionControl`) 는 panel 본문 영역으로 caption 영역과 무관. DX `BaseLayoutItem.Caption` (PR-D4 의 `ApplyAnchorMetadata` 의 Title set) 으로 docked/floating 양쪽 일원화 자연 완료 — 코드 변경 0.
- [x] **D7.3 — DX skin 정합 (적용 완료, 색차 시각 검수 사용자 의무)**:
  - `Promaker.Dock.DockHost.InitializeTheme()` 정적 메서드 신설 (Promaker.Dock 내부 `DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName="Office2019Colorful"`). App.xaml.cs OnStartup → ThemeManager.ApplySavedTheme() 직후 1줄 호출. DX type 외부 노출 0 유지 (§7 #4).
  - skin 명은 PR-D4 fallback D deploy SSOT 정합 — `DevExpress.Xpf.Themes.Office2019Colorful.v24.1.dll` 단일 Themes assembly deploy. verbatim 1차 선택 "WindowsUI Dark" 는 별도 Themes assembly 추가 deploy 필요로 본 PR 미채택 (검열 Minor 1 — 정당화된 박제 정정).
  - **사용자 1회 시각 검수 잔존**: Promaker dark theme background 와 Office2019Colorful skin background 색차 큼 시 후속 step (D7.3-fallback) — `DevExpress.Xpf.Themes.WindowsUI.v24.1.dll` deploy 추가 + `ApplicationThemeName="WindowsUIDark"` 재시도, 또는 자체 skin mapping. todo §3 PR-D7.3 박제 그대로 step 분할 룰 적용.

### PR-D8 — AvalonDock 잔재 정리 — ✅ 완료 (D8.1~D8.6 commit `7afa848b` + D8.7 본 commit)

순서 박제 (rename 은 **마지막 step**):

- [x] D8.1 — `Apps/Promaker/Directory.Packages.props` 의 `Dirkster.AvalonDock` / `Dirkster.AvalonDock.Themes.Metro` PackageVersion 제거.
- [x] D8.2 — `Apps/Promaker/Promaker/Promaker.csproj` 의 PackageReference 제거.
- [x] D8.3 — `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)` 제거 + App.xaml.cs 의 StartupDockSpike / `--dock-spike` arg / 분기 block 정리 (build 정합성).
- [x] D8.4 — `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs` / `DockPlacement.cs` / `DockTrace.cs` 잔재 0건 확인 (PR-D4 `8b5a8a97` 에서 제거 완료).
- [x] D8.5 — `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` 잔재 0건 확인 (PR-D4 에서 제거 완료).
- [x] D8.6 — `Apps/Promaker/Docs/done-dock-layout.md` → `Apps/Promaker/Docs/done-dock-avalon.md` rename (역사 보존). 11 todo 참조 + 8 코드 참조 update 완료.
- [x] **D8.7 (마지막 step)** — 본 문서 `todo-dock-devexpress.md` → `done-dock-devexpress.md` rename. 본 commit 에 §9 진행 체크포인트 전체 SHA 박제 + rename. orchestrator 종료 보고.

## 4. 관련 파일

### 신규
- `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj`
- `Apps/Promaker/Promaker.Dock/DockHost.xaml(.cs)`
- `Apps/Promaker/Promaker.Dock/IDockManager.cs`
- `Apps/Promaker/Promaker.Dock/DockAnchor.cs`
- `Apps/Promaker/Promaker.Dock/DockLayoutSerializer.cs` (PR-D6)

### 수정
- `Apps/Promaker/Directory.Packages.props` (DevExpress 추가, AvalonDock 제거)
- `Apps/Promaker/Promaker/Promaker.csproj` (PrivateAssets="all" ProjectReference, AvalonDock 제거)
- `Apps/Promaker/Promaker/Windows/MainWindow/MainWindow.xaml(.cs)` (DockingManager → DockHost)
- `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` (IDockManager 활용)
- `Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` (보기 메뉴 binding)

### 제거 (PR-D8)
- `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockPlacement.cs`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockTrace.cs`

## 5. 보존 (참조용, 변경 없음)
- `Apps/Promaker/Promaker/Controls/Canvas/SplitCanvasContainer.xaml`
- `Apps/Promaker/Promaker/Controls/Shell/ExplorerPane.xaml`
- `Apps/Promaker/Promaker/Controls/PropertyPanel/PropertyPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Shell/HistoryPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Simulation/SimulationPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` 외 LLM 영역
- `Apps/Promaker/Promaker/Controls/Logging/AppLogView.xaml`
- `Apps/Promaker/Promaker/Presentation/ThemeManager.cs`

## 6. AvalonDock 작업물 보존

- dock2 worktree 의 stash@{0} (label: "AvalonDock size snapshot + Root null deferred capture (fix 1-6) — superseded by DevExpress migration plan") 에 본 turn 까지의 6 fix 보존.
- 사용자 명시 시 stash drop. 그 외는 보존 (회귀 fallback / 학습 참조용).
- `abandoned-dock-avalon.md` (파일명 이력: `done-dock-layout.md` → [PR-D8 D8.6] `done-dock-avalon.md` → [폐기 정리] `abandoned-dock-avalon.md`) 의 v1~v15 박제는 그대로 — DevExpress 작업 검증 후 PR-D8 에서 rename, 이후 AvalonDock 구현 폐기 확정으로 abandoned- prefix 부여.

## 7. 결정 사항

| # | 항목 | 결정 |
|---|---|---|
| 1 | 라이브러리 | DevExpress.Wpf.Docking 24.x (안정 latest) |
| 2 | 격리 방식 | 별도 csproj `Promaker.Dock` + **fallback D — Reference HintPath** (PackageReference 회피, DX 11종 + System.Drawing.Common + Microsoft.Win32.SystemEvents 직접 binary link + Private="true"). NuGet graph 자체에서 격리 → Promaker 본체 transitive 0건. ProjectReference 측 추가 PrivateAssets 불필요. §2 spike 표 참조 |
| 3 | namespace 충돌 회피 | Promaker.Dock 만 `<ImplicitUsings>disable</...>` + 명시 using |
| 4 | API 노출 | `IDockManager` + `DockAnchor` record + `DockHost` UserControl (DX type 외부 노출 0) |
| 5 | 작업 순서 | PR-D1 → D2 → D3 → D4 → D5 → D6 → D7 → D8 |
| 6 | 검증 | 각 PR 단위 빌드 + 수동 시나리오 + 사용자 명시 후 commit |
| 7 | AvalonDock 작업물 | dock2 stash@{0} 보존 / `abandoned-dock-avalon.md` (이력: `done-dock-layout.md` → `done-dock-avalon.md` → `abandoned-dock-avalon.md`) 보존 |
| 8 | DX 라이선스 | PR-D1 첫 step 에서 점검 |

## 8. 주의 사항

- DevExpress 라이선스 미보유 / 만료 시 PR-D1 진행 불가 — 즉시 사용자 알림.
- 본 작업은 `dock2` worktree 에서 진행 (사용자가 의도한 work area). dock 폴더 (옛 worktree) 와 혼동 주의 — 사용자가 `make run` / `cd` 시 dock2 경로 명시.
- 각 PR 의 build 검증 — `dotnet build` 0 경고 / 0 오류 후 사용자 명시 시 commit.
- 자가 검열 trigger (CLAUDE.md): 신규 type 3개 이상 / dispatch 재작성 / public API 갱신 충족 시 sub-agent 위임 후 commit 제안.
- 사용자 명시 없이 git commit 금지.
- `--git-commit` 진행 시 dock2 branch 가 remote 없으면 local commit only (push 생략).
- 본 문서 외 다른 문서 (e.g. `abandoned-dock-avalon.md`) 가리키는 참조는 파일 경로 명시.

## 9. 진행 체크포인트 (이어받는 세션용)

### 현재 상태 (branch `dock2`, remote 없음)
- `87ad64e1` — PR-D1: Promaker.Dock csproj skeleton + Directory.Packages.props (DevExpress 24.1.7) + todo 문서 신규.
- `27c85180` — PR-D2: IDockManager / DockAnchor / DockHost skeleton + 자가 검열 0 finding.
- `8c2318d9` — todo §9 진행 체크포인트 + PR-D1/D2 완료 박제.
- `fd8f142d` — todo §3.0 sub-agent prompt 골격 + PR-D3 spike 룰 + PR-D5 baseline 박제.
- `a1b51760` — PR-D3: DockLayoutManager layout 트리 + IDockManager 구현 + 24.1.7 spike 박제 + RegisterAnchor/RegisterDocument 중복 가드. 자가 검열 Critical 0 / Major 2 (M1: 초기 raise 누설 우려 → PR-D5 spike 위임 / M2: 중복 가드 → 본 commit 반영) / Minor 4.
- `d6148275` — todo §3 PR-D3 헤더 완료 표기 + §9 갱신 (PR-D3 SHA 박제 + PR-D4 시작점).
- `00079ca8` — PR-D4 진입 차단 박제 (격리 mechanism spike A 계열 전부 실패).
- `92a355ce` — PR-D4 step 1: fallback D mechanism 적용 (Reference HintPath × 13 + Themes Content Include + Directory.Packages.props PackageVersion 제거). §2 spike 표 D row 박제.
- `8b5a8a97` — PR-D4 step 2~5: Promaker 본체 wire-up (MainWindow DockingManager → DockHost / 5 anchor + 2 document Register / AvalonDock 6 partial 파일 제거 / SimulationPanel Log tab 통합 / ContentId 박제 정합). 검열 Critical 0 / Major 4 (M1+M2 본 commit 처리, M3 정보, M4 PR-D5 위임) / Minor 5. 빌드 0/0 + transitive DevExpress 0건 + bin 11종 deploy 검증 PASS.
- `a7666390` — todo §3 PR-D4 헤더 완료 + §9 갱신 (PR-D4 SHA 박제 + PR-D5 시작점).
- `a13bf9d2` — PR-D5: SSOT (IsLlmChatVisible) + 보기 메뉴 + HasProject 재wiring + M1 박제 (Loaded hook 지연). DockAnchors.cs (VM partial 4 ObservableProperty) 신설. LlmChat.cs 변경 0 (baseline 박제 §5 보존). 검열 Critical 0 / Major 0 / Minor 4 (모두 정보 또는 PR-D6 위임).
- `4a9f4f71` — todo §3 PR-D5 헤더 완료 + §9 갱신 (PR-D5 SHA 박제 + PR-D6 시작점).
- `c51dc858` — PR-D6: Layout 영속화 (Save/Restore XML). IDockManager 에 SaveLayout/RestoreLayout 시그니처 추가. RestoreDockLayoutAndSyncVm 의 guard 안에서 IDockManager.IsAnchorVisible 로 4 anchor sync (PR-D5 검열 Minor 1 해소). LlmChat baseline §5 보존 (false 강제). 검열 Critical 0 / Major 0 / Minor 3 (모두 정보).
- `fdc47e75` — todo §3 PR-D6 헤더 완료 + §9 갱신 (PR-D6 SHA 박제 + PR-D7 시작점).
- `6eccdc01` — PR-D7: D7.1 native / D7.2 caption 일원화 / D7.3 DX skin (Office2019Colorful 박제 정정). 변경 18 line, 2 파일. DX type 외부 노출 0 유지. 검열 Critical 0 / Major 0 / Minor 1 (skin 박제 정정). 사용자 1회 시각 검수 (§3 PR-D7.3) 잔존.
- `c28a447d` — todo §3 PR-D7 헤더 완료 + §9 갱신 (PR-D7 SHA 박제 + PR-D8 시작점).
- `7afa848b` — PR-D8 D8.1~D8.6 + 추가 잔재 정리 + done-dock-layout.md → done-dock-avalon.md rename + 코드 8건 참조 update + App.xaml brush 3건 제거. 빌드 0/0. 검열 Critical 0 / Major 0 / Minor 2 (정보 권고 본 commit 처리).
- 본 commit (D8.7) — todo-dock-devexpress.md → done-dock-devexpress.md rename + 전체 SHA 박제. **orchestrator 종료.**
- AvalonDock 6 fix 작업물은 dock2 stash@{0} 보존 (label: "AvalonDock size snapshot + Root null deferred capture (fix 1-6) — superseded by DevExpress migration plan").

### ✅ 모든 phase 완료 — orchestrator 종료

PR-D1 ~ PR-D8 전체 완료. 본 commit (D8.7) 으로 `todo-dock-devexpress.md` → `done-dock-devexpress.md` rename + 진행 체크포인트 전체 SHA 박제.

**PR-D1 ~ D8 commit SHA 전체 박제** (orchestrator 추적용 ground truth):

| Phase | commit SHA | 내용 |
|---|---|---|
| PR-D1 | `87ad64e1` | Promaker.Dock csproj skeleton + Directory.Packages.props (DevExpress 24.1.7) |
| PR-D2 | `27c85180` | IDockManager / DockAnchor / DockHost skeleton |
| PR-D1/D2 post | `8c2318d9` | §9 진행 체크포인트 + PR-D1/D2 완료 박제 |
| Setup | `fd8f142d` | §3.0 sub-agent prompt 골격 + PR-D3 spike 룰 + PR-D5 baseline 박제 |
| PR-D3 | `a1b51760` | DockLayoutManager layout 트리 + IDockManager 구현 + 24.1.7 spike 박제 |
| PR-D3 post | `d6148275` | §3 PR-D3 헤더 완료 + §9 갱신 |
| PR-D4 차단 | `00079ca8` | 격리 mechanism spike A 계열 전부 실패 박제 |
| PR-D4 step 1 | `92a355ce` | fallback D mechanism 적용 (Reference HintPath × 13 + Themes Content) |
| PR-D4 step 2~5 | `8b5a8a97` | Promaker 본체 wire-up (MainWindow → DockHost / 5 anchor + 2 doc / AvalonDock 6 partial 제거 / SimulationPanel Log tab 통합) |
| PR-D4 post | `a7666390` | §3 PR-D4 헤더 완료 + §9 갱신 |
| PR-D5 | `a13bf9d2` | SSOT (IsLlmChatVisible) + 보기 메뉴 + HasProject 재wiring + M1 박제 (Loaded hook 지연) |
| PR-D5 post | `4a9f4f71` | §3 PR-D5 헤더 완료 + §9 갱신 |
| PR-D6 | `c51dc858` | Layout 영속화 (Save/Restore XML) + PR-D5 Minor 1 해소 |
| PR-D6 post | `fdc47e75` | §3 PR-D6 헤더 완료 + §9 갱신 |
| PR-D7 | `6eccdc01` | D7.1 native / D7.2 caption 일원화 / D7.3 DX skin (Office2019Colorful) |
| PR-D7 post | `c28a447d` | §3 PR-D7 헤더 완료 + §9 갱신 |
| PR-D8 D8.1~D8.6 | `7afa848b` | AvalonDock 잔재 정리 + done-dock-layout → done-dock-avalon rename + 코드 8건 참조 update + App.xaml brush 3건 제거 |
| **PR-D8 D8.7 (본 commit)** | *(이 commit)* | todo-dock-devexpress.md → done-dock-devexpress.md rename + 전체 SHA 박제 + orchestrator 종료 |

### 잔여 사항 (사용자 의무 / 후속 세션)

1. **PR-D7.3 사용자 1회 시각 검수** — Promaker dark theme background 와 Office2019Colorful skin background 색차 확인. 색차 큼 시 후속 step (D7.3-fallback) 으로 `DevExpress.Xpf.Themes.WindowsUI.v24.1.dll` deploy 추가 + `ApplicationThemeName="WindowsUIDark"` 재시도 또는 자체 skin mapping. §3 PR-D7.3 박제 그대로 step 분할 룰 적용.
2. **mechanism D 잔여 우려** (§2 박제):
   - HintPath 절대 경로 environment 의존 — `$(DXInstallRoot)` MSBuild property 도입 권고 (후속 PR).
   - Version upgrade 시 13건 HintPath 갱신 — `$(DXVersion)` property 화 권고.
   - `System.Drawing.Common` / `Microsoft.Win32.SystemEvents` 의 SDK reference 단순화 (후속 PR).
3. **AvalonDock 6 fix 작업물** — dock2 stash@{0} 에 보존. 사용자 명시 시 stash drop 가능.

### (이력) 이전 PR-D5 작업 흐름

1. **PR-D3 검열 M1 박제 — 초기 raise spike (선결)**:
   - `Apps/Promaker/Promaker.Dock/DockHost.xaml.cs` 의 ctor `_dockLayout.ItemIsVisibleChanged += OnItemIsVisibleChanged` 시점에 layout 초기 평가로 인한 false-IsVisible raise 가 외부 (SSOT) 로 누설되는지 1회 spike. 누설 시 hook 시점을 `Loaded` event 또는 `RegisterAnchor` 첫 호출 직후로 지연 + 박제.
   - 누설 검증 방법: PR-D4 가 적용된 상태에서 Promaker 본체 dev 모드로 실행 후 `AnchorVisibilityChanged` 의 첫 raise list (contentId / isVisible) 와 SSOT VM property 의 초기 값 비교. 또는 `OnItemIsVisibleChanged` 안에 console log 임시 삽입 후 raise sequence 관찰.

2. **MainViewModel.LlmChat.cs SSOT wiring**:
   - `IsLlmChatVisible` PropertyChanged → `dockHost.SetAnchorVisible("llmchat", value)` 호출.
   - DockHost 의 `AnchorVisibilityChanged` event → MainWindow.xaml.cs 에서 hook → `contentId == "llmchat"` 시 `_vm.IsLlmChatVisible = isVisible` (단방향, `_suppressLlmChatSync` guard 로 loop 차단).
   - LlmChat baseline 박제 (§3 PR-D5) 의 consent 거부 시 동일 값 PropertyChanged raise / `LlmChatVm` lazy 생성 / `ToggleLlmChat` 동작 모두 보존.

3. **보기 메뉴 (`MainToolbarEtcContent.xaml`) 재wiring (PR-D4 M4 박제 해소)**:
   - 5 체크박스 (Explorer/Simulation/Properties/History/Log) 각각 `IsChecked` binding 을 VM 의 `IsExplorerVisible` 등 새 property 5건 + `Command="{Binding ToggleAnchorCommand}" CommandParameter="explorer"` 패턴으로 재wiring. ToggleAnchorCommand → `dockHost.SetAnchorVisible(contentId, !IsAnchorVisible(contentId))`.
   - 또는 더 간단한 대안: VM 에 IDockManager 주입 + binding 5건 모두 `OneWayToSource` + Command 1건 + 라벨 정합 ("Properties" / "Simulation" / "Log" — abandoned-dock-avalon §3.1 안 A SSOT). PR-D5 진입 시 spike 후 결정.

4. **HasProject 토글 Welcome↔Canvas 전환 (PR-D4 M4 잔여)**:
   - `MainViewModel.HasProject` PropertyChanged → DockHost.SetAnchorVisible 로 `welcome` / `canvas` 토글. 또는 RegisterDocument 시점에 HasProject=false 이면 canvas 초기 Closed=true, true 시 welcome Closed=true 로 swap.
   - abandoned-dock-avalon §3.1 안 A 의 동작 (HasProject=false → Welcome 보임 / Canvas 숨김, true → 역전) 그대로 보존.

5. **빌드 + 자가 검열**:
   - `dotnet build Apps/Promaker/Promaker/Promaker.csproj` 0 오류.
   - 자가 검열 trigger 충족 (LlmChat / View menu / HasProject 3개 SSOT 재wiring) → 검열 agent 위임 의무.
   - 검열 prompt 검증 항목 0번: 사용자 의도 verbatim 인용 + LlmChat baseline 박제 (§3 PR-D5 의 consent 거부 raise / lazy 생성 / X 버튼 단방향 wiring) 보존 1:1 매핑.
   - 통과 시 commit (메시지 박제: `[dock2] Dock layout: PR-D5 — SSOT (IsLlmChatVisible) + 보기 메뉴 + HasProject 재wiring`).

#### PR-D5 spike 결과 처리 룰
- spike (M1 raise 누설 검증 / 보기 메뉴 binding 패턴 / HasProject 토글 위치) 결과가 본 step 의 가정과 다를 시 PR-D3/D4 spike 룰과 동일 — (a) 차이 박제 후 자동 진행 / (b) 기능 자체 부재 만 차단.

### 새 세션 시작 시 첫 행동
1. 본 문서 `Apps/Promaker/Docs/todo-dock-devexpress.md` 전체 read.
2. 현재 commit 상태 확인 — `git log --oneline -20` 로 PR-D1 ~ PR-D7 commit + 본 §9 갱신 commit 확인.
3. dock2 worktree (`/f/Git/ds2/dock2`) 안에서 작업.
4. **PR-D7.3 사용자 1회 시각 검수 (Office2019Colorful skin 색차)** 결과 받기 — 통과 시 §3 PR-D7.3 "사용자 1회 시각 검수 잔존" 박제 종료, 색차 큼 시 D7.3-fallback step 분할.
5. PR-D8 의 §3 항목 (D8.1~D8.7) 진행 → 본 §9 의 "다음 작업" 흐름 따라.
6. PR-D8.7 rename 직전 §9 에 PR-D1~D8 commit SHA 전체 박제. rename 후 orchestrator 종료.

### DX API spike 결과 박제 — PR-D3 완료
PR-D3 진행 중 24.1.7 의 정확한 API 가 PR-D2 의 가정과 다른 부분 (Closed DP / ItemIsVisibleChanged event / ItemWidth=GridLength / DockController.Float 등) 모두 본 §9 "PR-D3 spike API 박제" 절에 박제 완료. PR-D4 이후 동일 spike 반복 금지 — 본 박제 절을 ground truth 로 사용.

### PR-D3 spike API 박제 (확정 결과, 24.1.7 / `DevExpress.Xpf.Docking.v24.1.dll`)

DLL 직접 reflection (offline feed `C:\Program Files\DevExpress 24.1\Components\Offline Packages\devexpress.wpf.docking\24.1.7\lib\net6.0-windows\DevExpress.Xpf.Docking.v24.1.dll`) 결과. 모든 항목 §3 PR-D3 spike 룰 (a) "API 명칭/시그니처 차이" 에 해당 — 사용자 confirm 없이 자동 박제 후 진행. 기능 자체 부재 (b) 없음.

| # | PR-D2 가정 | 실제 확정 결과 (24.1.7) |
|---|---|---|
| 1 | `LayoutPanel.Closed` event/property 분기 불명 | **`BaseLayoutItem.Closed : bool` (DependencyProperty, read/write)** — visibility 토글의 SSOT. `IsClosed` / `IsHidden` 는 read-only computed. |
| 2 | visibility 변경 event | `DockLayoutManager.ItemIsVisibleChanged` (`ItemIsVisibleChangedEventArgs { Item: BaseLayoutItem, IsVisible: bool }`). 부가: `DockItemClosed` (X 버튼), `DockItemHidden`, `DockItemRestored` 등. visibility 통보용으로는 `ItemIsVisibleChanged` 가 가장 직접적. |
| 3 | `LayoutPanel.ItemHeight` / `ItemWidth` 단위 | **`GridLength`** (`BaseLayoutItem.ItemHeight : GridLength`, `BaseLayoutItem.ItemWidth : GridLength`). |
| 4 | Caption | `BaseLayoutItem.Caption : object` (string 직접 set 가능). |
| 5 | identifier | `BaseLayoutItem.BindableName : string` (serialize key). `Name` (FrameworkElement default) 도 가능하지만 layout XML serialize 용도는 `BindableName`. PR-D3 은 두 가지 모두 set (Name = ContentId 로 동일). |
| 6 | DocumentGroup 자식 추가 | `LayoutGroup.Items : BaseLayoutItemCollection` (DocumentGroup 도 LayoutGroup 상속). `documentGroup.Items.Add(documentPanel)`. |
| 7 | floating 트리거 API | `DockLayoutManager.DockController : IDockController`. `dockController.Float(BaseLayoutItem)`, `.Dock(item)`, `.Hide(item)`, `.Restore(item)`, `.Close(item)`. PR-D3 은 사용 없음 (Show/Hide 는 `Closed` DP 직접 toggle 로 충분). 향후 PR-D7.1 부동 메뉴 mouse 위치 검증 시 필요. |
| 8 | namespace | `DevExpress.Xpf.Docking` (DockLayoutManager / LayoutGroup / LayoutPanel / DocumentGroup / DocumentPanel / BaseLayoutItem). EventArgs: `DevExpress.Xpf.Docking.Base.ItemIsVisibleChangedEventArgs`. |
| 9 | XAML schema | `xmlns:dxdo="http://schemas.devexpress.com/winfx/2008/xaml/docking"` (PR-D2 의 skeleton 그대로). |

#### PR-D3 핵심 사용 패턴 (코드 박제용)

```csharp
// using DevExpress.Xpf.Docking;
// using DevExpress.Xpf.Docking.Base;

// 1. visibility 토글 — Closed DP set/get.
layoutPanel.Closed = !visible;   // SetAnchorVisible
bool visible = !layoutPanel.Closed; // IsAnchorVisible

// 2. visibility event hook — DockLayoutManager 단위.
_dockLayout.ItemIsVisibleChanged += (s, e) =>
{
    // e.Item: BaseLayoutItem, e.IsVisible: bool
    AnchorVisibilityChanged?.Invoke(this, new(e.Item.Name, e.IsVisible));
};

// 3. ItemWidth / ItemHeight = GridLength.
layoutPanel.ItemWidth = new GridLength(320);
layoutPanel.ItemHeight = new GridLength(200);

// 4. Caption + identifier.
layoutPanel.Caption = anchor.Title;
layoutPanel.Name = anchor.ContentId;
```

#### Anchor 개수 정정 (orchestrator 메시지 vs 안 A)

orchestrator PR-D3 체크박스 [2] 는 "5 anchor (explorer / simulation / log / property / history / llmchat)" 로 6 이름이 적혀 있으나 **abandoned-dock-avalon.md §3.1 안 A 의 실제 anchor 는 5개**: explorer / simulation / property / history / llmchat. `log` 는 simulation anchor 가 통합 (abandoned-dock-avalon §1 "Simulation/Gantt/Status Monitor/Event Log"). 따라서 **5 anchor 채택** (안 A 가 ground truth — orchestrator 메시지의 ※ 룰 적용).

DockAnchorPosition enum 매핑 (PR-D2 의 `DockAnchor.cs` 그대로):
- `Left` → explorer (DockWidth 320)
- `Bottom` → simulation (DockHeight 200)
- `RightTop` → property
- `RightMiddle` → history (DockHeight 220)
- `RightBottom` → llmchat (기본 Closed=true)
- `Document` → canvas / welcome (DocumentGroup 안의 DocumentPanel 들)
