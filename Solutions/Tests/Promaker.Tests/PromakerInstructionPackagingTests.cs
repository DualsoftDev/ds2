using System;
using System.Collections.Generic;
using System.IO;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Instructions;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

public sealed class PromakerInstructionPackagingTests
{
    [Fact]
    public void PromakerProfile_default_prompt_includes_promaker_yaml_instruction()
    {
        var prompt = SystemPromptText.Phase1c(PromakerProfile.Instance);

        Assert.Contains("### BUILTIN INSTRUCTION: promaker-yaml", prompt);
        Assert.Contains("# 시퀀스 모델 생성형 지침", prompt);
        Assert.Contains("protocol: promaker/v0", prompt);
    }

    [Fact]
    public void PromakerProfile_disable_promaker_yaml_omits_marker_and_body()
    {
        var root = NewTempRoot();
        try
        {
            var profile = new SelectionOverrideProfile(
                root,
                new InstructionSelectionState(
                    Array.Empty<string>(),
                    new[] { "builtin:promaker-yaml" }));

            var prompt = SystemPromptText.Phase1c(profile);

            Assert.Contains("# DS 모델 도메인 배경", prompt);
            Assert.DoesNotContain("### BUILTIN INSTRUCTION: promaker-yaml", prompt);
            Assert.DoesNotContain("# 시퀀스 모델 생성형 지침", prompt);
            Assert.DoesNotContain("pong: Instructions/promaker-yaml/INSTRUCTION.md", prompt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Promaker_yaml_is_instruction_resource_not_always_on_prompt_resource()
    {
        var names = typeof(PromakerProfile).Assembly.GetManifestResourceNames();

        Assert.DoesNotContain("Promaker.LlmAgent.Prompts.yaml.md", names);
        Assert.Contains("Promaker.LlmAgent.Instructions.promaker-yaml.instruction.json", names);
        Assert.Contains("Promaker.LlmAgent.Instructions.promaker-yaml.INSTRUCTION.md", names);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "promaker-instruction-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SelectionOverrideProfile : ILlmAppProfile
    {
        private readonly PromakerProfile _inner = PromakerProfile.Instance;

        public SelectionOverrideProfile(
            string userPromptsDir,
            InstructionSelectionState instructionSelection)
        {
            UserPromptsDir = userPromptsDir;
            InstructionSelection = instructionSelection;
        }

        public IReadOnlyList<PromptSource> EmbeddedPromptsSources => _inner.EmbeddedPromptsSources;
        public IReadOnlyList<InstructionSource> InstructionSources => _inner.InstructionSources;
        public InstructionSelectionState InstructionSelection { get; }
        public string UserPromptsDir { get; }
        public string? LegacyUserPromptsDir => null;
        public string LoggerName => "Promaker.Tests.InstructionPackaging";
    }
}
