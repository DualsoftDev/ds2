# 조건 편집 다이얼로그 — Ladder 미리보기 추가 (최종 설계문서)

## 0. 의사결정 확정 사항

| 항목 | 결정 |
|---|---|
| AutoAux base 합성(`WorkGoing ∧ preds`) 포함 여부 | **포함하지 않음** — 사용자가 편집한 트리만 표시 |
| 솔루션 cross-reference 방식 | **ProjectReference 직접 참조 (옵션 A)** — Promaker.csproj 가 dsev2cpu/src 의 AAStoPLC, AAStoPLC.LadderViewer 직접 참조 |
| 다이얼로그 기본 크기 | **2배 확대** — `Width=640→1280`, `Height=560→1120`, `MinWidth=480→960`, `MinHeight=400→800` |
| 렌더링 진입점 | **`AAStoPLC.LadderViewer.Preview` 네임스페이스 사용** — 어댑터/RenderRung 추가 불필요 (LadderViewer 가 이미 제공) |

## 1. 배경 / 목적

`ConditionEditDialog` 는 Call 의 `AutoAux / ComAux / SkipUnmatch` 조건 트리(`CallCondition`)를 GUI 로 편집한다. 현재는 트리 UI 만 있어 PLC 출력 확인까지 변환 → XG5000 import 가 필요하다.

다이얼로그 하단에 **실시간 ladder 미리보기**를 추가해 즉시 확인:
- AND/OR 직병렬 구조
- Rising/Falling edge contact 모양
- ApiDef 이름 라벨

## 2. 범위

| 포함 | 미포함 |
|---|---|
| `CallCondition` → `CoilCondition` 변환 | PLC 주소 할당 / Bind stage 실행 |
| 단일 rung LD 렌더링 | FB 호출 / 전체 ScanProgram 미리보기 |
| 트리 변경 시 즉시 redraw | 시뮬레이션 ON/OFF 오버레이 |
| 사용자 편집 조건만 표시 | AutoAux base 합성 |
| ApiDef.Name 컨택트 라벨 | PLC 변수명/주소 |

## 3. 아키텍처

```
┌──────────────────────────────────────────────────────────────┐
│ ConditionEditDialog (WPF, ds2/Apps/Promaker)                │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ 조건 트리 편집 UI (기존)                                 │ │
│ └─────────────────────────────────────────────────────────┘ │
│        │ on edit (Add/Remove/ToggleOR/ToggleRising/…)        │
│        ▼                                                     │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ <lv:CoilConditionView Condition="..." CoilName="..."/> │ │
│ │   (AAStoPLC.LadderViewer.Preview 의 WPF UserControl)    │ │
│ └─────────────────────────────────────────────────────────┘ │
└───────────────────────┬──────────────────────────────────────┘
                        │ Condition DependencyProperty 변경 → 자동 redraw
                        ▼
┌──────────────────────────────────────────────────────────────┐
│ AAStoPLC.LadderViewer.Preview.CoilConditionPreview            │
│   (LadderViewer 2.1 — 이미 구현 완료)                          │
│   CoilCondition → CoilLayout.run → LadderRung                │
│   → LadderRenderer.RenderRungs                               │
└───────────────────────▲──────────────────────────────────────┘
                        │ Condition 입력 (CoilCondition)
┌───────────────────────┴──────────────────────────────────────┐
│ AAStoPLC.Pipeline.ConditionExprBuilder.buildPreview          │
│   (신규 — Bind 의존 X, store 만 사용)                          │
│   CallCondition tree → CoilCondition                         │
│   ApiCall.Id → ApiDef.Name (raw label)                       │
│   ★ AutoAux base 합성 안 함                                   │
└──────────────────────────────────────────────────────────────┘
```

핵심:
- **PLC 측**: `ConditionExprBuilder` 가 도메인 → IR 변환만 담당.
- **렌더링**: `AAStoPLC.LadderViewer.Preview` 가 IR → ladder 변환/그리기 흡수. **다이얼로그 측 어댑터 코드 0줄**.

## 4. 데이터 흐름

1. 사용자가 트리 편집 → 기존 `_host.TryAction` 으로 store 변경 → `ReloadList`.
2. `ReloadList` 끝에서 `RefreshPreview()` 호출:
   - `_store.Calls[_callId]` → Call 조회.
   - `ConditionExprBuilder.buildPreview(store, call, condType)` → `CoilCondition option`.
   - `PreviewView.Condition` 에 setting (None → null).
3. UserControl 이 자동 redraw (`Condition` / `CoilName` / `LineFactor` 변경 감지).
4. 빈 트리 → UserControl 의 `EmptyHint` 텍스트 표시.

## 5. 파일별 변경 사항

### 5.1 신규 함수 — `AAStoPLC.Pipeline/ConditionExprBuilder.fs`

```fsharp
/// 미리보기 전용 — CallCondition 트리를 ApiDef.Name 라벨로 CoilCondition 변환.
/// PLC 변수명/주소 미해결, AutoAux base 합성 없음. 빈 트리는 None.
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
    let dataSource =
        if call.CallConditions.Count > 0 then call
        else
            call.ReferenceOf
            |> Option.bind (fun id -> Queries.getCall id store)
            |> Option.defaultValue call
    buildForType lookup dataSource.CallConditions condType
```

기존 `buildLookup` 은 OutputBindings(Bind stage 후) 의존 → 미리보기는 별도 lookup 필요. base 합성 로직은 본 함수에 **포함하지 않음** (Emit.coilRungs 와 분리).

### 5.2 솔루션 cross-reference — `Promaker.csproj`

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
- `AAStoPLC` → `Ds2.Core` (이미 동일 사본)
- `AAStoPLC.LadderViewer` → `AAStoPLC` → `Ds2.Core`
- TFM: `AAStoPLC.LadderViewer` 가 `net9.0-windows` (Promaker WPF 와 동일)

### 5.3 XAML — `Dialogs/ConditionEditDialog.xaml`

#### 5.3.1 다이얼로그 크기 2배

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

#### 5.3.2 네임스페이스 + 미리보기 행 추가

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

> ★ 어댑터 / 렌더러 코드 작성 **불필요**. UserControl 이 모든 변환을 흡수.

### 5.4 code-behind — `Dialogs/ConditionEditDialog.xaml.cs`

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

### 5.5 (제거됨) — 어댑터 / 단일-rung 진입점

이전 설계의 `LdLayoutToLadderRung` / `LadderRenderer.RenderRung` 절은 **삭제**.
같은 기능을 `AAStoPLC.LadderViewer.Preview.CoilConditionPreview` 가 이미 제공:

| 구 설계 | 신 설계 |
|---|---|
| `LdLayoutToLadderRung.Convert(layout, name)` 신규 작성 | `CoilConditionPreview.BuildRung(cond, name)` (이미 존재) |
| `LadderRenderer.RenderRung` 진입점 추가 | `CoilConditionPreview.Render(canvas, cond)` 또는 `<lv:CoilConditionView>` |
| 다이얼로그가 `LadderRenderer` 직접 인스턴스화 | UserControl 이 내부에서 처리 |

## 6. UX 동작

| 상황 | 표시 |
|---|---|
| 조건 트리 비어있음 | UserControl `EmptyHint` ("(조건이 비어있음 — TRUE)") |
| 단일 ApiCall | `──┤ ApiName ├──── ( OUT )` |
| AND 다수 | 직렬 contact + coil |
| OR 다수 | 분기 병렬 + coil (Canvas 동적 크기) |
| Rising 단일 | `──|P|──` |
| Rising 복합식 | leaf 별 `|P|` (`applyRising` 기존 동작) |
| 큰 조건 | UserControl 내장 ScrollViewer 가 가로/세로 흡수 |

미리보기 라벨에 "(사용자 입력 트리만)" 명시 — AutoAux base 누락 인지.

## 7. 엣지 케이스

1. **참조 깨진 ApiCall** — `lookup` None → `buildForType` 가 `Raw apiCallName` fallback. 이름 그대로 컨택트 라벨로 노출.
2. **테마** — 다이얼로그 Loaded 시 `LadderRenderer.DarkMode = <앱 테마>` 설정. UserControl background 는 transparent.
3. **변경 폭주** — `CoilConditionView.OnAnyChanged` 가 매번 redraw 하지만 동기 가벼움. 필요 시 50ms 디바운스 추가 가능.
4. **동일 ApiCall 중복 참조** — CoilCondition 트리에 같은 Var 중복 가능. layout 이 그대로 처리.

## 8. 구현 순서 (PR 단위)

1. **PR1** ✅ — `AAStoPLC.LadderViewer.Preview` (`CoilConditionPreview` + `CoilConditionView`) — **이미 구현 완료** (LadderViewer 2.1)
2. **PR2** — `ConditionExprBuilder.buildPreview` 추가 + 단위 테스트 (AAStoPLC.Tests).
3. **PR3** — `Promaker.csproj` 에 ProjectReference 2개 추가 + ds2 솔루션 빌드 검증.
4. **PR4** — `ConditionEditDialog` XAML/code-behind 수정 (창 크기 + UserControl + RefreshPreview).
5. **PR5** — UX 보강 (테마 동기화, 디바운스, 참조 누락 색상).

## 9. 리스크 / 대응

| 리스크 | 대응 |
|---|---|
| ds2 ↔ dsev2cpu 디렉토리 레이아웃 차이로 ProjectReference 깨짐 | 빌드 가이드 문서화. CI 동일 부모 디렉토리. |
| AAStoPLC.LadderViewer TFM(`net9.0-windows`) ↔ Promaker TFM 불일치 | Promaker 가 WPF 앱 → 동일. 점검 항목. |
| Ds2.Core 두 경로 노출 | 동일 fsproj 단일 인스턴스 → 검증만. |
| AutoAux 미리보기 ↔ 실제 PLC 출력 불일치 (base 누락) | UserControl 상단 라벨 명시. |
| 작은 모니터(1366×768) 잘림 | `MinHeight=800` + CanResize. |
| F# `CoilCondition option` 의 C# 직접 setting | `FSharpOption.get_IsSome` helper (5.4). |

## 10. 미해결 / 후속

- SkipUnmatch 의 의미적 시각화 — 후속.
- 시뮬레이션 ON/OFF 오버레이 — UserControl 에 `Signals` DP 추가 필요 (`LadderRenderer.Signals` API 활용).
- AutoAux base 합성 표시 토글 — 본 사양 명시적 제외, 후속.
- GridSplitter 미리보기 높이 사용자 조정 — 후속.

## 11. `AAStoPLC.LadderViewer.Preview` API 요약 (PR1 산출물)

| 진입점 | 시그니처 | 용도 |
|---|---|---|
| `CoilConditionPreview.Render(canvas, cond, "OUT")` | static void | 코드 1줄 렌더 |
| `CoilConditionPreview.RenderPartial(canvas, cond)` | static void | 코일 없는 부분식 |
| `CoilConditionPreview.Render(canvas, items)` | static void | 다중 rung |
| `CoilConditionPreview.BuildRung(cond, name)` | → `LadderRung` | 빌드만 (직접 LadderRenderer 사용) |
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
