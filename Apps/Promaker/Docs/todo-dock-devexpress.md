# TODO — Promaker Dock Layout DevExpress 재구현

> AvalonDock 4.74.1 의 누적 안정성 문제로 인해 DevExpress.Wpf.Docking 으로 swap.
> 핵심 설계: **별도 csproj (`Promaker.Dock`) 로 격리**하여 DevExpress 의 `System.Windows.Forms` / `System.Drawing` transitive 유입을 Promaker 본체에서 차단.

## 1. 배경

### AvalonDock 4.74.1 누적 문제 (작업 폐기 사유)

`done-dock-layout.md` 의 작업 결과로 v15 (hotfix 6건 누적, dock2 worktree stash) 까지 진행했으나 다음 문제 잔여:

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
- [x] DockHost.xaml 안에 DX `DockLayoutManager` + `LayoutGroup` 트리 — done-dock-layout.md §3.1 의 안 A (Welcome/Open 통합 LayoutDocument) 그대로 이전.
- [x] 5 anchor (explorer / simulation / property / history / llmchat) + canvas LayoutDocument. ※ orchestrator 체크박스 원문은 6 이름이었으나 done-dock-layout §3.1 안 A 의 SSOT 가 5 anchor (log 는 simulation 통합) — §9 spike 박제 "Anchor 개수 정정" 참조.
- [x] DX 의 layout 트리에서 size 보존 / drag-drop / floating 동작이 native 로 처리됨을 검증 (코드 상 별도 보정 0건 — `BaseLayoutItem.ItemWidth/Height GridLength` + `Closed` DP + `DockController.Float` 가 DX native 처리. 실 동작 시각 검증은 PR-D4 본체 wire-up 후 첫 실행 시).

#### PR-D3 spike 결과 처리 룰 (자동 진행 보장)
- spike 후 정확한 DX API (`LayoutPanel.Closed` 가 event/property 인지, `LayoutGroup` vs `LayoutPanel` 의 ItemHeight/Width 단위 등) 가 §9 step 1 의 가정과 다를 시:
  - **(a) 박제 갱신만으로 자동 진행**: §9 "PR-D3 spike API 박제" 절에 정확한 API 를 추가 박제한 뒤 step 2~5 진행. **사용자 confirm 요구 금지**.
  - **(b) DX 24.1.7 자체가 본 시나리오 (예: 5 anchor + 1 document group + floating + serializer) 를 지원하지 못함이 spike 로 판명**된 경우만 차단 사유 — 즉시 사용자 알림 후 orchestrator 종료.
- 즉 "API 명칭/시그니처 차이" 는 자동 박제 후 진행, "기능 자체 부재" 만 차단.

### PR-D4 — Promaker 본체 wire-up (격리 mechanism = fallback D Reference HintPath, §2 spike D1 PASS)
- [ ] `MainWindow.xaml` 의 AvalonDock `DockingManager` 통째 제거 → `<promakerDock:DockHost x:Name="dockHost" />` embed.
- [ ] 5 anchor 의 Content (ExplorerPane / SimulationPanel / etc) 를 `DockAnchor` 로 wrapping + `dockHost.RegisterAnchor(...)`.
- [ ] Welcome / Canvas LayoutDocument 동일.
- [ ] `MainWindow.xaml.cs` 의 AvalonDock 관련 wiring (5+ partial 파일 — DockExtents.cs / DockPlacement.cs / DockTrace.cs / MainViewModel.Dock.cs) 통째 제거.
- [ ] 빌드 통과.

### PR-D5 — SSOT (IsLlmChatVisible) 재구성
- [ ] `MainViewModel.LlmChat.cs` 의 `IsLlmChatVisible` PropertyChanged → `dockHost.SetAnchorVisible("llmchat", show)`.
- [ ] X 버튼 → `AnchorVisibilityChanged` event → VM `IsLlmChatVisible = false` 단방향 wiring.
- [ ] LlmChatVm null / consent 거부 edge case — 아래 **baseline 박제** 와 동등 동작 유지.
- [ ] 보기 메뉴 (`MainToolbarEtcContent.xaml`) — `IDockManager` 의 `IsAnchorVisible` / `SetAnchorVisible` 활용. `LayoutAnchorable` 직접 binding 제거.
- [ ] **PR-D3 검열 M1 박제 — 초기 raise spike**: PR-D5 진입 직전 1회 spike 로 다음 검증. (1) `DockHost` ctor 의 `_dockLayout.ItemIsVisibleChanged` hook 시점에 layout 초기 평가로 인한 false-IsVisible raise 가 외부 (SSOT) 로 누설되는지. (2) 누설 시 hook 시점을 `Loaded` event 또는 `RegisterAnchor` 첫 호출 직후로 지연. (3) 누설 없으면 박제 후 종료. SSOT 의 `IsLlmChatVisible` 무한 loop 차단 책임은 본 항목과 별개로 `_suppressLlmChatSync` 류 가드 (done-dock-layout §2 F3 박제) 적용 의무.

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

### PR-D6 — Layout 영속화
- [ ] DX `DockLayoutManager.SaveLayoutToXml(path)` / `RestoreLayoutFromXml(path)` 활용.
- [ ] `Window_Closing` 의 `_llmChatDisposed=true` 직후 Save.
- [ ] `Window.Loaded` 에서 Restore (파일 없음 / parse 실패 시 default).
- [ ] `%LOCALAPPDATA%\Promaker\dock-layout.xml`.

### PR-D7 — 추가 wiring

종료 조건이 측정 가능하도록 항목 박제:

- [ ] **D7.1 — 부동 메뉴 mouse 위치**:
  - PR-D3 spike 단계에서 DX `DockLayoutManager` 가 ▼ → Float menu click 시 mouse 위치 주변에 floating window 생성하는지 1회 수동 검증.
  - **정상 동작 시** = 코드 변경 0, checkbox tick 후 "DX native 처리 — 별도 보정 불필요" 박제 후 D7.1 종료.
  - **비정상 동작 시** = 별도 step 추가 (mouse 위치 capture + `LayoutPanel.FloatLocation` 적용). spike 결과를 §9 에 박제하고 D7.1 step 분할.
- [ ] **D7.2 — 헤더 (PanelHeader + HelpButton)**:
  - **박제 결정**: 두 옵션 중 **DX 자체 caption template** 채택. 기존 UserControl 의 PanelHeader 헤더는 제거하고 DX `LayoutPanel.Caption` + caption template 으로 일원화. docked/floating 양쪽에서 동일하게 Title 노출.
  - HelpButton 은 DX caption template 의 우측 정렬 영역에 button 으로 박제 (caption 의 `CaptionImage` 대신 custom template).
- [ ] **D7.3 — DX skin 정합**:
  - DX `DXSkinManager.ApplyTheme(Application.Current, "WindowsUI Dark")` 1줄 적용.
  - Promaker `ThemeManager.cs` 의 dark background color 와 DX `WindowsUI Dark` skin background 의 색차를 시각 검수 (사용자 1회 확인) 통과 시 D7.3 종료.
  - 색차 큼 → DX `Office2019Colorful` 등 다른 skin 시도 또는 자체 skin mapping 으로 step 분할. 자동 진행은 "사용자 확인 1회" 까지만, 그 외에는 D7.3 만 step 으로 격리하여 D7.1/D7.2 의 자동 진행은 차단하지 않음.

### PR-D8 — AvalonDock 잔재 정리

순서 박제 (rename 은 **마지막 step**):

- [ ] D8.1 — `Apps/Promaker/Directory.Packages.props` 의 `Dirkster.AvalonDock` / `Dirkster.AvalonDock.Themes.Metro` PackageVersion 제거.
- [ ] D8.2 — `Apps/Promaker/Promaker/Promaker.csproj` 의 PackageReference 제거.
- [ ] D8.3 — `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)` 제거.
- [ ] D8.4 — `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs` / `DockPlacement.cs` / `DockTrace.cs` 제거.
- [ ] D8.5 — `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` — DX API 로 재작성 또는 제거.
- [ ] D8.6 — `Apps/Promaker/Docs/done-dock-layout.md` → `Apps/Promaker/Docs/done-dock-avalon.md` 로 rename (역사 보존).
- [ ] **D8.7 (마지막 step — orchestrator 완료 보고 직전)** — 본 문서 `Apps/Promaker/Docs/todo-dock-devexpress.md` → `Apps/Promaker/Docs/done-dock-devexpress.md` 로 rename. rename 직전 §9 진행 체크포인트에 **마지막 commit SHA** + **PR-D1 ~ D8 의 commit SHA 전체** 박제 (다음 세션 추적용). rename 후 추가 phase 진입 금지 — orchestrator 는 즉시 종료 보고.

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
- `done-dock-layout.md` 의 v1~v15 박제는 그대로 — DevExpress 작업 검증 후 PR-D8 rename.

## 7. 결정 사항

| # | 항목 | 결정 |
|---|---|---|
| 1 | 라이브러리 | DevExpress.Wpf.Docking 24.x (안정 latest) |
| 2 | 격리 방식 | 별도 csproj `Promaker.Dock` + **fallback D — Reference HintPath** (PackageReference 회피, DX 11종 + System.Drawing.Common + Microsoft.Win32.SystemEvents 직접 binary link + Private="true"). NuGet graph 자체에서 격리 → Promaker 본체 transitive 0건. ProjectReference 측 추가 PrivateAssets 불필요. §2 spike 표 참조 |
| 3 | namespace 충돌 회피 | Promaker.Dock 만 `<ImplicitUsings>disable</...>` + 명시 using |
| 4 | API 노출 | `IDockManager` + `DockAnchor` record + `DockHost` UserControl (DX type 외부 노출 0) |
| 5 | 작업 순서 | PR-D1 → D2 → D3 → D4 → D5 → D6 → D7 → D8 |
| 6 | 검증 | 각 PR 단위 빌드 + 수동 시나리오 + 사용자 명시 후 commit |
| 7 | AvalonDock 작업물 | dock2 stash@{0} 보존 / `done-dock-layout.md` 보존 (PR-D8 에서 rename) |
| 8 | DX 라이선스 | PR-D1 첫 step 에서 점검 |

## 8. 주의 사항

- DevExpress 라이선스 미보유 / 만료 시 PR-D1 진행 불가 — 즉시 사용자 알림.
- 본 작업은 `dock2` worktree 에서 진행 (사용자가 의도한 work area). dock 폴더 (옛 worktree) 와 혼동 주의 — 사용자가 `make run` / `cd` 시 dock2 경로 명시.
- 각 PR 의 build 검증 — `dotnet build` 0 경고 / 0 오류 후 사용자 명시 시 commit.
- 자가 검열 trigger (CLAUDE.md): 신규 type 3개 이상 / dispatch 재작성 / public API 갱신 충족 시 sub-agent 위임 후 commit 제안.
- 사용자 명시 없이 git commit 금지.
- `--git-commit` 진행 시 dock2 branch 가 remote 없으면 local commit only (push 생략).
- 본 문서 외 다른 문서 (e.g. `done-dock-layout.md`) 가리키는 참조는 파일 경로 명시.

## 9. 진행 체크포인트 (이어받는 세션용)

### 현재 상태 (branch `dock2`, remote 없음)
- `87ad64e1` — PR-D1: Promaker.Dock csproj skeleton + Directory.Packages.props (DevExpress 24.1.7) + todo 문서 신규.
- `27c85180` — PR-D2: IDockManager / DockAnchor / DockHost skeleton + 자가 검열 0 finding.
- `8c2318d9` — todo §9 진행 체크포인트 + PR-D1/D2 완료 박제.
- `fd8f142d` — todo §3.0 sub-agent prompt 골격 + PR-D3 spike 룰 + PR-D5 baseline 박제.
- `a1b51760` — PR-D3: DockLayoutManager layout 트리 + IDockManager 구현 + 24.1.7 spike 박제 + RegisterAnchor/RegisterDocument 중복 가드. 자가 검열 Critical 0 / Major 2 (M1: 초기 raise 누설 우려 → PR-D5 spike 위임 / M2: 중복 가드 → 본 commit 반영) / Minor 4.
- `d6148275` — todo §3 PR-D3 헤더 완료 표기 + §9 갱신 (PR-D3 SHA 박제 + PR-D4 시작점).
- `00079ca8` — PR-D4 진입 차단 박제 (격리 mechanism spike A 계열 전부 실패).
- **PR-D4 격리 mechanism 결정 — fallback D (Reference HintPath) 채택**: §2 spike 표 D row PASS (3 검증 동시 만족). `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj` 의 PackageReference 제거 + Reference HintPath × 13 + Content Include × 1 (Themes.Office2019Colorful 보강). `Apps/Promaker/Directory.Packages.props` 의 DevExpress.Wpf.Docking PackageVersion 제거.
- working tree: 본 박제 commit 으로 mechanism 변경 적용 후 PR-D4 step 2~5 (MainWindow wire-up) 진입.
- AvalonDock 6 fix 작업물은 dock2 stash@{0} 보존 (label: "AvalonDock size snapshot + Root null deferred capture (fix 1-6) — superseded by DevExpress migration plan").

### 다음 작업 — PR-D4 step 2~5 (격리 mechanism = fallback D 적용 완료, wire-up 진행)

step 1 격리 mechanism 은 §2 spike 결과 D 채택 + 본 박제 commit 에 적용 완료. 이하는 step 2~5 의 wire-up 절차:

1. **Promaker.csproj ProjectReference (격리 mechanism D 적용 — PrivateAssets 불필요)**:
   - `Apps/Promaker/Promaker/Promaker.csproj` 에 `<ProjectReference Include="..\Promaker.Dock\Promaker.Dock.csproj" />` 추가 (Promaker.Dock 의 PackageReference 자체가 제거됐으므로 NuGet graph 격리는 mechanism D 측에서 이미 보장. ProjectReference 측 PrivateAssets 불필요).
   - 빌드 후 `dotnet list package --include-transitive` 로 Promaker (Promaker.dll) 의 transitive 에 `DevExpress.*` / `System.Drawing.Common` 0건 검증 (§2 spike D 행).
   - 빌드 후 `Apps/Promaker/Promaker/bin/Debug/net9.0-windows/` 에 DevExpress.*.v24.1.dll 11종 + Themes.Office2019Colorful 자체 deploy 검증 (mechanism D 의 Reference Private=true + Content Include 효과 propagate).
   - **검증 실패 시 차단** — mechanism D 가 ProjectReference carry-over 단계에서 깨졌다는 의미, 즉시 사용자 알림.

2. **MainWindow.xaml — AvalonDock 통째 제거 + DockHost embed**:
   - `Apps/Promaker/Promaker/Windows/MainWindow/MainWindow.xaml` 안의 AvalonDock `<xcad:DockingManager>` 트리 통째 제거.
   - 신규 xmlns: `xmlns:promakerDock="clr-namespace:Promaker.Dock;assembly=Promaker.Dock"`.
   - `<promakerDock:DockHost x:Name="dockHost" />` 1줄 embed (위치는 기존 DockingManager 자리).
   - 기존 5 anchor 의 Content UserControl (ExplorerPane / SimulationPanel / PropertyPanel / HistoryPanel / LlmChatPanel) 은 XAML 에서 직접 instantiate 하지 말고 PR-D4 step 3 에서 code-behind 로 `DockAnchor` wrapping + `RegisterAnchor` 호출.

3. **MainWindow.xaml.cs — RegisterAnchor / RegisterDocument 호출**:
   - 5 anchor 등록: `dockHost.RegisterAnchor(new DockAnchor("explorer", "Explorer", new ExplorerPane(...), DockAnchorPosition.Left));` 형태로 5건.
   - 2 document 등록: Welcome / Canvas (현재 SplitCanvasContainer 또는 동등 wrapping). `HasProject` 토글로 Welcome detach + Canvas attach 정책은 done-dock-layout §3.1 안 A 그대로.
   - 5 anchor 의 정확한 Content type / ctor 시그니처는 기존 AvalonDock XAML 직접 인스턴스 패턴을 본 step 진입 시 grep 으로 확인 후 박제.

4. **AvalonDock 관련 partial / wiring 제거**:
   - `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs` / `DockPlacement.cs` / `DockTrace.cs` 통째 제거 (PR-D8 의 일부지만 PR-D4 wire-up 완료를 위해 본 step 에서 함께 제거).
   - `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` 의 AvalonDock 의존 멤버 제거 또는 DX API 로 재작성 (PR-D5 SSOT 재구성 직전 단계 — PR-D5 가 IsLlmChatVisible 재wiring 한다는 사실 인지하고 본 step 에서는 build 통과 minimal 변경만).
   - **단, PR-D8 의 "rename 마지막 step" 룰 (D8.6 done-dock-layout 보존 rename, D8.7 todo rename) 은 본 PR-D4 에서 손대지 않음.** PackageReference / PackageVersion 의 AvalonDock 제거도 PR-D8 으로 미룸 (transitive 비활성 상태로 두면 본 PR-D4 빌드 시 unused warning 만 발생 — 무시 가능).

5. **빌드 + 자가 검열**:
   - `dotnet build Apps/Promaker/Promaker/Promaker.csproj` 0 오류 (경고는 AvalonDock unused 만 허용).
   - 자가 검열 trigger 충족 (MainWindow XAML 통째 교체 / DockHost embed / 5 anchor wiring 신설) → 검열 agent 위임 의무.
   - 검열 prompt 검증 항목 0번: 사용자 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑.
   - 통과 시 commit (메시지 박제: `[dock2] Dock layout: PR-D4 — Promaker 본체 wire-up (MainWindow DockingManager → DockHost)`).

#### PR-D4 spike 결과 처리 룰
- spike (예: `ExplorerPane` 의 ctor 시그니처, `SplitCanvasContainer` 의 binding context, `MainViewModel.Dock.cs` 의 AvalonDock 의존 멤버 목록) 결과가 PR-D4 step 의 가정과 다를 시 § PR-D3 spike 룰과 동일 — (a) 차이 박제 후 자동 진행 / (b) 기능 자체 부재 (e.g. Promaker 본체 빌드 가능성 차단) 만 사용자 알림 + 차단.

### 새 세션 시작 시 첫 행동
1. 본 문서 `Apps/Promaker/Docs/todo-dock-devexpress.md` 전체 read.
2. 현재 commit 상태 확인 — `git log --oneline -8` 로 PR-D1 ~ PR-D3 (및 본 §9 갱신 commit) 확인.
3. dock2 worktree (`/f/Git/ds2/dock2`) 안에서 작업 — `cd` / `make run` 시 경로 명시.
4. PR-D4 의 §3 항목 5건 진행 → 본 §9 의 "다음 작업" 흐름 따라 step 1~5 순서.
5. PR-D4 commit 후 본 §9 갱신 (PR-D4 completion 박제 + PR-D5 시작점 명시).

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

orchestrator PR-D3 체크박스 [2] 는 "5 anchor (explorer / simulation / log / property / history / llmchat)" 로 6 이름이 적혀 있으나 **done-dock-layout.md §3.1 안 A 의 실제 anchor 는 5개**: explorer / simulation / property / history / llmchat. `log` 는 simulation anchor 가 통합 (done-dock-layout §1 "Simulation/Gantt/Status Monitor/Event Log"). 따라서 **5 anchor 채택** (안 A 가 ground truth — orchestrator 메시지의 ※ 룰 적용).

DockAnchorPosition enum 매핑 (PR-D2 의 `DockAnchor.cs` 그대로):
- `Left` → explorer (DockWidth 320)
- `Bottom` → simulation (DockHeight 200)
- `RightTop` → property
- `RightMiddle` → history (DockHeight 220)
- `RightBottom` → llmchat (기본 Closed=true)
- `Document` → canvas / welcome (DocumentGroup 안의 DocumentPanel 들)
