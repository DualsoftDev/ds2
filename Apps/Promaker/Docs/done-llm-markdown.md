# LLM Chat Markdown View 도입 — 완료

## 결과 요약
`LlmChatPanel` 의 assistant 응답을 **MdXaml `MarkdownScrollViewer`** 로 자동 렌더하도록 도입. assistant role 의 `Text` 전체가 단일 Markdown segment 로 묶여 헤더 / 표 / 불릿 / `**bold**` / `` `inline code` `` / fenced code 등이 다크 테마로 렌더된다. 그 외 role (user/system/tool/thinking/error) 은 기존 평문 `TextBox` 유지. `model-doc-button` 도 기존 Button 분기 그대로 유지.

## 관련 commit
- `b56d531` — LLM Chat markdown view 도입 (A1 — assistant 전체 자동 렌더)
- `f353879` — docs(llm): LLM Chat markdown view 도입 계획 추가 (초기 todo)
- `b56d531` 직전까지 A2 (fence-split) 으로 구현했다가 실응답 검증에서 LLM 의 부분-wrap 회귀 발견 → A1 으로 정책 전환

## 채택된 설계 — A1 + B1

### A1. 렌더 트리거 = role 기반 (fence 무관)
- assistant role + `!IsStreaming` → 단일 Markdown segment (`MarkdownScrollViewer` 직행)
- assistant role + `IsStreaming` → 단일 Plain segment (token 단위 MdXaml 재파싱 회피)
- 그 외 role (user/system/tool/thinking/error) → 단일 Plain segment
- model-doc-button role → `ItemsControl` Visibility=Collapsed + 기존 Button 분기

### B1. 렌더 엔진 = MdXaml 1.27.0
- Markdig 파서 + FlowDocument, MIT
- 활성 유지보수 (2024-02 NuGet, 2025-09 issue #100 응답). 단 1인 유지보수 (whistyun) → 장기 버스 팩터 위험 → segment-분할 구조 (converter + DataTemplateSelector) 는 WebView2 fallback 으로 전환 가능하도록 유지.

### A2 → A1 전환 사유 (2026-05-18)
초기는 A2 (` ```md ``` ` fence 자동 인식) 였으나 실응답에서 LLM (Claude) 이 시스템 프롬프트 지시를 *부분만* 채택. 두 번째 응답에서 표/디바이스 요약만 ` ```md ``` ` 으로 감싸고, 그 앞 "발행 완료" 와 뒤 "⚠ 참고할 부분 …" 은 평문으로 둠 → raw `**bold**` 노출. LLM 의지에 의존하는 fence-based 방식의 본질적 한계 → A1 (assistant 전체 자동) 로 정책 전환.

### 라이브러리 대안 (기각)
- `Markdig.Wpf`: 2024-03 archived → 제외
- `Neo.Markdig.Xaml`: 2021-07 이후 dormant → 제외
- `WebView2 + marked.js`: MS 공식 perf 문서 "small UI 비권장" — 향후 Mermaid/KaTeX 필요시 fallback 으로만 보존

## 구현 내역 (b56d531)

### 신규 파일
- `Promaker/ViewModels/ChatSegment.cs` — `ChatSegmentKind` enum + `ChatSegment` record
- `Promaker/Presentation/TextToSegmentsConverter.cs` — `IMultiValueConverter` (Text+IsStreaming+Role → length=1 segment 배열). `Roles.Assistant` SSOT 사용, pattern matching, `ConvertBack` 은 `NotSupportedException` fail-fast
- `Promaker/Presentation/ChatSegmentTemplateSelector.cs` — `DataTemplateSelector` (switch expression, 예상치 못한 타입 시 `InvalidOperationException`)
- `Promaker/Themes/Theme.Controls.Llm.xaml` — `LlmMarkdownStyle` (FlowDocument 다크 테마 + sub-style `Heading1`~`Heading6` / `CodeSpan` / `CodeBlock` / `Blockquote` / implicit Hyperlink/Table/TableCell/List/ListItem)

### 수정 파일
- `Promaker/Controls/Llm/LlmChatPanel.xaml`
  - xmlns 추가 (`p:` Presentation, `md:` MdXaml)
  - Resources 에 `TextToSegmentsConverter` / `PlainSegmentTemplate` / `MarkdownSegmentTemplate` / `ChatSegmentTemplateSelector` 등록. **순서 중요**: PlainSegmentTemplate 이 `ChatBubbleTextStyle` 을 StaticResource 로 참조하므로 ChatBubbleTextStyle 정의 *뒤* 위치
  - `ChatBubbleTextStyle` 의 DataTrigger Binding 을 outer `ListBoxItem` ancestor 의 `DataContext.Role` 로 변경 (Plain segment 내부 DataContext=ChatSegment 인 경우에도 styling 유지)
  - assistant role 전체 monospace 폐기 (자연어 가독성)
  - ItemTemplate 의 `TextBox` 단일 → `ItemsControl` (segments) + ItemTemplateSelector
  - `MarkdownScrollViewer` 의 `HorizontalScrollBarVisibility="Auto"` (긴 URL 가로 잘림 회피)
- `Promaker/Themes/Theme.Controls.xaml` — `Theme.Controls.Llm.xaml` MergedDictionaries 등록
- `Promaker/Promaker.csproj` — `<PackageReference Include="MdXaml" />`
- `Directory.Packages.props` — `<PackageVersion Include="MdXaml" Version="1.27.0" />`

### 시행착오 (참고용)
- WPF Resources 정의 순서: `PlainSegmentTemplate` (StaticResource 로 `ChatBubbleTextStyle` 참조) 이 forward 참조하면 `XamlParseException` → segment template 들을 모든 Style 정의 뒤로 이동.
- `<Setter Property="Resources">` 는 invalid (CLR property 라 DependencyProperty 아님) → `<Style.Resources>` 자식 element 로 교체.
- MdXaml MarkdownStyle sub-style 의 `x:Key` 는 element Tag 기반 강제 매칭 — `Heading1`~`Heading6` / `CodeSpan` / `CodeBlock` / `Blockquote` 고정 키만 인식 (임의 `H1Style` 등은 적용 안 됨).
- assistant 전체 monospace 폐기로 평문/markdown 영역의 폰트 일관성 확보.

## 보류 / 후속 작업

### dist 전 시각 검증
- **가로 overflow**: 긴 URL / unwrap-able token 응답으로 `HorizontalScrollBarVisibility="Auto"` 실 동작 확인.
- **Light/Dark brush lookup**: `LlmMarkdownStyle` 이 참조하는 `PrimaryTextBrush` / `SecondaryBackgroundBrush` / `HoverBackgroundBrush` / `AccentTextBrush` / `AccentBrush` / `BorderBrush` 6종이 Light/Dark 양 테마 사전에 존재함은 grep 으로 확인. MdXaml FlowDocument 가 별도 visual tree 호스트일 때 실제 lookup 동작은 테마 전환 + assistant 응답으로 시각 검증.
- **Streaming fluidity**: `TextToSegmentsConverter` 가 매 token flush (≈200ms) 마다 새 `ChatSegment[]` 반환 → `ItemsControl` rebuild 가능성. 긴 응답 (수십 KB) 1건으로 체감 측정.
- **MdXaml 파싱 freeze**: `IsStreaming=false` 전환 시 1회 동기 파싱. 매우 긴 응답 (≈20 KB+) UI freeze 가능 — 임계 초과 시 Plain fallback 정책 검토.
- **Hyperlink 보안**: MdXaml `Hyperlink` 자동 활성화. LLM 응답 내 `file://` / `javascript:` 등 악성 URL 클릭 시 동작 미검증. dist 전 보안 검토.

### 별건 PR
- **단위 테스트 프로젝트 신설**: `Promaker.sln` 에 C# xUnit 프로젝트 없음. `ChatSegment` 정책 분기 함수를 추출해 단위 테스트 작성은 별건 PR.
- **ItemsControl → ContentControl 단순화**: 현재 length=1 segment 배열 반환 — `TextToSegmentsConverter` 가 의도적 wrapper (향후 fence-split 재도입 시 교체점). 단순화 필요시 별건.
- **List indent 미세 조정**: 현재 `List.Padding="20,0,0,0"` + `MarkerOffset="6"`. 사용자 체감상 적절. 추가 조정 필요시 본 파일 수정.

## 관련 파일 / 경로 (참조용)
- `Promaker/Controls/Llm/LlmChatPanel.xaml` — DataTemplate / Resources / Style 정의
- `Promaker/ViewModels/LlmChatViewModel.cs`
  - line 765~778: `BuildMarkdownTranscript` — raw `Text` round-trip 보장 검증 완료
  - line 920~989: `EnsureStreamingTurn` / `EndStreamingTurn` / `AppendAssistant` / `FlushAssistantBuffer` — streaming 정책 진입점
  - line 1022~1055: `partial class ChatTurn` + `Roles` 정의
- `Promaker/Presentation/TextToSegmentsConverter.cs` / `ChatSegmentTemplateSelector.cs` (신규)
- `Promaker/ViewModels/ChatSegment.cs` (신규)
- `Promaker/Themes/Theme.Controls.Llm.xaml` (신규)
- `Promaker/Themes/Theme.Controls.xaml` — MergedDictionaries
- `Directory.Packages.props` / `Promaker/Promaker.csproj` — MdXaml 패키지

## 주의 사항 (유지보수)
- **YAML / tool-call 회귀 금지**: `apply_model_doc` 등 도구 호출은 `model-doc-button` role 로 분리 → MdXaml 미통과. assistant 응답에 우연히 ` ```yaml ` fence 가 들어가도 MdXaml 의 markdown 파서가 yaml code block 으로 정상 렌더.
- **`ChatTurn.Text` raw 보존**: `BuildMarkdownTranscript` 가 `t.Text` 그대로 직렬화 — segment 모델 도입에도 Text 자체는 변경하지 않음. CopyAll / Export round-trip 안전.
- **`model-doc-button` 분기**: outer Grid level 에서 ItemsControl 과 Button 이 공존. 향후 segment 모델로 통합 시 `SegmentKind.DocButton` 추가하여 흡수 가능.
- **MdXaml MarkdownStyle sub-style x:Key 규약**: 임의 키 X — `Heading1`~`Heading6` / `CodeSpan` / `CodeBlock` / `Blockquote` 고정. 변경 시 본 규약 확인.
