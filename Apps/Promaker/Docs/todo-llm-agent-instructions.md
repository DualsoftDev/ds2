# LLM Agent 선택형 작업 지침 도입 TODO

## 목표

Promaker LLM Agent 에 선택 가능한 작업 지침(Instruction set)을 도입한다.
기존 KB digest 는 색인 자료 기반 참고 지식이고, 본 작업 지침은 LLM 의 행동 방식과 기능 수행 규칙을 바꾸는 instruction tier 로 분리한다.

## 현재 결정된 방향

- 사용자-facing 용어는 `skill` 대신 **작업 지침**을 사용한다.
- 내부 구현명은 `InstructionSet` 또는 `InstructionPack`, 폴더명은 `Instructions` 계열을 우선 검토한다.
- `prompt`는 system/user/operator prompt 등 기존 의미가 넓어 사용자-facing 명칭으로 피한다.
- 작업 지침은 참고자료가 아니라 명령성 instruction 이므로, 기존 user prompt dir 의 `DATA, not instructions` tier 아래에 섞지 않는다.
- built-in 작업 지침도 강제 주입이 아니라 선택 가능한 항목으로 취급한다.
- built-in 은 `defaultEnabled` 같은 기본 선택값을 가지며, 사용자는 켜거나 끌 수 있다.
- 사용자 선택 변경은 기존 LLM 세션에서 제거하려 하지 않고, **LLM 재시작 또는 provider 재생성 후 적용**한다.
- 선택 해제된 작업 지침을 이미 진행 중인 LLM 세션에서 완전히 제거하는 것은 불가능하다고 본다.
- `*.mdx` 파일은 의도적으로 system prompt 주입에서 제외하기 위해 확장자를 변경한 것이므로 본 작업 범위에서 신경 쓰지 않는다.
- 기존 always-on prompt 를 선택형 instruction 으로 이관할 때는 중복 주입을 금지한다. 특히 현재 `yaml.md` 는 `Promaker.csproj` 의 `LlmAgent\Prompts\*.md` 규칙으로 항상 주입되므로, `promaker-yaml` instruction 을 만들 경우 먼저 이관 범위를 확정해야 한다.

## 권장 디렉터리 구조

Built-in:

```text
Apps/Promaker/Promaker/LlmAgent/Instructions/
  promaker-yaml/
    instruction.json
    INSTRUCTION.md
  iolist-modeling/
    instruction.json
    INSTRUCTION.md
```

사용자 정의:

```text
%APPDATA%/Dualsoft/Promaker/Instructions/
  my-custom-instruction/
    instruction.json
    INSTRUCTION.md
```

운영자/배포 단위 확장을 허용한다면 후보:

```text
<exeDir>/Instructions/<id>/
```

`custom`은 사용자 정의 작업 지침 tier 를 뜻한다. 선택 상태 저장 key 에서는 `custom:<id>` prefix 를 사용한다.

operator tier 는 v1 기본 범위에서 비활성화한다. 추후 허용할 경우에도 신뢰 판정(관리자 배포/서명/ACL)이 통과한 항목만 trusted-operator 로 주입하고, 신뢰 판정이 불가능한 operator instruction 은 주입하지 않는다. untrusted operator 를 custom 처럼 명시 활성화할지는 별도 phase 에서 다시 결정한다.

## Manifest 초안

```json
{
  "id": "promaker-yaml",
  "displayName": "Promaker YAML 생성",
  "description": "공정 설명을 promaker/v0 YAML로 작성하도록 지시합니다.",
  "entry": "INSTRUCTION.md",
  "defaultEnabled": true,
  "order": 100
}
```

초기 정책:

- `id`는 manifest 값이며 source-local stable id 이다. 사용자 설정의 stable key 는 `source:<id>` 형태의 source-qualified key 이다. 폴더명은 저장 위치일 뿐 SSOT key 가 아니다.
- `entry`는 해당 instruction 폴더 기준 상대 경로만 허용한다.
- `entry` 검증은 source 별 resolver/validator 가 fail-closed 로 수행한다. filesystem source 는 `..`, 절대 경로, UNC/드라이브 경로, symlink/reparse point 탈출, 비 `.md` 확장자, strict UTF-8 decode 실패, 최대 파일 크기 초과를 차단한다. embedded source 는 resource 존재, manifest id 대조, `.md` entry, strict UTF-8 decode 를 검증한다.
- built-in / operator / custom 의 `id` 충돌은 override 허용 없이 deterministic reject 를 기본안으로 둔다. 충돌한 id 는 fail-closed 로 모두 비활성화하고 warning 한다.
- 동일 source 안 중복 id 도 fail-closed 로 처리한다.
- override 필요성이 생기면 `overrides` 같은 명시 필드를 별도 설계한다.
- 합성 순서는 deterministic 해야 한다. 기본 정렬은 `sourcePriority → order → id(StringComparer.Ordinal)` 로 고정한다.
- 개별 entry size cap 뿐 아니라 선택된 instruction 합산 budget 도 둔다. 합산 초과 시 fail-closed 또는 재시작 전 warning 정책을 정한다.

## 선택 상태 저장안

선택 상태는 manifest 에 쓰지 않고 사용자 설정에 별도 저장한다.

```json
{
  "enabledInstructionIds": [
    "builtin:promaker-yaml",
    "builtin:strict-validation"
  ],
  "disabledInstructionIds": [
    "builtin:legacy-tooling"
  ]
}
```

판정 규칙 후보:

1. 선택 상태 저장 key 는 source-qualified key(`builtin:<id>`, `operator:<id>`, `custom:<id>`) 를 기본안으로 한다. UI 표시와 manifest id 는 단순 id 를 유지한다.
2. 설정 파일 안에서 같은 key 가 `enabledInstructionIds`와 `disabledInstructionIds` 양쪽에 있으면 충돌로 보고 fail-closed(끔) + warning 한다.
3. built-in instruction 은 `disabledInstructionIds`에 있으면 끄고, `enabledInstructionIds`에 있으면 켠다. 둘 다 없으면 `instruction.json`의 `defaultEnabled` 사용.
4. custom instruction 은 명시적으로 `enabledInstructionIds`에 들어간 경우에만 켠다. custom manifest 의 `defaultEnabled`는 무시하거나 warning 처리한다.
5. custom instruction 의 `disabledInstructionIds` 단독 entry 는 의미 없는 stale 설정으로 보고 cleanup 후보 warning 만 남긴다.

이 방식은 새 built-in instruction 추가 시 기본값을 적용하면서도, 사용자가 명시적으로 끈 built-in 이 앱 업데이트로 다시 켜지는 문제를 피한다.

## Prompt 합성 위치

작업 지침은 base system prompt 내부 tier 로 합성한다.

권장 순서:

```text
baseline prompt
→ Promaker 기본 prompt
→ selected built-in instructions
→ selected trusted-operator instructions
→ selected custom instructions
→ operator/user DATA prompt
→ KB keyword digest
→ specialized digest
```

주의:

- Anthropic cache breakpoint 는 현재 `base + KB digest + specialized digest + snapshot` 구조로 4개 cap 을 사용한다.
- selected instructions 를 별도 `TextContent` breakpoint 로 추가하지 말고 base prompt 문자열 안에 합성하는 쪽이 단순하다.
- instruction 선택 변경 시 base prompt cache miss 는 감수한다. 선택 변경은 드문 이벤트로 본다.
- selected instructions 가 0개이면 header/separator 를 포함하지 않아 기존 base prompt 와 byte-identical 이어야 한다(cache 회귀 0). 기존 `PromptLoader` 의 operator/user 빈 tier 처리와 같은 원칙이다.
- effective prompt hash 를 계산해 실제 선택/본문 변경이 없으면 불필요한 provider 재생성을 피한다.
- custom instruction 은 별도 sub-tier 로 둔다. baseline 쪽에는 “후속 작업 지침은 안전/도구사용 규칙을 무효화할 수 없다”는 guard 문장을 추가하는 것을 기본안으로 한다.
- operator instruction 은 v1 에서 기본 비활성이다. 추후 관리자 배포/서명/ACL 로 무결성이 보장되면 trusted-operator 로 built-in 뒤에 둘 수 있다. 신뢰 판정이 불가능한 operator instruction 은 주입하지 않는다.
- effective prompt hash 는 선택 id 뿐 아니라 manifest 주요 필드와 entry 본문 hash 를 포함한다. selection 이 같아도 custom `instruction.json` 또는 `INSTRUCTION.md` 가 바뀌면 재시작 필요 상태가 되어야 한다.
- baseline guard 문장은 selected instructions 가 1개 이상일 때 instruction section header 안에 조건부 삽입한다. selected instructions 가 0개이면 guard/header/separator 모두 생략해 기존 base prompt 와 byte-identical 을 유지한다.

## Built-in packaging 권장안

Built-in instruction 은 앱 배포물의 일부이고 사용자가 임의 수정할 수 없어야 하므로, 기본안은 `EmbeddedResource` 이다. `Content copy` 로 배포하면 파일 discovery 는 단순해지지만, `<exeDir>/Instructions/` 파일이 수정 가능해져 operator tier 와 신뢰 경계가 섞인다.

따라서 `InstructionCatalog`는 다음 두 소스를 같은 추상으로 노출한다.

- built-in: embedded resource 기반 discovery. resource name 에서 instruction id 와 entry 를 복원한다.
- operator/custom: 파일 시스템 기반 discovery. manifest/entry 검증을 fail-closed 로 수행한다.

embedded resource 쪽 id 복원 규칙은 별도 확정이 필요하다. 예: `Promaker.LlmAgent.Instructions.<id>.instruction.json` 과 `Promaker.LlmAgent.Instructions.<id>.INSTRUCTION.md` 형태에서 `<id>` segment 를 manifest id 와 대조한다. filesystem source 의 path traversal/reparse 검증은 embedded source 에 적용하지 않는다.

## 변경 감지 정책

instruction 변경 감지는 선택 상태 변경만으로 충분하지 않다. custom/operator 의 `instruction.json` 또는 entry 본문이 바뀌면 selection 이 같아도 effective prompt 가 달라진다.

초기 구현 후보:

1. 설정 창 진입, 작업 지침 폴더 열기 후 앱 포커스 복귀, 적용/재시작 버튼 클릭 시 catalog 를 재스캔한다.
2. mtime/size 로 빠른 변경 후보를 찾고, 변경 후보에 대해서만 manifest + entry 본문 hash 를 계산한다.
3. 계산된 effective prompt hash 가 현재 provider 의 hash 와 다르면 “LLM 재시작 후 적용” 상태를 표시한다.
4. `FileSystemWatcher` 는 편의 기능으로 후속 검토한다. 기본 정합성은 명시 재스캔 경로로 보장한다.

## 보안 경계

baseline guard 문장은 soft mitigation 이다. LLM 이 custom instruction 의 강한 지시에 흔들릴 가능성을 줄이지만, 완전한 권한 경계는 아니다.

hard boundary 는 provider 별 실행 권한 제한이다. Claude CLI 는 `allowedTools: PromakerToolNames.All` 로 허용 도구를 제한하고, API 계열은 Promaker MCP host 가 노출하는 도구 표면으로 제한된다. Codex CLI 는 별도 tool allowlist 가 아니라 MCP 설정 + CLI 권한 모델에 의존하며, 현재 `danger-full-access` sandbox 를 쓰므로 `cd` 격리 workspace 와 별도 사용자 동의가 완화책이다. custom instruction 은 tool allowlist, MCP host 권한, 파일 시스템 sandbox 같은 실행 권한을 넓힐 수 없어야 한다.

custom instruction 의 lower-authority 는 wire-level 로 강제되는 보안 경계가 아니다. 따라서 acceptance criteria 에 다음을 포함한다.

- UI 에서 custom instruction 본문 preview 와 명시 승인 절차를 제공한다.
- 합성 prompt 안에 source marker(`CUSTOM INSTRUCTION: <id>`)를 넣어 provenance 를 명확히 한다.
- instruction section header 에 “상위 안전/도구사용 규칙 무효화 불가” 문구를 넣는다.
- custom instruction 이 도구 권한을 넓히지 못한다는 점을 UI/문서에 명시한다.

## 적용 lifecycle

선택 변경 시:

1. 설정만 저장한다.
2. UI 에 “LLM 재시작 후 적용” 상태를 표시한다.
3. 현재 provider/session 에서 기존 instruction 제거를 시도하지 않는다.
4. 사용자가 적용하거나 chat/provider 가 재시작될 때 provider 를 재생성한다.
5. provider 생성 시 현재 선택된 instruction set 만 포함해 effective base prompt 를 만든다.
6. 이후 기존 KB digest / specialized digest pending 주입 로직을 다시 적용한다.

단순 `ClearSession()`만으로는 불충분하다. 현재 `Phase1c` 를 받는 모든 provider 는 생성 시점에 `SystemPromptText.Phase1c(PromakerProfile.Instance)` 결과를 1회 전달받고, `ClearSession()`은 session/history 만 비운다. 따라서 instruction 선택 변경 적용은 provider 재생성이 유일하게 일관된 경로다.

Codex 는 `_codexWorkspacePath` 가 이미 있으면 `instructions.md`를 다시 쓰지 않는 lazy 생성 경로가 있으므로, 선택 변경 적용 시 `_codexInstructionsPath` 재작성 또는 workspace 재생성을 명시적으로 수행해야 한다. 다만 provider 공통 정책은 “재생성 후 새 effective prompt”로 맞춘다.

## 관련 현재 구조

- base prompt 진입점: `Apps/Shared/Llm.Shared/SystemPrompt.cs` 의 `SystemPromptText.Phase1c(ILlmAppProfile)`.
- prompt 합성: `Apps/Shared/Llm.Shared/PromptLoader.cs`.
- Promaker profile: `Apps/Promaker/Promaker/LlmAgent/PromakerProfile.cs`.
- provider 생성: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Providers.cs`.
- provider 전환/재생성: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Initialize.cs` 의 `ConfigureProviderAsync`.
- KB keyword digest 적용: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.KbProfile.cs` 의 `ApplyPendingKbDigest`.
- specialized digest 적용: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.SpecializedDigest.cs`.
- API system message 구성: `Apps/Shared/Llm.Shared/Api/SystemContentBuilder.cs`.
- CLI effective prompt 합성: `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` 의 `SystemPromptDigest.compose`.

## 현재 확인한 prompt 주입 사실

MSBuild item 평가 기준:

- `Apps/Shared/Llm.Shared/Llm.Shared.csproj` baseline embedded prompt:
  - `Prompts/baseline/1.attachments.md`
  - `Prompts/baseline/2.knowledge-base.md`
  - `Prompts/baseline/3.environment.md`
- `Apps/Promaker/Promaker/Promaker.csproj` Promaker overlay embedded prompt:
  - `LlmAgent/Prompts/0.domain.md`
  - `LlmAgent/Prompts/yaml.md`

`LlmAgent/Prompts/*.mdx` 는 의도적으로 system prompt 주입 제외 대상이다.

## 기존 always-on prompt 이관 선결 조건

현재 `0.domain.md`와 `yaml.md`는 항상 base prompt 에 포함된다. 선택형 built-in instruction 을 도입하면 다음 중 하나를 먼저 결정해야 한다.

- 권장안: `0.domain.md`는 mandatory base 로 유지하고, `yaml.md`는 `promaker-yaml` built-in instruction(`defaultEnabled=true`) 으로 이관한다. 사용자가 끄면 YAML 생성 지침이 빠진다.
- 대안: `yaml.md`도 mandatory base 로 유지한다. 이 경우 `promaker-yaml` instruction 을 별도로 만들지 않거나, 중복되지 않는 보조 지침만 담아야 한다.

이 선결 조건을 처리하지 않고 `promaker-yaml` instruction 을 추가하면 항상 주입 + 선택 주입이 중복되거나, UI 에서는 껐는데 실제로는 `yaml.md`가 계속 들어가는 해제 불가 상태가 된다.

## 남은 설계/구현 TODO

- [ ] 사용자-facing 용어 최종 확정: “작업 지침” / “LLM 작업 지침” / “사용자 정의 작업 지침”.
- [ ] 내부 타입명 확정: `InstructionSet`, `InstructionPack`, `InstructionCatalog` 등.
- [ ] built-in instruction 폴더 위치 확정: `LlmAgent/Instructions/<id>/`.
- [ ] custom instruction 폴더 위치 확정: `%APPDATA%/Dualsoft/Promaker/Instructions/<id>/`.
- [ ] operator instruction tier 는 v1 기본 비활성으로 둔다. 추후 `<exeDir>/Instructions/<id>/` 를 허용할 경우 관리자 배포/서명/ACL 로 무결성이 보장된 trusted-operator 만 주입한다.
- [ ] `instruction.json` schema 확정.
- [ ] instruction id 충돌 정책 확정. built-in/custom/operator 동일 id, 같은 설정의 enabled/disabled 동시 포함, 삭제된 custom id 가 나중에 built-in 과 재충돌하는 경우를 포함한다.
- [ ] instruction `entry` 검증 정책 확정: source 별 resolver/validator 분리, 상대 경로, `.md`, strict UTF-8, 개별 size cap, 합산 instruction budget, symlink/reparse point 탈출 차단(filesystem source), resource id 대조(embedded source), 실패 시 fail-closed.
- [ ] 선택 상태 저장 파일 위치와 schema 확정.
- [ ] 선택 상태 저장 key 는 source-qualified key(`builtin:<id>`, `operator:<id>`, `custom:<id>`) 기본안으로 확정한다. 삭제된 custom id 가 나중에 built-in 과 같은 id 로 등장해 의미가 바뀌는 stale 설정을 막기 위함이다.
- [ ] `InstructionCatalog`(source discovery), `InstructionSelection`(사용자 설정 resolve), `InstructionPromptComposer`(본문 합성) 책임을 분리한다.
- [ ] custom / operator instruction 변경 감지 정책 확정. 설정 창 진입, 앱 포커스 복귀, 적용/재시작 버튼 클릭 시 재스캔하고, mtime/size 선검사 후 manifest + entry 본문 hash 로 effective prompt hash 를 갱신하는 기본안을 검토한다.
- [ ] built-in instruction packaging 정책 확정. 기본안은 EmbeddedResource 로 무결성을 보장하고, `InstructionCatalog` 가 embedded resource 와 filesystem source 를 같은 추상으로 노출한다.
- [ ] PromptLoader 또는 별도 composer 에 selected instructions tier 추가.
- [ ] 현재 `PromptLoader.LoadComposed` 는 embedded prompt 뒤에 operator/user DATA tier 까지 내부에서 붙인다. selected instructions 를 DATA tier 앞에 넣으려면 단순 append 가 아니라 PromptLoader 분리 또는 composer 재구성이 필요하다.
- [ ] `ILlmAppProfile`에 instruction source/selection hook 을 추가하는 방향을 우선 검토한다. PR-S1 의 app-specific hook 패턴과 맞고, `Phase1c` 를 받는 모든 provider 의 단일 진입점을 유지할 수 있다.
- [ ] baseline guard 문장은 selected instructions 가 1개 이상일 때 instruction section header 에 조건부 삽입한다. selected instructions 0개일 때 base prompt byte-identical 조건과 충돌하지 않아야 한다.
- [ ] custom sub-tier 신뢰도 정책 확정. custom instruction 은 built-in 과 같은 권한처럼 취급하지 않는다.
- [ ] custom instruction 보안 경계 테스트 추가. custom instruction 이 tool allowlist / MCP host 도구 표면 / sandbox 권한을 넓힐 수 없음을 확인한다.
- [ ] provider 재생성/LLM 재시작 UI 흐름 설계.
- [ ] Codex provider 재생성 시 `instructions.md` stale 방지: `_codexInstructionsPath` 재작성 또는 workspace 재생성 경로를 명시한다.
- [ ] 설정 변경 시 “재시작 후 적용” 표시와 적용 버튼/동작 결정.
- [ ] selected instructions 가 `Phase1c` 를 받는 모든 `LlmProviderKind` 에 동일하게 base prompt 로 포함되는지 검증한다. Anthropic/OpenAI/Ollama/Groq/HKMC API, Claude CLI, Codex CLI 를 포함한다.
- [ ] 선택 해제 후 provider 재생성 시 effective prompt 에 해당 instruction marker 가 사라지는 회귀 테스트 추가.
- [ ] selected instructions 0개일 때 effective base prompt 가 기존과 byte-identical 인지 회귀 테스트 추가.
- [ ] `sourcePriority → order → id` 결정 정렬 회귀 테스트 추가.
- [ ] built-in defaultEnabled 와 사용자 override 의 업데이트 시나리오 테스트 추가.
- [ ] custom instruction malformed manifest / missing entry / empty file 처리 정책과 로그 수준 결정.
- [ ] `yaml.md` 를 선택형 `promaker-yaml` instruction 으로 이관할 경우 `LlmAgent/Prompts/0.domain.md` 의 “yaml.md 자동 주입” 전제 문구도 함께 갱신한다.
- [ ] strict UTF-8 decode 구현 방식을 확정한다. 단순 `Encoding.UTF8` reader 는 invalid byte 를 replacement char 로 통과시킬 수 있으므로 `new UTF8Encoding(false, true)` 류의 throw-on-invalid 설정을 사용한다.

## 주의 사항

- custom instruction 은 사용자 작성 명령성 지침이므로 보안/책임 경계가 필요하다. custom 은 명시 선택 전까지 off 이며, manifest/entry 검증 실패 시 fail-closed 한다.
- operator instruction 은 v1 기본 비활성이다. trusted 로 판정된 경우에만 built-in 에 가까운 tier 로 취급한다.
- 참고자료를 넣는 기존 user prompts dir 과 혼동되지 않도록 UI 에서 “작업 지침은 LLM 행동 규칙, 참고 지식은 자료”로 분리해 설명한다.
- 선택 변경을 진행 중 세션에 즉시 반영하려는 경로는 피한다. 특히 선택 해제는 이전 문맥에서 완전 제거가 불가능하다.
- instruction 선택이 base prompt 를 바꾸므로 prompt cache prefix 는 바뀐다. 이는 선택 변경 시에만 발생하는 비용으로 본다.
