# LLM Chat Markdown View 도입

## 작업 목표
`LlmChatPanel` 에서 assistant 응답에 포함된 ` ```md … ``` ` 형태의 fenced block 을 자동으로 markdown view 로 렌더한다.
평문/YAML 응답은 기존대로 `TextBox` (monospace) 로 표시, markdown segment 만 `MarkdownScrollViewer` (MdXaml) 로 치환한다.

## 배경 / 맥락
- 현재 chat 창: `Promaker/Controls/Llm/LlmChatPanel.xaml`
  - 모든 turn 이 read-only `TextBox` 로 표시 (plain text)
  - assistant role 에는 monospace 폰트만 적용 (line 148~150 주석 참조 — 의도적으로 markdown 파서 보류)
  - `model-doc-button` 이라는 특수 role 로 payload 를 별도 Dialog 로 띄우는 패턴 이미 존재 (XAML line 332~367 Grid 분기)
- 다크 테마 SSOT (`PrimaryBackgroundBrush`, `AccentBrush`, `SecondaryTextBrush` 등) 갖춰져 있음
- ListBox `CopyAllCommand` / `ExportCommand` 가 ContextMenu 에 있음 (raw `Text` 기반 — 변경 불필요)
- **`ChatTurn` 정의 위치**: `Promaker/ViewModels/LlmChatViewModel.cs:1022` 의 동일 파일 내 `partial class ChatTurn`. `Roles` static class 도 같은 파일 line 1028~1040. 별도 `ViewModels/Llm/` 폴더는 존재하지 않음.

## 결정된 설계 — 권장안 (A2 + B1)

### A2. 렌더 트리거 = ` ```md ``` ` fence 자동 인식
- assistant turn 의 `Text` 를 regex 로 split → `[plain, mdSegment, plain, mdSegment, …]`
- 평문 segment 는 기존 `TextBox` 스타일 그대로
- markdown segment 만 `MarkdownScrollViewer` 로 렌더
- 시스템 프롬프트에 "긴 설명/도움말/매뉴얼 형태 응답은 ` ```md ` … ` ``` ` 로 감싸라" 지시 추가 필요 (단 YAML 발행 양식 제외 — 아래 Phase 3 참조)

### B1. 렌더 엔진 = MdXaml
- NuGet: `MdXaml` 1.27.0 (Markdig 파서 + FlowDocument, AvalonEdit syntax highlight)
- 활성 유지보수 확인: 2024-02 NuGet release, 2025-09 issue #100 응답. 단 사실상 1인 유지보수 (whistyun) → 장기 버스 팩터 위험 있음 → 본 PR 의 segment-분할 구조는 WebView2 fallback (B3) 으로 전환 시 그대로 활용 가능하도록 유지.
- WPF native, FlowDocument selection 동작
- 다크 테마 XAML 스타일 오버라이드 가능 (`MarkdownStyle` DependencyProperty 로 ResourceDictionary 통째 주입이 정석. `Document.Background`/`Hyperlink.Foreground` 만 override 로는 부족 — Phase 2 상세 참조)

### 라이브러리 대안 (기각)
- `Markdig.Wpf`: 2024-03 archived → 제외
- `Neo.Markdig.Xaml`: 2021-07 이후 dormant → 제외
- `WebView2 + marked.js`: MS 공식 perf 문서 "small UI 비권장" (learn.microsoft.com/microsoft-edge/webview2/concepts/performance) → 보험 조항으로만 유지

### 선택 이유 (대안 대비)
- **A1 (전체 자동 렌더)** 대비: YAML/평문 오파싱 위험 없음, 사용자 발화 의도와 정확 일치
- **A3 (별도 Dialog 버튼)** 대비: 인라인 렌더 — 클릭 없이 즉시 가독성 확보
- **B3 (WebView2 + marked.js)** 대비: 초기화 비용/IPC 복잡도 회피, 기존 WPF selection 통합 자연스러움

## 남은 할 일

### Phase 1 — 패키지 + segment split (helper / converter 분리)

- [ ] **패키지 추가** (중앙 버전 관리 확인 완료 — `Directory.Packages.props` 의 `ManagePackageVersionsCentrally=true`)
  - `Directory.Packages.props` 에 `<PackageVersion Include="MdXaml" Version="1.27.0" />` 명시 (버전 없으면 NU1604 빌드 에러)
  - 단순 텍스트 렌더만 필요하면 `MdXaml` 본체로 충분. fenced code 의 syntax highlight 도 필요하면 `MdXaml.Full` 또는 `MdXaml.SyntaxEditor` 패키지로 교체 검토 (Phase 4 검증 단계에서 실응답 보고 결정)
  - `Promaker/Promaker.csproj` 에 `<PackageReference Include="MdXaml" />` 추가

- [ ] **Split 로직 위치** — `ChatTurn` 내부 메서드 / 필드로 두지 않는다
  - `ChatTurn._text` 는 `[ObservableProperty]` 로 streaming 중 token 단위 변경됨 (`LlmChatViewModel.cs:987 FlushAssistantBuffer` 에서 `_streamingTurn.Text += _pendingAssistant.ToString()`). 매 token split 재계산은 layout shift + MdXaml 재파싱 폭주 유발.
  - **위치**: `Promaker/ViewModels/ChatSegmentSplitter.cs` (static helper) + `Promaker/Behaviors/TextToSegmentsConverter.cs` (IValueConverter, XAML 에서 사용)
  - **Segment model**: `record ChatSegment(SegmentKind Kind, string Text)`, `enum SegmentKind { Plain, Markdown }`. `ModelDocButton` 은 본 PR 범위 외 (아래 Phase 2 절충안 참조)

- [ ] **Streaming 중 split 금지 정책 (핵심)**
  - `IsStreaming=true` 동안에는 single `Plain` segment 만 반환 (converter 내부에서 분기)
  - 종료 시점 = `LlmChatViewModel.cs:933 EndStreamingTurn()` 에서 `IsStreaming=false` 로 전환. 이때 `Text` 변경 알림 + `IsStreaming` 변경 알림이 converter 를 재호출 → 그 시점에 1회 split 수행
  - Converter `MultiBinding` 으로 `Text` + `IsStreaming` 양쪽 dependency 등록

- [ ] **CommonMark 준수 regex**
  - 부적합: ` ```md\s*\n([\s\S]*?)``` ` (대소문자/`markdown` alias 누락, indented closing fence 미허용, 4-backtick wrap 내부 ``` 에서 조기 종료)
  - 적용: `(?ms)^(?<f>` + 백틱3개이상매칭 + `)(?:md|markdown)[ \t]*\r?\n([\s\S]*?)\r?\n[ ]{0,3}\k<f>\s*$`
    - .NET regex 표기: `@"(?ms)^(?<f>`{3,})(?:md|markdown)[ \t]*\r?\n([\s\S]*?)\r?\n[ ]{0,3}\k<f>\s*$"`
  - backreference `\k<f>` 로 opening fence 와 동일 길이 close 만 매칭 → 4-backtick wrap 안전

- [ ] **단위 테스트** (xUnit, `Promaker.Tests` 프로젝트 — 없으면 신설 결정 필요)
  - 정상 ` ```md ` block
  - ` ```markdown ` alias
  - 4-backtick wrap (내부 ``` 가 종료 fence 로 오판되지 않아야 함)
  - indented closing fence (최대 3 space)

### Phase 2 — DataTemplate 변경 + 다크 테마 ResourceDictionary

- [ ] **`LlmChatPanel.xaml` DataTemplate 재작성** (line 325~370)
  - 현재 outer `Border > Grid > [TextBox, Button(ModelDocButton)]` 구조 유지
  - `Grid` 내부의 `TextBox` 를 `ItemsControl` 로 교체 (segments 바인딩, `TextToSegmentsConverter` 사용)
  - `ModelDocButton` 분기 Button 은 그대로 유지 — segment `ItemsControl` 과 outer Grid level 에서 공존 (Visibility 분기). 향후 ModelDocButton 일반화 시 segment 모델로 흡수 가능.
- [ ] **DataTemplateSelector** (또는 `ContentControl` + 두 DataTemplate)
  - `SegmentKind.Plain` → 기존 `ChatBubbleTextStyle` `TextBox`
  - `SegmentKind.Markdown` → `MarkdownScrollViewer` (`xmlns:md="clr-namespace:MdXaml;assembly=MdXaml"`)

- [ ] **다크 테마 ResourceDictionary 신설** — `Themes/Theme.Controls.Llm.xaml`
  - MdXaml 의 `MarkdownStyle` DependencyProperty 에 주입할 `MarkdownStyle` ResourceDictionary 작성
  - **점검 체크리스트** (MdXaml 기본 prebuilt dark theme 없음 — 외부 자료 확인됨: whistyun.github.io/MdXaml/render_markdown_in_control.html):
    - `Table` — 셀 배경/border (기본 흰색 → SecondaryBackgroundBrush)
    - `InlineCode` — 인라인 ` `code` ` 회색 배경
    - `CodeBlock` — fenced code block 배경/foreground (단색 또는 AvalonEdit syntax)
    - `Hyperlink` — 기본 파랑 → AccentBrush
    - `Blockquote` — 좌측 border 색
    - `HR` — 검정선 → BorderBrush
    - `H1`~`H6` — Foreground / 크기 / margin
    - `ListItem` (`ul`/`ol`) — bullet 색
  - `App.xaml` 의 ResourceDictionary merge 에 등록

- [ ] **assistant 평문 segment 폰트 정책 재검토**
  - 현재 line 146~150 주석: "YAML 등 schema-doc 응답 등폭 정렬 위해 monospace. 자연어 부분도 mono 라 약간 어색하나… 전체 mono 로 타협"
  - markdown segment 도입 후에는 평문 segment 와 markdown segment 의 폰트 mismatch 우려 — Plain segment 는 자연어 가독성 위해 기본 (proportional) 폰트, Markdown segment 내부 fenced code 만 mono 로 환원하는 방향 검토
  - 같은 시점에 line 146~150 주석도 갱신 (markdown 파서 도입 완료로 "scope 외" 문구 제거)

### Phase 3 — 시스템 프롬프트 보강

- [ ] 위치: `Promaker/LlmAgent/Prompts/` (실존 폴더 확인 완료 — `1.entities.md`, `2.modeling.md`, `3.tooling.md`, `4.attachments.md`, `9.environment.md`)
  - 합성 메커니즘: `PromptLoader.LoadComposed()` 가 embedded + override 합성
  - 추가 위치 후보: `4.attachments.md` 와 같은 layer 의 신규 `10.output-format.md` (numbering convention 따름)

- [ ] **양수 규칙 (markdown wrap 권장)**
  - "사용자에게 보여줄 도움말 / 매뉴얼 / 긴 설명 형태의 자연어 응답은 ` ```md ` 로 감싸라"
  - 한 줄 예시 포함

- [ ] **음수 규칙 (필수)** — `apply_model_doc` 발행 양식과의 충돌 방지
  - `3.tooling.md` 가 YAML 발행 양식을 강제 → 새 markdown wrap 지시가 이를 흡수해서는 안 됨
  - 명시: **"`apply_model_doc` 호출 / YAML 모델 본문 / tool 사용 인자는 절대 ` ```md ` 로 감싸지 말 것. 도구 호출은 기존 양식 (`3.tooling.md`) 그대로 유지."**
  - 합성 순서상 음수 규칙이 양수 규칙 뒤에 와야 우선 적용 — `10.output-format.md` 내부에서 양수 → 음수 순으로 기술

### Phase 4 — 검증

- [ ] 실제 LLM 응답 샘플 (한국어 설명 + 코드 + 표) 으로 렌더 결과 확인
- [ ] 다크 테마 sub-element 8종 (Table/InlineCode/CodeBlock/Hyperlink/Blockquote/HR/H1~H6/ListItem) 색상 확인
- [ ] selection / copy (Ctrl+C) 동작 확인 — segment 별
- [ ] **CopyAll / Export round-trip**:
  - `LlmChatViewModel.cs:765 BuildMarkdownTranscript` 가 `t.Text` raw 그대로 직렬화 — `ChatTurn.Text` 는 segment concat 으로 덮어쓰지 않고 원본 보존 (Segments 는 별도 derived property/converter output)
  - export .md 를 VS Code preview / GitHub 등 외부 viewer 에서 열어 ` ```md ` fence 가 plain 본문으로 정상 렌더되는지 확인 (중첩 markdown 의도가 외부에서 유지되는지)
- [ ] YAML 응답 (`model-doc-button` 분기) 회귀 없음 확인 — Phase 3 음수 규칙이 동작하는지
- [ ] **Edge case**:
  - 빈 md block (` ```md\n``` `) → FlowDocument 0px 렌더로 시각적으로 사라지지 않는지 (최소 height 또는 안내 텍스트)
  - streaming 중 partial fence (` ```md\n... ` 종료 fence 미도착) → Plain 으로만 표시되다 종료 시점에 1회 split 확인
  - 4-backtick wrap 내부 ` ``` ` 가 본문에 포함된 케이스
- [ ] **regex 단위 테스트** (Phase 1 의 4 케이스) 통과

### Phase 5 — 자가 검열 (CLAUDE.md 강제 절차)

- [ ] **Trigger 매핑**:
  - ② 신규 함수/타입 3개 이상 (`ChatSegment` record, `SegmentKind` enum, `ChatSegmentSplitter` static helper, `TextToSegmentsConverter`, `SegmentTemplateSelector` 등 다수)
  - ③ 2개 이상 파일 동시 변경 (XAML + ViewModel + helper + converter + ResourceDictionary + Prompts/*.md 등 6개 이상)
  - ④ DataTemplate / segment-dispatch 재작성
- [ ] `Agent` 도구 (subagent_type=general-purpose 또는 code-review skill) 에 본 PR 의 commit 범위 / unstaged diff 명시 + 검토 항목 (논리 오류, regex 누락 사례, MdXaml resource override 누락, copy/export round-trip) 명시
- [ ] **리포트 형식**: ① 검열 대상 ② Reviewer 발견 이슈 분류 (Critical/Major/Minor) ③ 자가 수정 결과 ④ 잔여 우려

## 관련 파일 / 경로

- `Promaker/Controls/Llm/LlmChatPanel.xaml` — DataTemplate 변경 대상 (line 325~370 outer Grid 내부)
- `Promaker/Controls/Llm/LlmChatPanel.xaml.cs` — code-behind (필요 시)
- `Promaker/ViewModels/LlmChatViewModel.cs`
  - line 765~778: `BuildMarkdownTranscript` — raw `Text` round-trip 보장 검증 대상
  - line 920~989: `EnsureStreamingTurn` / `EndStreamingTurn` / `AppendAssistant` / `FlushAssistantBuffer` — streaming 중 split 금지 정책의 trigger 지점
  - line 1022~1055: `partial class ChatTurn` + `Roles` 정의 — `ObservableProperty` 패턴, `Payload` (ModelDocButton 용) 와 신규 segments derived property 추가 시 위치
- `Promaker/ViewModels/ChatSegmentSplitter.cs` (신규) — static split helper
- `Promaker/Behaviors/TextToSegmentsConverter.cs` (신규) — IValueConverter (MultiBinding Text+IsStreaming)
- `Promaker/Themes/Theme.Controls.Llm.xaml` (신규) — MdXaml `MarkdownStyle` 다크 ResourceDictionary
- `Promaker/App.xaml` — ResourceDictionary merge 등록
- `Promaker/LlmAgent/Prompts/10.output-format.md` (신규) — markdown wrap 양수/음수 규칙
- `Promaker/LlmAgent/PromptLoader.cs` — 합성 메커니즘 확인 (Phase 3)
- `Directory.Packages.props` — `<PackageVersion Include="MdXaml" Version="1.27.0" />`
- `Promaker/Promaker.csproj` — `<PackageReference Include="MdXaml" />`

## 주의 사항

- **YAML / tool-call 회귀 금지**: `apply_model_doc` 등 도구 호출 응답은 절대 ` ```md ` 로 wrap 되어서는 안 됨. Phase 3 음수 규칙이 이를 차단하며, 만약 LLM 이 무시하면 split 후 segment 내부에 YAML 이 들어가 MdXaml 이 YAML 을 markdown 으로 잘못 파싱. → Phase 4 회귀 케이스 필수.

- **Streaming token 폭주 회피**: streaming 중 매 token split 시 ItemsControl 재build + MdXaml 재파싱으로 CPU 폭주. 반드시 `IsStreaming=false` 전환 시 1회만 split. converter MultiBinding 으로 `IsStreaming` 의존성 등록.

- **`ChatTurn.Text` raw 보존**: `BuildMarkdownTranscript` 가 `t.Text` 그대로 직렬화 (`LlmChatViewModel.cs:765~778`). Segments 는 derived (converter output) 로만 두고 Text 는 원본 ` ```md ` fence 포함된 상태 유지. round-trip 보장.

- **`model-doc-button` 분기 통합은 본 PR 범위 외**: segment 모델로 일반화하면 XAML Grid 내 두 mechanism 이 깔끔히 합쳐지나, scope 폭증 → 본 PR 에서는 outer Grid level 공존만. 향후 별건 작업으로 `SegmentKind.DocButton` 추가하여 흡수.

- **MdXaml 다크 테마**: 기본 FlowDocument 흰 배경 — `Document.Background` / `Hyperlink.Foreground` 만 override 로는 sub-element (Table/InlineCode/Blockquote/HR 등) 가 누락됨. `MarkdownStyle` DependencyProperty 로 ResourceDictionary 통째 주입이 정석. Phase 2 의 8종 체크리스트 필수.

- **regex CommonMark 사양**: lazy ` ```md\s*\n([\s\S]*?)``` ` 는 ①alias `markdown` 미지원 ②4-backtick wrap 내부 ``` 에서 조기 종료 ③indented closing fence 미허용. backreference `\k<f>` 기반 동적 매칭으로 해결. Phase 1 의 단위 테스트 4 케이스 필수.

- **segment 경계 selection**: FlowDocument/TextBox 가 분리되므로 segment 를 가로지르는 selection 은 불가. 전체 복사는 ContextMenu `CopyAllCommand` 로 대응 (이미 존재). turn-단위 복사 ContextMenu 항목 추가도 부가 개선으로 고려 (현재 "전체 대화 복사" 옆에 "현재 turn 복사").

- **MdXaml 1인 유지보수 (whistyun)**: 장기 버스 팩터 위험. 본 PR 의 segment-분할 구조 (helper + converter + DataTemplateSelector) 는 렌더 엔진과 분리되어 있어 향후 WebView2 (B3) fallback 으로 전환 시 segment 모델 그대로 재사용 가능 — 구조적 보험으로 유지.

- **assistant 평문 segment 폰트**: 현재 `ChatBubbleTextStyle` 의 assistant role 트리거가 전체 monospace (line 148~150). markdown segment 도입 후 평문 segment 와 markdown segment 의 폰트 mismatch 발생 가능 → 평문 segment 는 자연어 가독성 위해 proportional 로 환원, markdown 내부 fenced code 만 mono 유지 검토.

- **commit 권한**: CLAUDE.md `feedback_commit_authorization` 메모리 — multi-step 작업 종료 후 commit 은 별도 confirm 필요.

## 트레이드오프 (감안하고 진행)

- LLM 이 시스템 프롬프트 지시를 무시하면 markdown view 가 안 나타남 — 추후 heuristic 자동 wrap 으로 확장 가능 (h1~h6 / `- ` / 표 패턴 감지)
- MdXaml 의 표/이미지 렌더 일부 차이 → Phase 4 검증에서 실제 응답 샘플로 확인
- MdXaml 활성도가 1인 유지보수에 의존 — 본 PR 구조는 엔진 교체 시에도 segment 분할 / converter / DataTemplateSelector 재사용 가능하도록 유지
- 풍부한 렌더 (Mermaid, KaTeX) 필요 시점이 오면 WebView2 (B3) 로 전환 검토 — segment 분할 구조 그대로 활용
