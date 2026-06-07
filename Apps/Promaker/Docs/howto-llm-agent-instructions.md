# LLM Agent 작업 지침 추가 방법

## 목적

Promaker LLM Agent 의 선택형 작업 지침은 LLM 의 행동 방식과 작업 수행 규칙을 바꾸는 instruction tier 이다.
참고 지식/자료를 넣는 user prompts 와 다르며, source 에 따라 built-in 과 custom 으로 구분한다.

## Built-In 과 Custom 구분

- `builtin:<id>`: Promaker assembly 에 embedded resource 로 포함된 지침이다. 배포물에 포함되며 기본 활성화 여부를 `instruction.json` 의 `defaultEnabled` 로 정할 수 있다.
- `custom:<id>`: 사용자 AppData 폴더에서 읽는 지침이다. 사용자가 직접 추가하고, 설정 UI 에서 명시 승인/선택해야 주입된다.
- `operator:<id>`: key parser 는 인식하지만 v1 에서는 reserved 로 두며 비활성 처리한다.

현재 source 는 `Apps/Promaker/Promaker/LlmAgent/PromakerProfile.cs` 가 결정한다.

- built-in source: `Promaker.LlmAgent.Instructions.` embedded resource prefix
- custom source: `%APPDATA%\Dualsoft\Promaker\Instructions`

## Built-In 지침 신규 추가

예시 id 를 `my-task` 라고 하면 다음 순서로 추가한다.

1. 지침 폴더를 만든다.

```text
Apps/Promaker/Promaker/LlmAgent/Instructions/my-task/
```

2. `instruction.json` 을 추가한다.

```json
{
  "id": "my-task",
  "displayName": "My Task",
  "description": "작업 지침 설명",
  "entry": "INSTRUCTION.md",
  "defaultEnabled": false,
  "order": 200
}
```

3. `INSTRUCTION.md` 를 추가한다.

```md
# My Task

이 지침이 활성화됐을 때 LLM 이 따라야 할 작업 규칙을 작성한다.
```

4. `Apps/Promaker/Promaker/Promaker.csproj` 에 embedded resource 를 추가한다.

```xml
<EmbeddedResource Include="LlmAgent\Instructions\my-task\instruction.json"
                  LogicalName="Promaker.LlmAgent.Instructions.my-task.instruction.json" />
<EmbeddedResource Include="LlmAgent\Instructions\my-task\INSTRUCTION.md"
                  LogicalName="Promaker.LlmAgent.Instructions.my-task.INSTRUCTION.md" />
```

5. 빌드/테스트로 resource 이름과 선택 동작을 확인한다.

- `defaultEnabled=true`: 기본 선택 상태로 effective prompt 에 포함된다. 사용자가 UI 에서 끌 수 있다.
- `defaultEnabled=false`: 기본 꺼짐 상태다. 사용자가 UI 에서 켜야 포함된다.
- 같은 `id` 가 다른 built-in/custom 지침과 충돌하면 catalog 에서 제외된다.

## Custom 지침 사용자 추가

사용자는 Promaker 설정 UI 의 LLM 작업 지침 섹션에서 custom instructions 폴더를 열거나, 아래 폴더에 직접 package 폴더를 만들 수 있다.

```text
%APPDATA%\Dualsoft\Promaker\Instructions\<package-folder>\
```

예시:

```text
%APPDATA%\Dualsoft\Promaker\Instructions\my-custom\
  instruction.json
  INSTRUCTION.md
```

`instruction.json` 예:

```json
{
  "id": "my-custom",
  "displayName": "내 사용자 지침",
  "description": "사용자 정의 작업 지침",
  "entry": "INSTRUCTION.md",
  "defaultEnabled": true,
  "order": 100
}
```

`INSTRUCTION.md` 예:

```md
# 내 사용자 지침

이 지침이 선택됐을 때 LLM 이 따라야 할 작업 규칙을 작성한다.
```

custom 지침의 `defaultEnabled` 값은 자동 활성화에 사용하지 않는다. custom 은 사용자가 설정 UI 에서 preview 후 명시적으로 승인/체크해야 `custom:<id>` 로 저장되고 effective prompt 에 포함된다.

## Manifest 와 파일 검증 규칙

- `id`: source-qualified key 의 suffix 로 사용된다. 예: `builtin:promaker-yaml`, `custom:my-custom`.
- `entry`: package 폴더 내부의 안전한 상대경로 `.md` 파일이어야 한다.
- `displayName`: 비어 있으면 `id` 로 fallback 된다.
- `order`: 같은 source tier 안에서 정렬에 사용된다. 최종 정렬은 `sourcePriority -> order -> id(StringComparer.Ordinal)` 이다.
- strict UTF-8 로 읽을 수 없는 파일은 제외된다.
- path traversal, rooted path, 빈 path segment, symlink/reparse point, 과대 manifest/entry, 빈 entry 파일은 fail-closed 로 제외된다.

## 적용 시점

지침 선택 또는 본문 변경은 현재 provider session 에 즉시 주입하지 않는다. 설정 UI 는 “LLM 재시작 후 적용” 상태를 표시하고, provider 재생성 시 새 effective prompt 가 적용된다.

Codex provider 는 provider 생성 시 `instructions.md` 를 다시 써 stale prompt 를 막는다.
