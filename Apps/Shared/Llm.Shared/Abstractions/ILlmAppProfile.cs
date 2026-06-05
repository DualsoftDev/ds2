using System.Collections.Generic;
using System.Reflection;
using Llm.Shared.Instructions;

namespace Llm.Shared.Abstractions;

/// <summary>
/// 한 assembly 안의 embedded prompt resource 집합을 가리키는 source descriptor.
/// <c>Prefix</c> 로 시작하는 .md resource 만 <see cref="PromptLoader"/> 가 자연 정렬 후 concat.
/// </summary>
/// <param name="Assembly">resource manifest 를 조회할 assembly (typeof(X).Assembly).</param>
/// <param name="Prefix">resource name prefix (예: "Llm.Shared.Prompts.baseline." / "Promaker.LlmAgent.Prompts.").</param>
public sealed record PromptSource(Assembly Assembly, string Prefix);

/// <summary>
/// LLM 인프라 (PromptLoader / ApiChatProvider / McpHost) 의 App-specific hook.
///
/// <para><b>주입 방식</b>: 각 App 이 본 interface 구현체 (`PromakerProfile` / `XxxDesignerProfile` 등) 를
/// 신설 + DI 등록. Llm.Shared 안 코드는 직접 App 의 path/logger/tool 이름을 모름 — 모두 본 interface 를 통해 위임.</para>
///
/// <para><b>확장 정책</b>: 신규 hook 추가 시 새 property 추가 (positional record 대신 interface 라 default 구현 도입 가능).
/// 기존 구현체는 미구현 시 compile error → 누락 검출 명확.</para>
/// </summary>
public interface ILlmAppProfile
{
    /// <summary>baseline + App overlay 의 embedded prompt source 들. PromptLoader 가 본 순서대로 concat.</summary>
    IReadOnlyList<PromptSource> EmbeddedPromptsSources { get; }

    /// <summary>사용자 작성 prompt override (*.md) 디렉토리. PromptLoader 가 본 dir 의 *.md 를 user-tier 로 추가.</summary>
    string UserPromptsDir { get; }

    /// <summary>옛 버전의 user prompts dir (마이그레이션 안내 한정). 없으면 null. PromptLoader 가 *.md 발견 시 1회 warn.</summary>
    string? LegacyUserPromptsDir { get; }

    /// <summary>선택형 작업 지침 catalog source 들. 없으면 기존 prompt 와 byte-identical 이어야 한다.</summary>
    IReadOnlyList<InstructionSource> InstructionSources => Array.Empty<InstructionSource>();

    /// <summary>source-qualified key 기반 선택 상태. operator:<id> 는 v1 reserved 로 파싱만 하고 비활성 처리한다.</summary>
    InstructionSelectionState InstructionSelection => InstructionSelectionState.Empty;

    /// <summary>작업 지침 manifest/entry discovery 검증 옵션.</summary>
    InstructionCatalogOptions InstructionCatalogOptions => InstructionCatalogOptions.Default;

    /// <summary>선택된 작업 지침 prompt 합성 옵션.</summary>
    InstructionPromptComposerOptions InstructionPromptComposerOptions => InstructionPromptComposerOptions.Default;

    /// <summary>log4net logger 이름. App 마다 다른 logger 로 분리 (e.g. "Promaker.LlmAgent.Provider").</summary>
    string LoggerName { get; }
}
