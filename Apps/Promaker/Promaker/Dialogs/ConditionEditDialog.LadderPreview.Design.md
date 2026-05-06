# 조건 편집 다이얼로그 — Ladder 미리보기 추가 (최종 설계문서)

## 0. 의사결정 확정 사항

| 항목 | 결정 |
|---|---|
| AutoAux base 합성(`WorkGoing ∧ preds`) 포함 여부 | **포함하지 않음** — 사용자가 편집한 트리만 표시 |
| 솔루션 cross-reference 방식 | **ProjectReference 직접 참조 (옵션 A)** — Promaker.csproj 가 dsev2cpu/src 의 AAStoPLC, AAStoPLC.LadderViewer 직접 참조 |
| 다이얼로그 기본 크기 | **2배 확대** — `Width=640→1280`, `Height=560→1120`, `MinWidth=480→960`, `MinHeight=400→800` |
| 렌더링 진입점 | **`AAStoPLC.LadderViewer.Preview` 네임스페이스 사용** — 어댑터/RenderRung 추가 불필요 |
| ApiCall 변수명 해결 | 미리보기는 ApiDef.Name 그대로 (PLC 변수명 미해결) |

## 1. 현재 코드 상태 매트릭스 (2026-05-06 기준)

### AAStoPLC (PLC 파이프라인) — 본 미리보기와 무관하게 완료된 변경

| 위치 | 변경 | 상태 |
|---|---|---|
| `AAStoPLC.Ir/Rung.fs` | `CoilCondition` IR (Var/NegVar/And/Or/Not/Rising/Falling/Raw) | ✅ 기존 |
| `AAStoPLC.Ir/Rung.fs` | `CoilLayout.run` (CoilCondition → LdLayoutResult) | ✅ 기존 |
| `AAStoPLC.Pipeline/StageTypes.fs` | `CallPlan.AutoAuxCond / ComAuxCond : CoilCondition option` | ✅ 완료 |
| `AAStoPLC.Pipeline/ConditionExprBuilder.fs` | `buildLookup` (single Call) | ✅ 완료 |
| `AAStoPLC.Pipeline/ConditionExprBuilder.fs` | `buildGlobalLookup` (cross-Call, ReferenceOf 추적) | ✅ 완료 |
| `AAStoPLC.Pipeline/ConditionExprBuilder.fs` | `buildForType`, `ofNode`, `applyRising` | ✅ 완료 |
| `AAStoPLC.Pipeline/Stages/Bind.fs` | 3-pass 구조 (plan 골격 → globalLookup → AutoAux/ComAux 채움) | ✅ 완료 |
| `AAStoPLC.Pipeline/Stages/Emit.fs` | `coilRungs` 가 `(WorkGoing ∧ preds) ∧ user-AutoAux` / `user-ComAux` 합성 | ✅ 완료 |
| `AAStoPLC.Pipeline/ConditionExprBuilder.fs` | **`buildPreview`** (미리보기 전용 — Bind 무관) | ❌ 본 PR 대상 |

### AAStoPLC.LadderViewer (렌더러) — 미리보기 인프라 (LadderViewer 2.1)

| 위치 | 변경 | 상태 |
|---|---|---|
| `AAStoPLC.LadderViewer/Preview/CoilConditionPreview.cs` | 정적 API + Canvas 확장 메서드 | ✅ 완료 |
| `AAStoPLC.LadderViewer/Preview/CoilConditionView.xaml(.cs)` | WPF UserControl + DependencyProperty 4개 | ✅ 완료 |

### Promaker (다이얼로그) — 본 PR 의 핵심 작업

| 위치 | 변경 | 상태 |
|---|---|---|
| `Promaker.csproj` | AAStoPLC, AAStoPLC.LadderViewer ProjectReference 추가 | ❌ 본 PR 대상 |
| `Dialogs/ConditionEditDialog.xaml` | 창 크기 2배 + `<lv:CoilConditionView>` 행 추가 | ❌ 본 PR 대상 |
| `Dialogs/ConditionEditDialog.xaml.cs` | `RefreshPreview()` + `ReloadList()` 끝에서 호출 | ❌ 본 PR 대상 |

## 2. 배경 / 목적

`ConditionEditDialog` 는 Call 의 `AutoAux / ComAux / SkipUnmatch` 조건 트리(`CallCondition`)를 GUI 로 편집한다. 현재는 트리 UI 만 있어 PLC 출력 확인까지 변환 → XG5000 import 가 필요하다.

다이얼로그 하단에 **실시간 ladder 미리보기**를 추가해 즉시 확인:
- AND/OR 직병렬 구조
- Rising/Falling edge contact 모양
- ApiDef 이름 라벨

## 3. 범위

| 포함 | 미포함 |
|---|---|
| `CallCondition` → `CoilCondition` 변환 (`buildPreview`) | PLC 주소 할당 / Bind stage 실행 |
| 단일 rung LD 렌더링 | FB 호출 / 전체 ScanProgram 미리보기 |
| 트리 변경 시 즉시 redraw | 시뮬레이션 ON/OFF 오버레이 |
| 사용자 편집 조건만 표시 | AutoAux base 합성 (Emit 가 별도로 처리) |
| ApiDef.Name 컨택트 라벨 | PLC 변수명/주소 |

## 4. 아키텍처

```
┌──────────────────────────────────────────────────────────────┐
│ ConditionEditDialog (WPF, ds2/Apps/Promaker)                │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ 조건 트리 편집 UI (기존)                                 │ │
│ └─────────────────────────────────────────────────────────┘ │
│        │ on edit (Add/Remove/ToggleOR/ToggleRising/…)        │
│        ▼  ReloadList() → RefreshPreview()                    │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ <lv:CoilConditionView Condition="..." CoilName="..."/> │ │
│ │   (AAStoPLC.LadderViewer.Preview 의 WPF UserControl)    │ │
│ └─────────────────────────────────────────────────────────┘ │
└───────────────────────┬──────────────────────────────────────┘
                        │ Condition DependencyProperty 변경 → 자동 redraw
                        ▼
┌──────────────────────────────────────────────────────────────┐
│ AAStoPLC.LadderViewer.Preview.CoilConditionPreview ✅         │
│   CoilCondition → CoilLayout.run → LadderRung                │
│   → LadderRenderer.RenderRungs                               │
└───────────────────────▲──────────────────────────────────────┘
                        │ Condition 입력 (CoilCondition)
┌───────────────────────┴──────────────────────────────────────┐
│ AAStoPLC.Pipeline.ConditionExprBuilder.buildPreview ❌ TODO   │
│   CallCondition tree → CoilCondition                         │
│   ApiCall.Id → ApiDef.Name (raw label)                       │
│   ★ AutoAux base 합성 안 함 — Emit 의 합성 로직과 분리         │
└──────────────────────────────────────────────────────────────┘
```

핵심:
- **PLC 측**: `ConditionExprBuilder` 가 도메인(`CallCondition`) → IR(`CoilCondition`) 변환만. PLC 출력용은 `buildLookup`/`buildGlobalLookup` 으로 이미 동작 중. 미리보기용은 `buildPreview` 신규.
- **렌더링**: `AAStoPLC.LadderViewer.Preview` 가 IR → ladder 흡수. 다이얼로그 어댑터 코드 0줄.

## 5. 데이터 흐름

1. 사용자가 트리 편집 → 기존 `_host.TryAction` 으로 store 변경 → `ReloadList`.
2. `ReloadList` 끝에서 `RefreshPreview()` 호출:
   - `_store.Calls[_callId]` → Call 조회.
   - `ConditionExprBuilder.buildPreview(store, call, condType)` → `CoilCondition option`.
   - `PreviewView.Condition` 에 setting (None → null).
3. UserControl 이 자동 redraw (`Condition` / `CoilName` / `LineFactor` 변경 감지).
4. 빈 트리 → UserControl 의 `EmptyHint` 텍스트 표시.

> **PLC 출력 경로(별도)**: Bind.run 이 `buildGlobalLookup` 으로 plan 단위 AutoAuxCond/ComAuxCond 채움 → Emit 에서 `(WorkGoing ∧ preds) ∧ user` (AutoAux) / `user` (ComAux) 합성. 미리보기와 완전 분리.

## 6. 파일별 변경 사항

### 6.1 신규 함수 — `AAStoPLC.Pipeline/ConditionExprBuilder.fs` (PR2)

```fsharp
/// 미리보기 전용 — CallCondition 트리를 ApiDef.Name 라벨로 CoilCondition 변환.
/// PLC 변수명/주소 미해결, AutoAux base 합성 없음. 빈 트리는 None.
/// store 만 의존 — Bind stage 결과(CallPlan list) 불필요.
let buildPreview (store: DsStore) (call: Call) (condType: CallConditionType)
                 : CoilCondition option =
    let lookup : ApiCallVarLookup =
        fun apiCallId ->
            store.ApiCalls.Values
            |> Seq.tryFind (fun ac -> ac.Id = apiCallId)
            |> Option.bind (fun ac ->
                ac.ApiDefId
                |> Option.bind (fun id -> Queries.getApiDef id store)
                |> Option.map (fun def -> def.Name))
    // ReferenceOf 추적 — 편집 대상 Call 자체에 조건 있으면 그대로,
    // 없으면 원본 Call 의 CallConditions 사용 (SimIndex 와 동일 규칙).
    let dataSource =
        if call.CallConditions.Count > 0 then call
        else
            call.ReferenceOf
            |> Option.bind (fun id -> Queries.getCall id store)
            |> Option.defaultValue call
    buildForType lookup dataSource.CallConditions condType
```

기존 `buildLookup` / `buildGlobalLookup` 은 InputBindings/OutputBindings 의존(Bind stage 후) → 미리보기는 별도 lookup 필요. **base 합성 로직은 본 함수에 포함하지 않음** (Emit.coilRungs 와 분리).

### 6.2 솔루션 cross-reference — `Promaker.csproj` (PR3)

옵션 A: ProjectReference 직접 추가.

```xml
<ItemGroup>
  <!-- 기존 ds2 솔루션 내부 참조 -->
  <ProjectReference Include="..\..\..\Solutions\Core\Ds2.Core\Ds2.Core.fsproj" />
  ...

  <!-- ★ 신규 — dsev2cpu 솔루션 직접 참조 -->
  <ProjectReference Include="..\..\..\..\..\dsev2cpu\src\AAStoPLC\AAStoPLC.fsproj" />
  <ProjectReference Include="..\..\..\..\..\dsev2cpu\src\AAStoPLC.LadderViewer\AAStoPLC.LadderViewer.csproj" />
</ItemGroup>
```

전제 — 두 리포(`ds2`, `dsev2cpu`)가 동일 부모 디렉토리(`/mnt/c/ds/`)에 클론. CI 도 동일.

트랜지티브 의존성:
- `AAStoPLC` → `Ds2.Core` (이미 동일 사본 — 단일 어셈블리로 해소)
- `AAStoPLC.LadderViewer` → `AAStoPLC` → `Ds2.Core`
- TFM: `AAStoPLC.LadderViewer` 가 `net9.0-windows` (Promaker WPF 와 동일)

### 6.3 XAML — `Dialogs/ConditionEditDialog.xaml` (PR4)

#### 6.3.1 다이얼로그 크기 2배

```xml
<Window ...
        Title="조건 편집"
        Width="1280"      <!-- was 640 -->
        Height="1120"     <!-- was 560 -->
        MinWidth="960"    <!-- was 480 -->
        MinHeight="800"   <!-- was 400 -->
        ... >
```

`WindowStartupLocation="CenterOwner"` / `ResizeMode="CanResize"` 유지.

#### 6.3.2 네임스페이스 + 미리보기 행 추가

```xml
<Window ...
    xmlns:lv="clr-namespace:AAStoPLC.LadderViewer.Preview;assembly=AAStoPLC.LadderViewer"
    ...>

    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>             <!-- header -->
        <RowDefinition Height="10"/>
        <RowDefinition Height="*" MinHeight="120"/>     <!-- 조건 트리 -->
        <RowDefinition Height="6"/>
        <RowDefinition Height="240" MinHeight="80"/>    <!-- ★ 미리보기 -->
        <RowDefinition Height="10"/>
        <RowDefinition Height="Auto"/>             <!-- footer -->
    </Grid.RowDefinitions>

    <!-- ★ Ladder Preview pane — UserControl 1줄 -->
    <Border Grid.Row="4"
            BorderBrush="{DynamicResource BorderBrush}"
            BorderThickness="1"
            CornerRadius="3">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0"
                       Text="Ladder 미리보기 (사용자 입력 트리만)"
                       Foreground="{DynamicResource SecondaryTextBrush}"
                       FontSize="11"
                       Margin="6,4"/>
            <lv:CoilConditionView
                Grid.Row="1"
                x:Name="PreviewView"
                CoilName="OUT"
                LineFactor="0.6"
                EmptyHint="(조건이 비어있음 — TRUE)"/>
        </Grid>
    </Border>
```

기존 footer `Grid.Row="4"` → `Grid.Row="6"` 시프트.

> ★ 어댑터 / 렌더러 코드 작성 **불필요**. UserControl 이 변환 흡수.

### 6.4 code-behind — `Dialogs/ConditionEditDialog.xaml.cs` (PR4)

신규 추가는 `RefreshPreview()` 1개 메서드 + `ReloadList` 끝에서 호출:

```csharp
using AAStoPLC.Pipeline;          // ConditionExprBuilder
using AAStoPLC.Ir;                // CoilCondition
using Microsoft.FSharp.Core;      // FSharpOption

private void RefreshPreview()
{
    if (!_host.TryRef(() => _store.Calls[_callId], out var call))
    {
        PreviewView.Condition = null;
        return;
    }

    var condOpt = ConditionExprBuilder.buildPreview(_store, call, _condType);
    PreviewView.Condition =
        FSharpOption<CoilCondition>.get_IsSome(condOpt) ? condOpt.Value : null;
}
```

`ReloadList()` 마지막에 `RefreshPreview()` 호출 추가.

> 이전 설계의 `LadderRenderer` 직접 사용 / `LdLayoutToLadderRung` 어댑터 / `LadderRenderer.RenderRung` 진입점 추가는 **모두 불필요**. UserControl 의 `Condition` 프로퍼티에 IR 객체만 setting.

## 7. UX 동작

| 상황 | 표시 |
|---|---|
| 조건 트리 비어있음 | UserControl `EmptyHint` ("(조건이 비어있음 — TRUE)") |
| 단일 ApiCall | `──┤ ApiName ├──── ( OUT )` |
| AND 다수 | 직렬 contact + coil |
| OR 다수 | 분기 병렬 + coil (Canvas 동적 크기) |
| Rising 단일 | `──|P|──` |
| Rising 복합식 | leaf 별 `|P|` (`applyRising` 기존 동작) |
| 큰 조건 | UserControl 내장 ScrollViewer 가 가로/세로 흡수 |

미리보기 라벨 "(사용자 입력 트리만)" — AutoAux base 누락 인지.

## 8. 엣지 케이스

1. **참조 깨진 ApiCall** — `lookup` None → `buildForType` 가 `Raw apiCallName` fallback. 컨택트 라벨로 노출.
2. **테마** — 다이얼로그 Loaded 시 `LadderRenderer.DarkMode = <앱 테마>` 설정. UserControl background 는 transparent.
3. **변경 폭주** — `CoilConditionView.OnAnyChanged` 가 매번 redraw (동기 가벼움). 필요 시 50ms 디바운스 추가.
4. **동일 ApiCall 중복 참조** — CoilCondition 트리에 같은 Var 중복 가능. layout 이 그대로 처리.
5. **ReferenceOf 기반 Call** — `buildPreview` 가 자체 conditions 우선, 없으면 원본 Call 추적 (SimIndex 와 동일 규칙).

## 9. 구현 순서 (PR 단위)

| PR | 내용 | 상태 |
|---|---|---|
| PR1 | `AAStoPLC.LadderViewer.Preview` (`CoilConditionPreview` + `CoilConditionView`) | ✅ 완료 (LadderViewer 2.1) |
| PR1.5 | AAStoPLC PLC 파이프라인의 CallCondition→PLC 변환 (`buildLookup` + `buildGlobalLookup` + Bind 3-pass + Emit 합성) | ✅ 완료 (별도 작업) |
| **PR2** | `ConditionExprBuilder.buildPreview` 추가 + 단위 테스트 | ❌ 진행 예정 |
| **PR3** | `Promaker.csproj` 에 ProjectReference 2개 추가 + ds2 솔루션 빌드 검증 | ❌ 진행 예정 |
| **PR4** | `ConditionEditDialog` XAML/code-behind (창 크기 + UserControl + RefreshPreview) | ❌ 진행 예정 |
| PR5 | UX 보강 (테마 동기화, 디바운스, 참조 누락 색상) | ⏳ 후속 |

## 10. 리스크 / 대응

| 리스크 | 대응 |
|---|---|
| ds2 ↔ dsev2cpu 디렉토리 레이아웃 차이로 ProjectReference 깨짐 | 빌드 가이드 문서화. CI 동일 부모 디렉토리. |
| AAStoPLC.LadderViewer TFM(`net9.0-windows`) ↔ Promaker TFM 불일치 | Promaker 가 WPF 앱 → 동일. 점검 항목. |
| Ds2.Core 두 경로 노출 | 동일 fsproj 단일 인스턴스 → 검증만. |
| AutoAux 미리보기 ↔ 실제 PLC 출력 불일치 (base 누락) | UserControl 상단 라벨 "(사용자 입력 트리만)" 명시. |
| 작은 모니터(1366×768) 잘림 | `MinHeight=800` + CanResize. |
| F# `CoilCondition option` 의 C# 직접 setting | `FSharpOption.get_IsSome` helper (6.4 참조). |
| `buildPreview` 와 `buildLookup`/`buildGlobalLookup` 동작 차이로 인한 혼동 | 변환 lookup 차이만 있음 (Bind 의존 vs ApiDef.Name 직사용). 함수 docstring 에 명시. |

## 11. 미해결 / 후속

- SkipUnmatch 의 의미적 시각화 — 후속.
- 시뮬레이션 ON/OFF 오버레이 — `CoilConditionView` 에 `Signals` DP 추가 필요 (`LadderRenderer.Signals` API 활용 가능).
- AutoAux base 합성 표시 토글 — 본 사양 명시적 제외, 후속.
- GridSplitter 미리보기 높이 사용자 조정 — 후속.

## 12. `AAStoPLC.LadderViewer.Preview` API 요약 (PR1 산출물)

| 진입점 | 시그니처 | 용도 |
|---|---|---|
| `CoilConditionPreview.Render(canvas, cond, "OUT")` | static void | 코드 1줄 렌더 |
| `CoilConditionPreview.RenderPartial(canvas, cond)` | static void | 코일 없는 부분식 |
| `CoilConditionPreview.Render(canvas, items)` | static void | 다중 rung |
| `CoilConditionPreview.BuildRung(cond, name)` | → `LadderRung` | 빌드만 (직접 LadderRenderer 사용 시) |
| `CoilConditionPreview.BuildRungs(items)` | → `IReadOnlyList<LadderRung>` | 다중 빌드 |
| `myCanvas.DrawCondition(cond)` | extension | Canvas 1줄 호출 |
| `myCanvas.DrawConditionPartial(cond)` | extension | 부분식 |
| `<lv:CoilConditionView Condition="..." CoilName="..."/>` | UserControl | **본 다이얼로그가 사용** |

UserControl DependencyProperty:
- `Condition : CoilCondition?` — null 이면 `EmptyHint` 표시
- `CoilName : string?` — null/빈 문자열이면 코일 없는 부분식
- `LineFactor : double` (0.15~1.0) — 빈 칸 가로 압축 비율
- `EmptyHint : string` — 빈 상태 안내 텍스트

변경 시 자동 redraw, ScrollViewer 내장.

## 13. 참고 — AAStoPLC PLC 파이프라인의 CallCondition 처리 (별도, 본 PR 무관)

미리보기와 별개로 PLC 출력 경로의 CallCondition 처리는 이미 완성됨:

- `AAStoPLC.Pipeline.ConditionExprBuilder.buildGlobalLookup` — 모든 plan 의 Bindings 통합. 다른 Call 의 ApiCall 참조(예: `CCCCC.RET` 가 `B.ADV` 참조) 지원. ReferenceOf 추적.
- `AAStoPLC.Pipeline.Stages.Bind.run` — 3-pass: plan 골격 → globalLookup → AutoAux/ComAux 채움.
- `AAStoPLC.Pipeline.StageTypes.CallPlan.AutoAuxCond / ComAuxCond` — `CoilCondition option`.
- `AAStoPLC.Pipeline.Stages.Emit.coilRungs` — AutoAux: `(WorkGoing ∧ preds) ∧ user`, ComAux: `user`(없으면 AlwaysTrue).

미리보기 (`buildPreview`) 와 PLC 출력 (`buildGlobalLookup` + Emit 합성) 은 lookup 만 다르고 변환 로직(`ofNode` / `buildForType` / `applyRising`)은 공유.
