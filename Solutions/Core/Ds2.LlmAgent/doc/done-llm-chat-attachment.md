# LLM Chat 첨부파일 — 완료된 작업 (historical record)

> `todo-llm-chat-attachment.md` 의 phase 별 산출물 누적. 본 문서는 *그 시점의 사실* 을 보존 — path / 식별자 갱신 안 함 (`git log --follow` 로 추적).

## Phase 3a-pre — Spike (rev 1, 2026-05-09)

### S-1 — Claude CLI 이미지/PDF 입력 채널 조사

- 조사 대상: `claude --version` = 2.1.136 (Claude Code)
- `claude --help` 풀 출력 검토 결과:
  - **`--image` 류 옵션 없음**
  - `--file <specs...>` 발견되나 형식이 `file_id:relative_path` (e.g. `--file file_abc:doc.txt file_def:img.png`) — Anthropic Files API 에 이미 업로드된 `file_id` 를 download 하는 용도이지, 로컬 이미지 업로드 채널이 아님
  - `--input-format <format>` 옵션 (text / **stream-json**) 발견 — non-interactive `--print` 모드 한정
- **결론**: Claude CLI 의 정석 multimodal 채널 = **`--input-format stream-json`** + JSON Lines stdin. 이 모드에서는 user message content 를 array 로 받기 때문에 Anthropic API 의 multipart content block (`{type:"image", source:{type:"base64", media_type:..., data:...}}` / `{type:"document", source:{type:"base64", media_type:"application/pdf", data:...}}`) 을 그대로 wire 가능. 이미지/PDF 모두 동일 채널.
- **`ClaudeCliProvider.fs:69` 영향**: 현재 `Stdin = Some prompt` (텍스트 stdin). multimodal turn 시 stream-json input 으로 전환 + JSON Lines 직렬화 helper 추가 필요. 기존 텍스트 only turn 은 회귀 방지 위해 text input 유지 (capability 분기) 또는 일관성을 위해 항상 stream-json 으로 전환 — commit-2 시점 결정.

### S-2 — Codex CLI multimodal 입력 spec

- 조사 대상: `codex --version` = codex-cli 0.128.0
- `codex --help` 풀 출력 검토 결과:
  - **`-i, --image <FILE>...` 발견** — 설명: "Optional image(s) to attach to the initial prompt"
  - **path 기반** 인자 (반복 가능, 다중 이미지)
  - PDF 류 옵션 **없음** → Codex 는 PDF 미지원 (정책 11 표와 일치)
  - 외부 검증 통과 사항 (rev 3) — Codex 가 PNG/JPEG/GIF/WebP 모두 지원하는 점 — 채널 (위치 인자 vs path 옵션) 이 **path 옵션** 으로 확정
- **결론**: Codex CLI 는 path 인자만 받음. 클립보드 paste 이미지처럼 disk 에 없는 경우 임시 파일 spool 후 path 전달 패턴 필요.
- **`CodexCliProvider.fs:85,107` 영향**: 현재 prompt 위치 인자만 사용. 첨부 시 `-i <path1> -i <path2> ...` 추가 + 임시 파일 spool helper 책임은 Codex 어댑터 안에서 수용 (DU 는 단일 case 유지).

### S-3 — Capabilities 표 확정

| Provider | 이미지 채널 | 이미지 cap | PDF | PDF cap | 비고 |
|---|---|---|---|---|---|
| Claude CLI 2.1.136 | `--input-format stream-json` content block (bytes base64) | Anthropic API 와 동일 = 5MB | O (document content block) | 32MB | stdin JSON Lines 로 전송 |
| Codex CLI 0.128.0 | `-i/--image <FILE>...` (path) | OpenAI API 기준 ≈ 20MB (미공식 — 추후 검증) | X | — | bytes 첨부 시 임시 파일 spool 필요 |
| Anthropic API | SDK content block (bytes) | 5MB (base64 inline) | O | 32MB | Phase 3b |
| OpenAI API | SDK content block (bytes) | 20MB | △ S-4 | △ S-4 | gpt-4o vision |
| Ollama | 모델 의존 (bytes, vision 모델만) | 모델 의존 | X | — | `EnsureCli` 시 `/api/show` 동적 조회 |

### Spike 결과로 todo 갱신된 항목

- §6 V-2 (Claude CLI 인자) → **closed** — stream-json content block 채널
- §6 V-3 (Codex CLI 채널) → **closed** — `-i/--image` path 기반
- §3.2 capability matrix Claude CLI 행 채움
- 정책 13 — DU 단일 `Image of name * bytes * mime` 유지 결정 (`ImagePath` case 미추가). Codex 어댑터가 임시 파일 spool 책임
- §7 주의사항 — Codex 임시 파일 spool 위치 + ChildProcessTracker lifetime 노트 (구현 시 명시 예정)

### S-4 — Phase 3b 진입 전 보류

- Anthropic.SDK 12.20.0 / OpenAI .NET SDK 2.10.0 / OllamaSharp 5.4.25 어댑터 PDF/image content block 매핑
- OpenAI API PDF 직접 입력 지원 여부 (V-1) 본 spike 에서 확정
- Phase 3a 완료 후 별도 진행

## Phase 3a — commit-1 (a93e763, 2026-05-09)

### 산출물

- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` (신규) — `ImageFormat` enum (Png/Jpeg/Gif/Webp) + `Attachment` DU (Image/Pdf/TextFile) + `LlmUserMessage` record + `OfText` factory + `Capabilities` record + `TextOnly` factory
- `Ds2.LlmAgent.fsproj` `<Compile Include>` 에 `LlmEvent.fs` 다음으로 등록
- 빌드 통과 (오류 0 / 경고 0). 사용처 0 — dead code 의도 (commit 정의 = "type 신설")

## Phase 3a — commit-2 (7450520, 2026-05-09)

### 산출물

- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` — `Capabilities.ImagesOnly(maxImageBytes)` / `ImagesAndPdf(maxImageBytes, maxPdfBytes)` static factory 2종 추가 (C# 측 호출 친화)
- `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` — `ILlmProvider.Send` 시그니처 `string prompt` → `LlmUserMessage msg` 교체 + `Capabilities` 추상 멤버 추가
- `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` — instance Send + interface impl 갱신, `Capabilities = ImagesAndPdf(5L*1024L*1024L, 32L*1024L*1024L)` (Anthropic API 와 동일 — `--input-format stream-json` 으로 동일 wire)
- `Solutions/Core/Ds2.LlmAgent/CodexCliProvider.fs` — 동일 패턴, `Capabilities = ImagesOnly(20L*1024L*1024L)` (V-1 미해결 placeholder)
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` — ctor 에 `Capabilities` 인자 추가, `Send(LlmUserMessage)` 시그니처, `Capabilities` 프로퍼티 노출
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs` — 4종 factory (Anthropic/OpenAI/Groq/Ollama) 에 capability 분배. Anthropic = `ImagesAndPdf(5MB, 32MB)` / OpenAI = `ImagesOnly(20MB)` / Groq = `TextOnly` / Ollama = `TextOnly` (모델 의존 동적 갱신은 commit-4..N 으로 미룸)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:466` — `_provider.Send(promptForProvider, ct)` → `_provider.Send(LlmUserMessage.OfText(promptForProvider), ct)` wrap
- 빌드 통과 (Promaker.sln 전체, 오류 0 / 경고 2 — OllamaSharp source generator 사전 환경 무관)
- 자가 검열 통과: 7파일 +76/-15, Critical/Major 0건, Minor 1 (V-1 placeholder, 코드 주석 명시)

### 회귀 호환

- 본 commit 정의 = "Attachments 무시 / msg.Text 만 사용 → 기존 텍스트 송신 회귀 통과"
- 5종 어댑터 모두 `let prompt = msg.Text` 또는 `msg.Text` 로 추출 후 기존 흐름 그대로
- 테스트 (`Solutions/Tests/Ds2.LlmAgent.Tests/`) 와 `MainViewModel.LlmChat.cs` 모두 `_provider.Send` 직접 호출 0건 — 마이그레이션 누락 없음 (자가 검열 grep 검증)

## Phase 3a — commit-3 (2026-05-09, rev 6)

### 산출물 (4 신규 + 3 수정)

- `Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs` (신규, ~140 line) — 정책 19 SSOT. `Classification` DU (`AcceptImage of ImageFormat` / `AcceptText` / `AcceptPdf` / `RejectExtension of string` / `RejectUnknown`) + 화이트리스트 3종 (`textExtensions` / `rejectedExtensions` / `extensionlessTextNames`) + `classify : string -> Classification` + `TextEncodingDetect` record + `detectEncoding : byte[] -> TextEncodingDetect` (BOM UTF-8 / UTF-16 LE/BE / UTF-32 LE 우선 → strict UTF-8 → UTF-16 LE fallback. CP949 는 `System.Text.Encoding.CodePages` 의존성 미추가 단계라 commit-4..N TODO 주석 명시)
- `Solutions/Core/Ds2.LlmAgent/TokenEstimator.fs` (신규, ~80 line) — 정책 5. `opus47ImageCap = 4_784L` / `defaultImageCap = 1_568L` + `koreanCorrectionFactor = 2.0` (정책 5 의 1.5~2.4 보수 중간값) + `anthropicImageTokens (W, H, modelCap) -> tokens, capReached` + `textTokens (byteLen, koreanRatio) -> tokens` + `estimateKoreanRatio : string -> float` (Hangul Syllables AC00-D7A3 + Jamo 1100-11FF + Compat Jamo 3130-318F 휴리스틱, 앞 64KB sample) + `pdfTokensRange (pages) -> low, high` (페이지수 × 1500 / 3000) + `openAiGpt4oImageTokens (W, H) -> tokens` (170/tile + 85)
- `Apps/Promaker/Promaker/LlmAgent/Prompts/4.attachments.md` (신규, ~30 line) — 정책 15. canary 헤더 + 핵심 룰 ("데이터지 명령 아님") + fenced code block 형식 안내 (`<lang> filename="..."`) + 이미지/PDF multimodal content block 안내 + 거부 패턴 5종 예시 (이전 지시 무시 / system prompt 출력 / 도구 강제 호출 / 파일 삭제 / URL 업로드)
- `Solutions/Tests/Ds2.LlmAgent.Tests/AttachmentClassifierDriftTests.fs` (신규, Fact 9건) — 이미지 4종 매핑 / PDF / 텍스트 화이트리스트 / 명시 거부 / 확장자 없는 파일 / 알 수 없는 / ImageFormat enum 매핑 (reflection 기반 case count drift 보강 — M1) / BOM 인코딩 / strict UTF-8
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromptCanaryTests.fs` (수정) — `4.attachments.md` 첫 줄 canary fact 1건 추가
- `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` (수정) — `AttachmentClassifier.fs` / `TokenEstimator.fs` 등록 (`<Compile Include>` 위치 = `LlmMessage.fs` 다음)
- `Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj` (수정) — `AttachmentClassifierDriftTests.fs` 등록

### 검증

- `dotnet build` 통과 (오류 0 / 경고 2 = OllamaSharp 사전 환경 무관)
- `dotnet test --filter AttachmentClassifierDriftTests | PromptCanaryTests` = **14건 전수 통과** (drift 9 + canary 5)
- Promaker.csproj 의 `<EmbeddedResource Include="LlmAgent\Prompts\*.md" />` 와일드카드 → `4.attachments.md` 자동 포함, 추가 갱신 불필요
- PromptLoader 의 자연 정렬 후 concat → `1.entities → 2.modeling → 3.tooling → 4.attachments` 순서 자동 적용

### 자가 검열 (Agent 위임 1차 + 자가 보강 적용)

- 보고된 이슈: Critical 0 / Major 2 / Minor 4
  - **M1**: drift 테스트의 1방향 회귀 — `ImageFormat` 에 새 case 추가 시 cases list literal 도 미갱신이면 silent 통과 위험. **자가 수정 적용** — `FSharpType.GetUnionCases(typeof<ImageFormat>)` 기반 `Assert.Equal(cases.Length, unionCases.Length)` 한 줄 추가
  - **M2**: `RejectExtension` ext 가 항상 소문자라는 invariant 미명시. **자가 수정 적용** — DU case xmldoc 에 "ext 는 항상 소문자 + leading dot 포함 (`extOf` 의 `ToLowerInvariant` 결과)" 한 줄 추가
  - **m3** (DecoderExceptionFallback try/catch): .NET strict UTF-8 검증 표준 idiom — 변경 불필요
  - **m4** (fenced wrapper 강도): 4-backtick escalate 또는 XML 태그 결정은 commit-4..N (UI wrapper 생성 코드) 진입 시
  - **m5** (injection 패턴 일반화 한 줄 추가): commit-4..N 진입 시
  - **m6** (OpenAI tile short side scale 분기 누락): dead code 단계라 체감 영향 없음. commit-4..N 진입 전 보정
- 잔여 우려: dead code 보존 기간이 길어지면 namespace 직속 public 노출이 외부 우연 의존 형성 risk — Phase 3a 종료 시점까지 호출자 미발생 시 `internal` 검토

## Phase 3a — commit-4 (2026-05-09, rev 7)

### 산출물 (1 신규 + 3 수정)

- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs` (신규, ~280 line, partial class) — 정책 6/14/18/19 적용
  - `AttachmentChipVm` — filename + size + ≈token + Source(F# `Attachment`) 보유 plain class. `SizeLabel` (B/KB/MB 자동) + `TokensLabel` (`≈Nt` or empty)
  - `MaxAttachmentCount = 10` (정책 6) + `MaxTextBytes = 1MB` (텍스트 cap) const
  - `Attachments: ObservableCollection<AttachmentChipVm>` + `HookAttachmentsCollection` (CollectionChanged → `HasAttachments` PropertyChanged + `SendCommand.NotifyCanExecuteChanged`)
  - `[ObservableProperty] AttachmentNotice` — chip 영역 1줄 안내 SSOT (정책 8/9/MI-5)
  - `AddPathsAsync(IReadOnlyList<string>)` — drag-drop / 향후 file paste 진입점. STA sync = `ClassifyPathSync` 검증 → background `LoadAcceptedAttachments` (bytes 로드 + dim + 토큰 추정) → dispatcher chip 추가
  - `AddImageBytesAsync(byte[], mime, suggestedName)` — clipboard image 진입점 (commit-5 paste handler 가 호출 예정)
  - `RemoveAttachmentCommand` (RelayCommand<AttachmentChipVm>) — chip × 버튼 + Delete 키 binding
  - `MimeOf(ImageFormat)` — F# DU C# interop (`IsPng`/`IsJpeg`/...) 으로 enum switch 회피
  - `TryReadImageDimFromPath/Bytes` + `ReadImageDim` — `BitmapDecoder.Create(stream, IgnoreColorProfile, None)` metadata-only 경로 (full decode 회피, 정책 18)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` (수정 ~5 line) — constructor 에 `HookAttachmentsCollection()` 한 줄 + `CanSend` 본문은 commit-4 단계 text 필수 유지 (commit-6 의 첨부-only 송신 + default prefix 와 함께 SendAsync 본체 갱신 예정)
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` (수정 ~80 line) — UserControl 에 `AllowDrop="True"` + `PreviewDragEnter/Leave/Over/Drop` 4종 hookup. Grid 행 2개 추가 (notice TextBlock + chip ItemsControl). chip = WrapPanel + Border focusable + KeyBinding(Delete) + × Button. `RemoveAttachmentCommand` 는 `RelativeSource AncestorType=UserControl` 으로 binding
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml.cs` (수정 ~30 line) — `Panel_PreviewDragEnter/Leave/Over/Drop` 4종 핸들러. 모두 `e.Handled=true` + FileDrop 만 `Copy` effect. Drop handler 는 fire-and-forget `vm.AddPathsAsync(paths)` 호출

### 검증

- `dotnet build Apps/Promaker/Promaker.sln` 통과 (오류 0 / 경고 2 = OllamaSharp 사전 환경 무관)
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests --no-build` = **201건 전수 통과**
- 회귀 호환: `CanSend` 본체 무수정 → 기존 텍스트 송신 회귀 통과. commit-4 단계는 chip UI / drag-drop / 토큰 추정만 활성화. 첨부 wire 는 commit-6 의 race-free SendAsync 와 함께 진입

### 자가 검열 (Agent 위임 1차 + 자가 보강 적용)

- 보고된 이슈: Critical 0 / Major 1 / Minor 4
  - **M1**: `MainWindow.xaml.cs:143` 의 `Window_DragEnter` bubble 단계 후킹으로 `FileDragOverlay.Visibility=Visible` → LLM Chat 패널 위에 `.sdf`/`.json` drag 시 overlay 가 chip UI 를 가림. **자가 수정 적용** — `LlmChatPanel.xaml` 에 `PreviewDragEnter`/`PreviewDragLeave` 추가 + `LlmChatPanel.xaml.cs` 에 `Panel_PreviewDragEnter`/`Panel_PreviewDragLeave` 2종 핸들러 추가 (모두 `e.Handled=true`). 정책 14 의 "MainWindow bubble 차단" 의도가 DragEnter/Leave 까지 확장
  - **mi1** (path null 가드): drag-drop OS 가드로 실용 영향 없음 — 영구 skip
  - **mi2** (`BitmapDecoder` lazy frame 위험): `using stream` 블록 *안* 에서 `PixelWidth/Height` 즉시 읽고 tuple 반환 → 안전. 추가 metadata 사용 시 `BitmapCacheOption.OnLoad` 검토
  - **mi3** (미사용 `Microsoft.FSharp.Core` using): **자가 수정 적용** — using 정리
  - **mi4** (`AddImageBytesAsync` notices 단일화 누락): commit-5 paste 합류 시 helper 통일 — 이월
- 잔여 우려: 없음 (M1 적용으로 정책 14 완전 충족)

### 미처리 (commit-5 / commit-6 으로 분할)

- commit-5: Ctrl+V (`DataObject.AddPastingHandler`), 클립보드 이미지 PNG 인코딩, `OnSelectedProviderChanged` 시 미지원 첨부 강제 제거, DragOver visual cue
- commit-6: race-free SendAsync (snapshot + provider 캡처), `CanSend` 갱신 (첨부-only 허용), default prompt prefix, ImportPlan label fallback, prompt injection wrapper, history summary, status / 413 매핑

## Phase 3a — commit-4 1차 review 적용 (rev 8, 2026-05-09)

### 산출물 (수정 7 + NuGet 1)

- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — F1 SendAsync 진입 시 `Attachments.Count > 0` 분기 추가 (system turn 안내 1줄: "첨부 N개는 표시만 됩니다. 실제 LLM 전송 wire 는 후속 commit (Phase 3a commit-6) 에서 활성화됩니다."). F4 Reset 명령에 `Attachments.Clear()` + `AttachmentNotice = ""` 추가
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs` — F2 race 재검증: `AddPathsAsync` 의 dispatcher add 단계 for-loop 안에서 `if (Attachments.Count >= MaxAttachmentCount)` 검증 + cap overflow 시 notice append. `AddImageBytesAsync` 도 동일 패턴 (`Attachments.Count` 재확인 후 거부)
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` — F5 chip filename TextBlock 에 `MaxWidth="220"` + `TextTrimming="CharacterEllipsis"` + `ToolTip="{Binding FileName}"` (긴 파일명 layout 보호)
- `Apps/Promaker/Promaker/App.xaml.cs` — F3 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 1회 등록 (`McpConfigWriter.SweepStale` 다음 줄)
- `Apps/Promaker/Promaker/Promaker.csproj` — F3 `<PackageReference Include="System.Text.Encoding.CodePages" />`
- `Apps/Promaker/Directory.Packages.props` — F3 `<PackageVersion Include="System.Text.Encoding.CodePages" Version="9.0.0" />`
- `Solutions/Directory.Packages.props` — F3 동일 PackageVersion 추가 (테스트 + Ds2.LlmAgent transitive 용)
- `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` — F3 `<PackageReference Include="System.Text.Encoding.CodePages" />`
- `Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs` — F3 `tryCp949` (code page 949, lazy probe) + `isStrictDecodable` helper. `detectEncoding` BOM 미일치 분기 = strict UTF-8 시도 → fail 시 strict CP949 → 모두 실패 시 UTF-16 LE fallback. 기존 try/catch 의 `EncoderExceptionFallback` 인자 (decode 무영향) 정리
- `Solutions/Tests/Ds2.LlmAgent.Tests/AttachmentClassifierDriftTests.fs` — F3 CP949 case 추가 ("안녕하세요" CP949 인코딩 → strict UTF-8 fail → CP949 검출 검증). `RegisterProvider` 호출은 idempotent

### 검증

- `dotnet build Apps/Promaker/Promaker.sln` 통과 (오류 0 / 경고 2 = OllamaSharp 사전 환경 무관)
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests --no-build` = **202건 전수 통과** (CP949 case +1)

### 의도적 잔여 (2차 review 17건은 사용자 지시 대기)

- 2차 review (Critical 1 / Major 5 / Minor 11) 결과는 별도 처리 — C1 (`openAiGpt4oImageTokens` long-side 2048 fit) / M1 (Capability byte-cap SSOT 통합) / M2 (`estimateKoreanRatio` surrogate pair) / M3 (UTF-8 replacement fallback + logWarn) / M4 (provider Send capability gating helper) / M5 (drift 보안 critical contains assertion) + Minor 11
- commit-5 / commit-6 분할 유지

## Phase 3a — commit-4 2차 review sweep (rev 9, 2026-05-09)

### 산출물 (수정 10)

- `Solutions/Core/Ds2.LlmAgent/TokenEstimator.fs`
  - **C1**: `openAiGpt4oImageTokens` 알고리즘 정정 — 1단계 short-side fit → 2단계 (long 2048 fit → short 768 fit) 비율 보존 scale. 4096×1000 입력 → 765 토큰 (이전 ~1445, 약 2배 오차 해소)
  - **M2**: `estimateKoreanRatio` `text.[i]` UTF-16 char loop → `EnumerateRunes` (`System.Text.Rune.IsWhiteSpace`). surrogate pair (emoji / 한자보충) underestimate 해소
- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs`
  - **M1**: `module CapabilityPresets` 신설 — `AnthropicWire` / `OpenAiApiWire` / `CodexCliWire` / `DefaultMaxAttachmentCount` literal. 4 호출처 byte literal 중복 제거
  - **M4**: `module LlmUserMessageOps` 신설 — `WarnUnsupportedAttachments(caps, msg)` log warn 만 (commit-4 dead code 단계, commit-6 strict 전환 예정)
  - **m4**: `Capabilities.TextOnly` static member → `static member val ... with get` (1회 평가)
  - **m11**: `LlmUserMessage.Create(text, attachments)` null-safe factory
- `Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs`
  - **M3**: 모든 strict 시도 실패 시 fallback = UTF-16 LE → **UTF-8 replacement** + `Log.provider.Warn` 1줄. ASCII 부분 보존 + 비-UTF-8 만 U+FFFD
  - **m2**: `extensionlessTextNames` 의 dead entry "license.txt" 제거 (.txt 는 ext 분기 미진입)
- `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` / `CodexCliProvider.fs`
  - **M1**: `Capabilities.ImagesAndPdf(...)` literal → `CapabilityPresets.AnthropicWire` / `CapabilityPresets.CodexCliWire`
  - **M4**: Send 진입 시 `LlmUserMessageOps.WarnUnsupportedAttachments` 호출
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs`
  - **M1**: Anthropic = `CapabilityPresets.AnthropicWire`, OpenAI = `CapabilityPresets.OpenAiApiWire`
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs`
  - **M4**: Send 진입 시 `LlmUserMessageOps.WarnUnsupportedAttachments(_capabilities, msg)` 호출 (Anthropic/OpenAI/Ollama/Groq 4 provider 공통 진입)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs`
  - **m1**: `MaxAttachmentCount = Ds2.LlmAgent.CapabilityPresets.DefaultMaxAttachmentCount` (F# literal 위임)
  - **m10**: `ExtOf(ImageFormat) -> ".jpg"` helper 추가, notice 의 `{img.Item}` (= "Jpeg") → `{ExtOf(img.Item)}` (= ".jpg")
- `Apps/Promaker/Promaker/LlmAgent/Prompts/4.attachments.md`
  - **m8**: `\``` escape → 자연어 ("3개의 backtick + lang + filename")
- `Solutions/Tests/Ds2.LlmAgent.Tests/AttachmentClassifierDriftTests.fs`
  - **M5**: 보안·정책 critical contains assertion 2 fact 추가 (`rejectedExtensions` 16개 + `textExtensions` 15개)
  - **m7**: invalid UTF-8 (`0xC3 0x28`) fixture 추가 — UTF-8 replacement fallback 분기 회귀 보장

### 검증

- `dotnet build Apps/Promaker/Promaker.sln` 통과 (오류 0 / 경고 2 OllamaSharp 사전 환경)
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests --no-build` = **205건 전수 통과** (기존 202 + 신규 3)

### 자가 검열 (Agent 위임 — Critical 0 / Major 0 / Minor 1)

- **Mi-1**: `LlmUserMessageOps.WarnUnsupportedAttachments` 의 `if isNull (box msg.Attachments)` null guard 가 m11 `Create` factory 정규화 후 dead path. C# record initializer 직접 생성 가능성을 방어층으로 유지 — 의도된 결정
- 5 검토항목 모두 정합 확인:
  - C1 OpenAI 두 단계 fit = 공식 high detail 알고리즘 정합 (4096×1000 → 765 토큰)
  - M4 호출 위치 = ClaudeCli + CodexCli + ApiChatProvider (4 provider 공통) 5종 전수 커버
  - M3 UTF-8 replacement = `Encoding.UTF8` default `ReplacementFallback("�")` 동작 정합
  - m1 F# literal → C# const initializer 통과 (compile-time literal field IL emit)
  - M5 contains list 누락 critical 없음 (`.bat`/`.cmd`/`.ps1` 는 textExtensions 통과 + 실행 안 됨)

### 의도적 미적용 (별도 follow-up todo)

- **m3**: `Image of name * bytes * mime` → `Image of name * bytes * format: ImageFormat` 일원화 + `mimeOf` helper. 광범위 영향 (5종 provider wire 진입 + DriftTests + AttachmentClassifier) — 별도 PR
- **m5**: `ApiChatProvider.cs:145,178-180` streaming `collected.Add(update)` 누적 메모리 — 본 PR 범위 외 follow-up
- **m6**: `_history` unbounded — multi-turn OOM. todo 정책 추가 권고
- **m9**: BOM 4중 elif → table-driven. 룰 5번째 추가 시 trigger

## Phase 3a — commit-5 (rev 10, 2026-05-09)

### 산출물 (수정 4)

- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml`
  - input TextBox 에 `x:Name="InputBox"` 부여 (DataObject.AddPastingHandler hook 대상)
  - root Grid 를 outer `<Border x:Name="DragHighlightBorder">` (BorderThickness=2 / CornerRadius=3 / Padding=6) 로 감쌈 — drag-over visual cue (정책 commit-5)
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml.cs`
  - constructor 에서 `DataObject.AddPastingHandler(InputBox, OnInputPaste)` 등록 (정책 2 정석)
  - `OnInputPaste` 우선순위 (정책 3.3절): file drop > image > text. text-only 는 `e.CancelCommand()` 미호출 → 기존 TextBox paste 회귀 (MI-3)
  - `TryExtractPngBytes` helper — CF_PNG raw 우선 (`MemoryStream.ToArray()` 재인코딩 회피) / fallback `BitmapSource` → `Clone()+Freeze()` → `PngBitmapEncoder` (큰 이미지 background marshal 은 commit-6 이월 — m1)
  - `Panel_PreviewDragEnter` / `PreviewDragOver` 에서 FileDrop 일 때 `DragHighlightBorder.BorderBrush = AccentBrush` (`TryFindResource` fallback `DodgerBlue`). Leave/Drop 시 Transparent. 자가 검열 M1 적용 — Enter/Over 짝
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs`
  - `ReevaluateAttachmentsForProvider()` 추가 — `_provider.Capabilities` 와 chip `Source.IsImage`/`IsPdf`/`IsTextFile` 분기로 미지원 chip 강제 제거 + notice (정책 9 / 3.4). image format 세분 비교는 단순화 (commit-6 m2)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs`
  - `ConfigureProviderAsync` 의 IsValid=true 분기에서 `IsReady = true` 직후 `ReevaluateAttachmentsForProvider()` 호출

### 검증

- `dotnet build Apps/Promaker/Promaker.sln` 통과 (오류 0 / 경고 2 OllamaSharp 사전 환경)
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests --no-build` = **205건 전수 통과** (commit-5 추가 fixture 없음 — UI 통합 검증은 수동)

### 자가 검열 (Agent 위임 — Critical 0 / Major 1 / Minor 3)

- **M1 적용**: `Panel_PreviewDragOver` 에 highlight 토글 추가 — DragEnter/Over 짝 맞춰 FileDrop 데이터로 swap 되는 edge case 에서 highlight 누락 방지
- **m1 이월** (commit-6): 큰 BitmapSource fallback 경로의 PNG 인코딩이 STA paste handler 안 동기 수행 — 4K 스크린샷 등에서 paste freeze 가능. CF_PNG 우선 경로가 대다수 흡수해 실측 영향 작음. background marshal 로 옮길 예정
- **m2 이월** (commit-6): `ReevaluateAttachmentsForProvider` 의 image format 별 세분 capability 비교 — 현재 모든 ImageFormats 일괄 (`!caps.ImageFormats.IsEmpty`). 4종 provider 모두 4 format 일괄 지원/미지원이라 실질 회귀 없음
- **m3 영구 skip**: `DataContext` 변경 시 paste handler 재등록 — `MainViewModel.LlmChatVm` lazy 단일 인스턴스 패턴상 불필요

### 미처리 (commit-6 으로)

- race-free SendAsync (snapshot + provider 캡처) + `Attachments.Clear()` 송신 후
- `CanSend` 갱신 (첨부-only 허용)
- default prompt prefix
- ImportPlan label fallback
- F# `Attachment.toInlineString` helper (정책 15 fenced wrapper)
- `ApiChatProvider` history summary (정책 17)
- `StatusText` 진행 표시 + HTTP 413 매핑
- `LlmUserMessageOps.WarnUnsupportedAttachments` warn-only → strict invalidArg 모드 전환
- commit-4 의 SendAsync system turn 안내 ("commit-6 wire 미구현 placeholder") 제거
- CLI provider 첨부 wire 본 구현 (Claude `--input-format stream-json` JSON Lines / Codex `-i/--image` path + bytes 임시 spool)
- commit-5 자가 검열 m1 (background 인코딩) + m2 (format 세분 비교)
