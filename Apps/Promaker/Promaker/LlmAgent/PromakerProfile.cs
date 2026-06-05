using System.Collections.Generic;
using System.Reflection;
using Llm.Shared.Abstractions;
using Llm.Shared.Instructions;
using Promaker.Services;

namespace Promaker.LlmAgent;

/// <summary>
/// <see cref="ILlmAppProfile"/> 의 Promaker 측 구현.
///
/// <para><b>EmbeddedPromptsSources 순서</b>:
/// <list type="number">
/// <item>baseline = Llm.Shared.dll 의 <c>Llm.Shared.Prompts.baseline.</c> prefix
///   (1.attachments / 2.knowledge-base / 3.environment)</item>
/// <item>App overlay = Promaker.dll 의 <c>Promaker.LlmAgent.Prompts.</c> prefix
///   (0.domain — mandatory base, *.mdx 는 주입 제외)</item>
/// <item>built-in instruction = Promaker.dll 의 <c>Promaker.LlmAgent.Instructions.</c> prefix
///   (promaker-yaml 은 defaultEnabled=true 이며 사용자가 끌 수 있음)</item>
/// </list>
/// PromptLoader 가 baseline → overlay → selected instructions 순서로 concat → LLM 에 명시 주입 순서 signal.
/// </para>
///
/// <para><b>UserPromptsDir / LegacyUserPromptsDir</b>: <see cref="SettingsPaths"/> SSOT 위임.
/// PR-S1 이전 PromptLoader 직접 의존 (`using Promaker.Services;`) 을 본 profile 로 추출.</para>
///
/// <para><b>LoggerName</b>: "Promaker.LlmAgent.Provider" — log4net.config 정합 (기존 logger 이름 유지, log 회귀 0).</para>
/// </summary>
public sealed class PromakerProfile : ILlmAppProfile
{
    /// <summary>process-wide singleton (PromptLoader 호출처에서 매번 new 안 함).</summary>
    public static PromakerProfile Instance { get; } = new();

    private readonly IReadOnlyList<PromptSource> _sources;
    private readonly IReadOnlyList<InstructionSource> _instructionSources;

    private PromakerProfile()
    {
        // baseline = Llm.Shared 의 ILlmAppProfile assembly. App overlay = 본 클래스의 assembly (Promaker.dll).
        var baselineAsm = typeof(ILlmAppProfile).Assembly;
        var promakerAsm = typeof(PromakerProfile).Assembly;
        _sources = new[]
        {
            new PromptSource(baselineAsm, "Llm.Shared.Prompts.baseline."),
            new PromptSource(promakerAsm, "Promaker.LlmAgent.Prompts."),
        };
        _instructionSources = new[]
        {
            InstructionSource.BuiltInEmbedded(promakerAsm, "Promaker.LlmAgent.Instructions."),
        };
    }

    public IReadOnlyList<PromptSource> EmbeddedPromptsSources => _sources;

    public IReadOnlyList<InstructionSource> InstructionSources => _instructionSources;

    public string UserPromptsDir => SettingsPaths.UserPromptsDir;

    public string? LegacyUserPromptsDir => SettingsPaths.LegacyUserPromptsDir;

    public string LoggerName => "Promaker.LlmAgent.Provider";
}
