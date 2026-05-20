# LLM Chat — 이미지 인라인 렌더링 도입 (todo)

## 작업 목표
LLM 응답에 포함된 이미지를 `LlmChatPanel` 안에서 **바로 렌더**한다.
모델: 웹의 `<img src="...">` 와 동일 — **LLM 은 image source reference (link) 만 응답에 포함**, Promaker 가 그 link 를 resolve 해서 실제 이미지 byte 를 가져와 렌더. base64 inline 안티패턴은 회피 (token 폭증, response size 폭증).

## 배경 / 맥락

### 현재 LLM branch 상태 (이미 끝난 것)
`done-llm-markdown.md` — MdXaml `MarkdownScrollViewer` 도입 (A1 정책 = role 기반, assistant 전체 = 단일 Markdown segment) 완료. commit `b56d531`. 본 이미지 작업은 그 위에 얹는 후속.

### 병행 작업 — `F:/Git/ds2/light-house` branch (추후 본 branch 와 merge 예정)
- LightHouse KB: 외부 사양 문서 (.pdf/.docx/.pptx/.xlsx/.txt/.md) 를 로컬 FTS5 + SQLite 로 인덱싱 → MCP `attachment_*` tool 4종 (list/outline/search/read) 으로 LLM 이 검색·인용
- 이미지 저장: `.lighthouse-kb/blobs/images/<sha256>.<ext>` per-collection (object storage 아님, data URI 도 아님)
- caption: VLM (Anthropic Sonnet 4.6 등) eager-at-indexing → `ImageCache.CaptionText`
- 현재 단계: Phase 1 lib 종결 (commit `9736237`, **99 Fact**) + Phase S0~S5e 종결, Phase S6 (= Phase 2 이미지 인프라) 진행 중
- 핵심 invariant: **"chat image drop ≠ KB ingest" — 두 경로 완전 분리 SSOT** (`todo-lighthouse-kb-index.md` §3.0, 본문 L195~L202. 참조 위치 L66, L468)
- KB → LLM 으로 가는 이미지는 MCP `ImageContentBlock` (base64 inline) — vision API 입력 (server.md §결정 D-2-7, L1084). chat panel 직접 렌더 경로 아님.
- 사용자에게 KB 이미지 표시는 별도 attachment panel / citation 클릭 UX 로 분리 (index.md L520~523, "WPF UI, LLM 무관")
- Phase S7 후보 endpoint: `GET /collections/{id}/files/{fileId}/thumbnail`, `page/{n}.png` (현재 명시적 wiring 없음)

## 확정 사항 (이 대화에서)

### 시나리오 A 와 B 의 정의
**A. KB 검색 결과의 이미지를 사용자가 별도 panel 에서 확인**
- 트리거: UI (KB hit) 자동 첨부
- 위치: chat 본문 *밖* (별도 attachment panel)
- LLM 응답에 image markdown 없음
- light-house invariant 와 부합

**B. LLM 이 응답 본문에서 이미지를 markdown 으로 인용 (= 본 작업의 1차 타겟)**
- 트리거: LLM 이 응답에 `![](lighthouse://...)` 작성
- 위치: chat bubble 본문 안 (MarkdownScrollViewer inline)
- LLM 이 인용할 image reference 를 *알아야* 함 → light-house tool 응답에 image id 동봉 필요

### 채택된 모델
- LLM 응답 = 텍스트 + image **link 만** (`![](lighthouse://...)` 같은 짧은 reference)
- Promaker (MdXaml) = resolver 로 link → 실제 이미지 byte fetch + 렌더
- base64 inline 데이터 URI 의 LLM 응답 본문 포함은 **금지** (token 폭증, prompt injection 회피)
- **책임 분리 명문화**: MCP `ImageContentBlock` (base64) 경로는 **LLM vision API 입력 전용** (light-house attachment_read → LLM 모델). chat panel resolver 경로는 그와 무관 — chat panel 은 LLM 응답 본문에 들어온 link reference 만 처리한다. 동일 이미지 byte 가 두 채널 모두 통과하지 않도록 LLM 시스템 프롬프트에 "scheme URI 만 본문 인용, base64 data URI 본문 inline 금지" 명시.

## 미확정 결정 사항

### D1. A 와 B 의 도입 범위
사용자 답: A, B 둘 다 좋아 보임 / B 가 더 자연스러움.
구체 선택:
- (a) B 만 도입 — 검색 다중 결과도 LLM 이 본문에 골라 인용
- (b) A + B 둘 다 — 검색 다중 thumbnail panel + LLM 핵심 인용 inline (역할 분담 합의 필요)
→ **본 todo 의 1차 타겟은 B**. A 는 light-house 측 후속 작업 (`todo-lighthouse-kb-*` 또는 별 todo) 으로 분리하고, 본 todo 의 Phase 2 옵션 목록에는 "역할 경계 합의 후 도입 검토" 형태로만 남긴다.

### D2. Image reference scheme
- (a) `lighthouse://collection/<col>/image/<sha256>` 가상 scheme — App 내 resolver 가 로컬 blob 파일 경로로 매핑. 단순, offline.
- (b) HTTPS thumbnail endpoint (`https://localhost:<port>/collections/.../page/N.png`) — Phase S7 결과물 활용. 인증 헤더 처리 부담. **이득: remote / 멀티 호스트 KB / Promaker 외부 클라이언트도 동일 URL 접근 가능**.
- (c) 둘 다 지원
**Phase 0b 진입 전 lock-in 필수** (Phase 0a 는 scheme 결정 없이도 진입 가능 — 아래 Phase 분리 참조). 잠정 default = (a) 가상 scheme, Phase S7 진행 후 (c) 로 확장.

#### D2 sub-spec (lock-in 시 동시 결정)
- collection 식별: GUID vs alias 둘 중 alias 가 user-friendly. resolver 에서 alias → GUID 매핑 검증 필요.
- resolution 실패 정책: alt text + 경고 메시지 표시. raw URL 노출 금지 (보안).
- query string 허용 여부: **불허**. `?v=2` 류 cache-bust 금지 (resolver 가 sha256 기반 = content-addressed).
- cross-collection lookup: **불허**. 응답 시점 활성 collection 만 resolve.
- collection unmount 시 fallback: alt text + "unmounted collection" 안내.

### D3. 작업 타이밍
- (a) 본 llm branch 에서 image rendering 기반 (KB-agnostic, scheme 정의만 예약) 까지만 먼저 → light-house merge 후 wiring
- (b) light-house merge 끝나길 기다렸다가 한 번에
→ **권장 (a)**. light-house Phase S6 진행 중이므로 충돌·이중작업 최소화.

### D4. LLM 이 image reference 를 받는 wiring 방식 (light-house 측 협의 필요)
현재 `attachment_read(...)` → MCP `ImageContentBlock` (base64) 로만 가서 LLM 은 이미지를 *보지만* reference (sha256/id/URL) 를 *모름*. 해결 후보:
- (a) MCP tool 결과의 text block 에 image id 동봉 — 예: text block `"[image: lighthouse://col1/img/abc123]"` + image block (base64)
- (b) `attachment_search` 결과 JSON 에 `images: [{ref, caption}, ...]` 메타 노출
- (c) 시스템 프롬프트에 scheme 규약 명시 (a/b 와 병행)
→ 미확정. light-house 측 `attachment_*` tool 명세 보강 필요. **Phase 1 진입 전까지 lock-in**. Phase 0 진입은 D4 미확정 상태로도 가능 (Phase 0 는 KB-agnostic).

### D5. lighthouse:// resolver 의 구현 위치 (신설)
- (a) MdXaml resolver hook 활용 — WPF MdXaml 본체 hook 지원 여부 **미확정**. context7 에 MdXaml WPF 자료 없음 (Markdown.Avalonia 포트만 등록). Phase 0 step 1 의 산출물로 직접 확인 필요. 부재 시 fork 부담.
- (b) **render-time 전처리** (`LlmChatViewModel` 의 markdown 직전 단계에서 `lighthouse://` → 내부 임시 scheme 또는 file 경로 치환, MdXaml 에는 표준 scheme 만 전달) — **권장 default**. 사유: (1) hook 부재 가정 안전, (2) A1 정책 (single Markdown segment) 유지, (3) `ChatTurn.Text` raw 보존 invariant 와 일관.
- (c) Option 3 (segment 분리) — 보안·인터랙션 강하게 필요할 때만.

**(b) 채택 시 invariant**: 변환된 텍스트는 `ChatSegment` lifetime 안에서만 존재. `ChatTurn.Text` 로 write-back 금지. CopyAll / Export round-trip 단위 테스트로 박제.

**(b) 채택 시 내부 scheme**: `file://` 직접 사용 금지 (아래 C-C 의 file:// allowlist 정책과 충돌). 대안 = 내부 전용 scheme `promaker-image://<token>` + resolver 가 메모리 내 매핑 보유. 또는 application data 한정 cache 경로 사용.

## 옵션 비교표 (Option 1/2/3)

| 항목 | Option 1 (MdXaml 기본) | Option 2 (resolver hook) | Option 3 (segment 분리) |
|---|---|---|---|
| 구현 위치 | MdXaml `![]()` 그대로 활용 | MdXaml hook API | `TextToSegmentsConverter` 가 image markdown 분리, `Image` 컨트롤 직접 |
| A1 정책 (single segment) | 유지 | 유지 | **깨짐** |
| MdXaml fork 부담 | 없음 | hook 부재 시 fork PR 필요 (1인 유지보수 risk) | 없음 |
| Resolver 책임 | render 시점 MdXaml 내부 | MdXaml hook 콜백 | converter 가 markdown 일부 파싱 (drift risk) |
| 보안 통제 | 약 (data:/https/file scheme 직접 노출) | 강 (hook 에서 통제) | 강 (segment 분리 단계에서 통제) |
| streaming 미리보기 | 불가 (완료 후만) | 불가 | 가능 (URL 만 도착해도) |
| 인터랙션 (클릭→viewer, 컨텍스트 메뉴) | 약 | 약 | 강 |
| Drift / 회귀 risk | 낮음 | hook 사양 변경 시 | 과거 A2 (fence-split) 회귀 사유 (LLM 부분 채택) 와 유사 — converter 가 markdown 자체 파싱 → MdXaml 과 두 번 파싱 + drift 위험 |

**진입 default**: Option 1 (Phase 0a) → render-time 전처리 (Phase 1, D5(b)) → Option 3 은 *trigger* 발생 시만 (아래)

**Option 3 진입 trigger (객관 기준, 1건 이상 충족 시 검토)**:
- 우클릭 메뉴 / 클릭→원본 viewer / 컨텍스트 메뉴 등 인라인 인터랙션 요구 발생
- 보안 incident (현 정책으로 막을 수 없는 위협 사례)
- MdXaml hook 부재 *확정* + render-time 전처리로도 통제 불가능한 케이스 발생

## 보안 정책 (모든 Phase 공통 SSOT)

### 화이트리스트 단일 SSOT 함수
화이트리스트 / 검증 / canonicalize 를 단일 pure 함수로 추출 (단위 테스트 가능). `IChatImageResolver` 인터페이스 진입점에서 일괄 enforce.

### Scheme 허용 정책
- **허용**:
  - `https://` — 단, host = `localhost` / `127.0.0.1` / `::1` / **본 KB 서비스가 사용하는 port 만**. RFC1918 사설망 (10/8, 172.16/12, 192.168/16) + 169.254/16 link-local + UNC 차단 (SSRF / tracking pixel 방어)
  - `lighthouse://` (Phase 0b 부터 예약, Phase 1 부터 resolver 활성)
  - (선택) `promaker-image://` (D5(b) 채택 시 내부 전용)
- **차단**:
  - `data:` — **본 todo 의 "base64 inline 금지" 정책과 충돌하므로 LLM 응답 본문 발생 시 strip**. CopyAll/Export 시점도 strip 정책 동일 적용.
  - `file://` — **default 차단**. UNC `\\attacker.com\share` 자동 NTLM 인증 / junction symlink 우회 / WebDAV outbound 위험. 부득이 허용 시 `%LOCALAPPDATA%/Promaker/cache/images/` 같은 application data 한정 + UNC/ReparsePoint reject.
  - `javascript:`, `vbscript:`, 기타 모든 scheme — Uri.Scheme lowercase string equality 로 정확 매칭 (정규식 prefix 매칭 금지)

### lighthouse:// resolver strict 검증
1. **정규식 strict match**: `^lighthouse://collection/(?<col>[A-Za-z0-9_\-]{1,64})/image/(?<sha>[a-f0-9]{64})$`
2. `<col>` 화이트리스트 lookup — 활성 mount collection 만
3. 최종 매핑 파일 경로 = `Path.GetFullPath(...)` 정규화 후 **collection root prefix 강제** (Windows case-insensitive). `..` traversal, junction, symlink 모두 reject.
4. ImageCache 테이블 조회만 허용. blob 디렉토리 glob 옵션은 **제거** (TOCTOU race / 미인덱싱 파일 노출 회피).

### 사이즈 / 메모리 한도 (chat panel 측 자체 enforcement)
light-house 서버 SSOT 와 정합:
- 단일 이미지: byte ≤ 5MB, decode pixel ≤ 8MP
- chat bubble 내 응답: ≤ 5장 (초과 분은 alt text 만 표시 + 경고 footer)
- timeout: HTTP fetch ≤ 5초 (custom HttpClient)
- redirect: 비허용 (`AllowAutoRedirect=false`)
- Content-Type 강제 검증 (선언과 실제 매직 바이트 비교, image/png|jpeg|webp 만)

### 실패 시 동작
fetch / decode / 검증 실패 → **alt text + 경고 글리프**만 표시. raw URL / 실패 원인 / file path 노출 금지.

## merge blocker 체크리스트 (Phase 1 commit 전 강제 확인)

본 todo 의 `B` 시나리오 도입은 light-house §3.0 invariant ("KB image ↛ chat 자동 inline") 의 명시적 예외 조항이 필요하다. 다음 모두 충족 전에 Phase 1 commit / push 금지:

- [ ] light-house 측 `todo-lighthouse-kb-index.md` §3.0 (L195~) 에 예외 조항 patch 반영 commit hash 확보
- [ ] 예외 조항 문구 합의 (예시 patch 안 아래)
- [ ] light-house 측 `attachment_*` tool 응답 image reference 메타 동봉 spec 확정 (D4 lock-in)
- [ ] D2 scheme 사양 lock-in (가상 scheme / HTTPS endpoint / 둘 다)

### §3.0 예외 조항 patch 안 (light-house 측 변경 제안)
```
3.0 두 경로 완전 분리 SSOT (가장 상위 invariant)
...
[기존] KB 의 image 가 chat 에 자동 inline 되지도 않음.
[추가] 단, LLM 이 응답 본문에서 명시적으로 `lighthouse://` scheme reference 를 인용한 경우에 한해
       Promaker chat panel 의 inline 렌더 허용. KB 측은 reference 노출 책임(attachment_* tool
       응답에 image id 동봉), UI 측은 resolver 및 렌더 책임. base64 ContentBlock 경로(vision API
       입력)와 inline 렌더 경로(사용자 표시)의 byte 중복은 LLM 시스템 프롬프트로 회피.
```

## 남은 할 일

### Phase 0a — decision-free (D2/D4/D5 미확정 상태로도 진입 가능)

1. **MdXaml 1.27.0 이미지 지원 동작 검증** (산출물 = D5 input)
   - context7 확인 결과: MdXaml WPF 자체 hook 자료 없음 (Markdown.Avalonia 포트만 등록). MdXaml WPF 소스 직접 확인 필요.
   - 실측: `![](https://...)`, `![](file:///...)`, `![](data:image/png;base64,...)` 각각의 동작 확인
   - 비동기 다운로드 여부, 캐시 동작, fetch 실패 시 fallback 동작
   - **산출물**: hook API 유무 / image source resolution 개입 지점 → D5(a) 가능 여부 결론
2. **이미지 implicit style 추가**
   - `Promaker/Themes/Theme.Controls.Llm.xaml` 에 `Image` implicit style
   - `MaxWidth=600`, `Stretch=Uniform`, margin, 비율 유지
   - 다크/라이트 테마 transparent PNG 가독성 확인 (done-llm-markdown.md 의 dist 전 시각 검증과 통합)
3. **streaming / freeze 임계값 측정 + Plain fallback 정책 박제**
   - `IsStreaming=true` 동안 Plain segment → 이미지 안 보임 (기본 동작 OK)
   - 임계값 제안: text > 50KB **또는** image 개수 > 3 → MdXaml 파싱 freeze 우려, Plain fallback 검토
   - 실제 사용자 응답으로 임계값 보정
4. **CopyAll / Export round-trip 검증**
   - `ChatTurn.Text` raw 보존: image markdown 원문 그대로 직렬화 (현 `BuildMarkdownTranscript` 정책 유지)
   - render-time 변환 (D5(b)) 은 별도 — 단위 테스트로 round-trip 박제
5. **`IChatImageResolver` 추상화 신설**
   - Phase 0a = `NoopResolver` (입력 URL 그대로 반환)
   - Phase 1 = `LighthouseResolver`
   - Phase 진입 decision-free 화의 핵심 — D5 결정 연기 가능
   - 단일 책임: 입력 URL → 검증 + 화이트리스트 적용 + 최종 렌더 URL/경로 + alt fallback. 단위 테스트 대상.
6. **화이트리스트 / strict validator 단일 SSOT 함수 추출 + 단위 테스트**
   - resolver 인터페이스 진입점에서 strict 정규식 + size + mime + path canonicalize 일괄 enforce
   - `data:` strip / `file://` default 차단 / `https://` host 제한 / `lighthouse://` 정규식 — 단일 함수에서

### Phase 0b — D2 lock-in 후 진입

1. **scheme 화이트리스트 활성** (위 보안 정책 그대로 enforce)
2. **D5 결정 박제** (기본 = render-time 전처리 b, MdXaml hook 사용 가능 확정 시만 a)
3. **Hyperlink URL 보안 정책 동시 도입 (또는 별 PR)**
   - done-llm-markdown.md 의 보류 사항 (Hyperlink 보안) 과 IMG scheme 정책을 동일 화이트리스트로 묶기
   - URL/IMG 동시 도입 권장: 본 PR 에서 처음 도입되는 화이트리스트 SSOT 의 비대칭 회피
   - `RequestNavigate` handler 도 본 PR 또는 직전 별 PR 에서 도입

### Phase 1 — light-house merge 직후 (merge blocker 체크리스트 통과 후)

1. **`lighthouse://` scheme resolver 구현** (`LighthouseResolver`)
   - 매핑: `lighthouse://collection/<col>/image/<sha256>` → 로컬 `<col-path>/.lighthouse-kb/blobs/images/<sha256>.<ext>` 파일 경로
   - 구현 위치: D5 결정에 따라 (a) MdXaml hook 또는 (b) render-time 전처리
   - **light-house blob layout 직접 hard-code 금지** — light-house 측이 노출하는 SSOT API 또는 `LightHouseBlobResolver` 의존성 역전 형태로 호출 (Phase S6 진행 중이라 layout 변경 risk)
   - extension 결정: `ImageCache` 테이블 조회만. blob glob 금지.
   - **변환 후 텍스트 write-back 금지** (D5(b) invariant) — CopyAll/Export round-trip 단위 테스트로 박제
2. **streaming 종료 → image fetch race 방지**
   - streaming cancel: `CancellationToken` 으로 in-flight HTTP fetch 취소
   - 응답 종료 시 image fetch burst 회피: latch + debounce
   - partial `![](lighthouse://col/img/ab` (sha256 미완) 처리: streaming 중에는 image segment 렌더 안 함 (현 Plain segment 정책 그대로). 종료 후 turn-local URL dedup
3. **light-house `attachment_*` tool 응답에 image reference 메타 동봉** (D4 합의 후, light-house 측 PR)
4. **시스템 프롬프트 가이드 추가**
   - "도면/이미지 인용 시 `lighthouse://collection/<col>/image/<sha256>` 형식 사용"
   - "alt text = LLM 책임, caption 약어 ≤ 30자 권장"
   - "본문 인라인 이미지는 시각적으로 핵심인 1~2장으로 제한, 다중 검색결과는 panel 사용 권장" (D1 = B 1차 + 향후 A 도입 대비)
   - "MCP `ImageContentBlock` 으로 본 이미지를 본문에 또 `lighthouse://` 로 인용해도 OK — chat panel 측이 byte 한번만 fetch, dedup 처리. 단 base64 data URI 본문 inline 은 금지"

### Phase 2 — 선택 (본격 통제 / 다른 시나리오 필요 시)

1. **시나리오 A (attachment panel) 도입 — light-house 측 후속 작업으로 이관**
   - 본 todo 의 1차 타겟은 B. A 는 별 todo (light-house 측 또는 신규) 에서 추진.
   - A 도입 시 역할 경계 합의 필요:
     - 옵션 (b1) chat markdown 은 LLM 이 본문 인용한 핵심 이미지만 inline, 검색결과 다중 이미지는 panel
     - 옵션 (b2) chat markdown 은 인라인 안 함, 전부 panel (light-house invariant 와 가장 일관 — §3.0 예외 조항 없이 가능)
2. **Option 3 (SegmentKind.Image 분리) 검토** — Option 3 진입 trigger 충족 시만
3. **Phase S7 thumbnail endpoint 활용**
   - HTTPS localhost endpoint URL 인용 지원 — D2(c) 채택 시 본격화
   - custom HttpClient + 인증 헤더 필요 (MdXaml 기본 fetch 로 안 될 가능성 높음 → render-time 전처리 또는 Option 3 측 책임)

### 보강 / 부수 작업

- **Export 옵션**: `lighthouse://` 가 호스트 머신 의존 → Export 결과 외부에서 dead link. 옵션 "이미지 포함 (blob 동봉 zip)" vs "reference-only" 분리 또는 한계 명시.
- **accessibility / NVDA**: 인라인 이미지의 스크린리더 읽힘 1회 점검 (alt text 역할 확인).
- **Light/Dark 시각 검증**: done-llm-markdown.md 의 dist 전 시각 검증 5건과 통합 진행.

## 관련 파일 / 경로

### 현재 llm branch
- `Promaker/Controls/Llm/LlmChatPanel.xaml` — DataTemplate / Resources / Style (이미지 작업 진입점)
- `Promaker/Themes/Theme.Controls.Llm.xaml` — Markdown style 사전 (Image implicit style 추가 대상)
- `Promaker/Presentation/TextToSegmentsConverter.cs` — 현재 length=1 segment 반환. Option 3 으로 가면 image segment 분리 진입점.
- `Promaker/Presentation/ChatSegmentTemplateSelector.cs` — Option 3 시 새 template selector 분기
- `Promaker/ViewModels/ChatSegment.cs` — Option 3 시 `SegmentKind.Image` 추가 진입점
- `Promaker/ViewModels/LlmChatViewModel.cs` — D5(b) render-time 전처리 진입점
- `Directory.Packages.props` / `Promaker/Promaker.csproj` — MdXaml 1.27.0 이미 도입
- (신규 예정) `Promaker/Presentation/IChatImageResolver.cs` + `NoopResolver` / `LighthouseResolver`
- (신규 예정) `Promaker/Presentation/ChatImageWhitelist.cs` — 화이트리스트 단일 SSOT 함수 + 단위 테스트 대상

### light-house branch (참조용, 본 branch merge 대상)
- `F:\Git\ds2\light-house\Apps\Promaker\Docs\todo-lighthouse-kb-index.md` (**926 line**, r13)
  - 두 경로 분리 invariant: **§3.0 본문 L195~L202** (정의 SSOT). 참조 위치 = L66 (핵심결정 리스트), L468 (재언급)
  - 이미지 schema (ImageCache / ImageReferences / Chunks.ImageCount) 본문: L294 부근 + L455~L472 (재정의 ↔ 참조 구별 필요)
  - UI/LLM 무관 분리: L520~L523
  - VLM caption eager-at-indexing 본문: L840~L846 부근 (참조 위치)
- `F:\Git\ds2\light-house\Apps\Promaker\Docs\todo-lighthouse-kb-server.md` (**1220 line**, s6-r22 또는 s6-r24)
  - 이미지 응답 포맷 결정 **D-2-3 (L1077)** / **D-2-7 (L1084)** — base64 ContentBlock 강제, file path / presigned URI 탈락
  - 사이즈 한도: 단일 ≤ 5MB / 응답 ≤ 5장 / 초과 `caption_only` 강등 (L1077, L834 부근)
  - VLM caption eager-at-indexing (L840~L846 부근)
  - Phase S7 후보 thumbnail/page endpoint: L370, L566~L567

## 주의 사항 (유지보수 / merge 시점)

### light-house invariant 와의 일관성
- 현 light-house §3.0: "chat 자동 inline 금지" 가 invariant. 본 작업 (시나리오 B = LLM 명시 인용 inline) 도입 시 **light-house todo §3.0 예외 조항 patch 동시 필요** (위 patch 안 참조).
- merge blocker 체크리스트 통과 전에는 Phase 1 commit 금지.

### double-rendering 회피
- A + B 동시 도입 시: 동일 KB 이미지가 chat inline + attachment panel 양쪽에 노출되는 UX 혼란. merge 전에 dedup 정책 또는 역할 분담 합의 필요.
- B 단독에서도 token 이중부담 risk: LLM 이 MCP `ImageContentBlock` 으로 본 이미지를 본문에 또 `lighthouse://` 로 인용하면 byte 가 두 경로 통과. → 시스템 프롬프트 규약 "본문 인용은 scheme URI 만, base64 data URI 본문 inline 금지" 로 완화. chat panel resolver 는 byte 한 번만 fetch + turn-local dedup.

### `ChatTurn.Text` raw 보존
- `done-llm-markdown.md` 의 정책 유지. `BuildMarkdownTranscript` 는 `t.Text` 그대로 직렬화. scheme 변환 (`lighthouse://` → render-time URL) 은 ChatSegment lifetime 안에서만, 저장은 raw.
- CopyAll / Export round-trip 시 `lighthouse://` 원문 유지되어야 다른 환경에서 재해석 가능 (단위 테스트로 박제).

### Hyperlink 보안 정책과 묶기
- done-llm-markdown.md 의 보류 사항 "Hyperlink 보안" (`file://` / `javascript:` 등) 과 동일 화이트리스트 SSOT 함수 공유. URL 과 IMG 공통 처리.
- 본 PR 에서 처음 도입되는 화이트리스트 SSOT 의 비대칭 회피 위해, Hyperlink RequestNavigate handler 도 본 PR 또는 직전 별 PR 에서 도입.

### A1 정책 유지 우선
- Option 1 (MdXaml 기본) 또는 D5(b) render-time 전처리 까지는 A1 (single Markdown segment) 유지.
- Option 3 (segment 분리) 은 위 *Option 3 진입 trigger* 충족 시만 — converter 가 markdown 자체 파싱 → MdXaml 과 두 번 파싱 + drift 위험 (과거 A2 fence-split 회귀 사유와 유사한 함정).

### MdXaml 1인 유지보수 (whistyun) 버스 팩터
- `done-llm-markdown.md` 와 동일 risk. context7 에 WPF MdXaml hook 자료 없음 (Avalonia 포트만 등록) → hook 미지원 가능성 — D5(b) render-time 전처리가 default 인 사유.
- 장기적으로 WebView2 fallback 으로의 전환 가능성 유지.

## 이력

- 2026-05-19: 최초 작성 (todo-llm-imageview.md)
- 2026-05-19: 5 reviewer 종합 리뷰 반영 — 외부 인용 라인 번호 정정 (light-house server.md D-2-3 L1077 / D-2-7 L1084, server.md 1220 line, index.md 926 line, commit `9736237` 의 99 Fact), Option 비교표 신설, D5 신설 (resolver 구현 위치, default = render-time 전처리), Phase 0a/0b 분리, 보안 정책 SSOT 단원 신설 (data: strip / file:// default 차단 / lighthouse:// strict 정규식 / SSRF 방어), merge blocker 체크리스트 + light-house §3.0 예외 조항 patch 안, `IChatImageResolver` 추상화 신설, streaming cancel race 처리, MdXaml WPF hook 미확정 (context7 결과 Avalonia 포트 한정) 명시.
