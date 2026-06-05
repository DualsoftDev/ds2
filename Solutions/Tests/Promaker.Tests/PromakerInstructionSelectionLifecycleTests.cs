using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Llm.Shared;
using Llm.Shared.Instructions;
using Promaker.LlmAgent;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

[Collection("LlmConfigOverride")]
public sealed class PromakerInstructionSelectionLifecycleTests : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    private readonly string _root;
    private readonly string _configPath;
    private readonly string _customInstructionsDir;
    private readonly string _userPromptsDir;

    public PromakerInstructionSelectionLifecycleTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "promaker-instruction-lifecycle-" + Guid.NewGuid().ToString("N"));
        _configPath = Path.Combine(_root, "llm-config.json");
        _customInstructionsDir = Path.Combine(_root, "Instructions");
        _userPromptsDir = Path.Combine(_root, "Prompts");
        Directory.CreateDirectory(_customInstructionsDir);
        Directory.CreateDirectory(_userPromptsDir);
        LlmConfig.TestConfigPathOverride = _configPath;
        PromakerProfile.TestCustomInstructionsDirOverride = _customInstructionsDir;
        PromakerProfile.TestUserPromptsDirOverride = _userPromptsDir;
    }

    public void Dispose()
    {
        LlmConfig.TestConfigPathOverride = null;
        PromakerProfile.TestCustomInstructionsDirOverride = null;
        PromakerProfile.TestUserPromptsDirOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LlmConfig_instruction_selection_round_trips()
    {
        var cfg = new LlmConfig();
        cfg.SetInstructionSelection(new InstructionSelectionState(
            new[] { "custom:review", "builtin:strict", "custom:review" },
            new[] { "builtin:promaker-yaml" }));

        cfg.SaveTo(_configPath);
        var loaded = LlmConfig.LoadFrom(_configPath);

        Assert.Equal(new[] { "builtin:strict", "custom:review" }, loaded.EnabledInstructionIds);
        Assert.Equal(new[] { "builtin:promaker-yaml" }, loaded.DisabledInstructionIds);
        Assert.Equal(
            new[] { "builtin:strict", "custom:review" },
            loaded.InstructionSelectionState.EnabledInstructionIds);
    }

    [Fact]
    public void PromakerProfile_persisted_builtin_disable_removes_yaml_instruction()
    {
        var cfg = new LlmConfig();
        cfg.SetInstructionSelection(new InstructionSelectionState(
            Array.Empty<string>(),
            new[] { "builtin:promaker-yaml" }));
        cfg.Save();

        var prompt = SystemPromptText.Phase1c(PromakerProfile.Instance);

        Assert.Contains("# DS 모델 도메인 배경", prompt);
        Assert.DoesNotContain("### BUILTIN INSTRUCTION: promaker-yaml", prompt);
        Assert.DoesNotContain("# 시퀀스 모델 생성형 지침", prompt);
    }

    [Fact]
    public void Provider_restart_prompt_removes_disabled_builtin_instruction_marker()
    {
        var activePrompt = LlmChatViewModel.LoadSystemPromptForProvider();
        var activeHash = LlmChatViewModel.ComputeSystemPromptHash(activePrompt);
        Assert.Contains("### BUILTIN INSTRUCTION: promaker-yaml", activePrompt);

        var cfg = LlmConfig.Load();
        cfg.SetInstructionSelection(new InstructionSelectionState(
            Array.Empty<string>(),
            new[] { "builtin:promaker-yaml" }));
        cfg.Save();

        var restartedPrompt = LlmChatViewModel.LoadSystemPromptForProvider();

        Assert.DoesNotContain("### BUILTIN INSTRUCTION: promaker-yaml", restartedPrompt);
        Assert.DoesNotContain("# 시퀀스 모델 생성형 지침", restartedPrompt);
        Assert.True(LlmChatViewModel.IsSystemPromptRestartRequired(activeHash));
    }

    [Fact]
    public void Builtin_default_can_be_disabled_and_restored_by_user_override_update()
    {
        var defaultPrompt = SystemPromptText.Phase1c(PromakerProfile.Instance);
        Assert.Contains("### BUILTIN INSTRUCTION: promaker-yaml", defaultPrompt);

        var disabled = new LlmConfig();
        disabled.SetInstructionSelection(new InstructionSelectionState(
            Array.Empty<string>(),
            new[] { "builtin:promaker-yaml" }));
        disabled.Save();

        var disabledPrompt = SystemPromptText.Phase1c(PromakerProfile.Instance);
        Assert.DoesNotContain("### BUILTIN INSTRUCTION: promaker-yaml", disabledPrompt);

        var restored = LlmConfig.Load();
        restored.SetInstructionSelection(InstructionSelectionState.Empty);
        restored.Save();

        var restoredPrompt = SystemPromptText.Phase1c(PromakerProfile.Instance);
        Assert.Contains("### BUILTIN INSTRUCTION: promaker-yaml", restoredPrompt);
    }

    [Fact]
    public void Custom_instruction_stays_off_until_persisted_enabled()
    {
        CreateCustomInstruction("custom-review", "custom-review", "CUSTOM REVIEW BODY");

        var defaultPrompt = SystemPromptText.Phase1c(PromakerProfile.Instance);
        Assert.DoesNotContain("### CUSTOM INSTRUCTION: custom-review", defaultPrompt);
        Assert.DoesNotContain("CUSTOM REVIEW BODY", defaultPrompt);

        var cfg = new LlmConfig();
        cfg.SetInstructionSelection(new InstructionSelectionState(
            new[] { "custom:custom-review" },
            Array.Empty<string>()));
        cfg.Save();

        var enabledPrompt = SystemPromptText.Phase1c(PromakerProfile.Instance);
        Assert.Contains("### CUSTOM INSTRUCTION: custom-review", enabledPrompt);
        Assert.Contains("CUSTOM REVIEW BODY", enabledPrompt);
    }

    [Fact]
    public void Codex_instructions_file_writer_overwrites_existing_content()
    {
        var path = Path.Combine(_root, "codex-workspace", "instructions.md");

        LlmChatViewModel.WriteCodexInstructionsFile(path, "old instructions");
        LlmChatViewModel.WriteCodexInstructionsFile(path, "new instructions");

        Assert.Equal("new instructions", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Selected_custom_instruction_body_change_requires_restart_from_active_instruction_hash()
    {
        var entryPath = CreateCustomInstruction("custom-review", "custom-review", "OLD CUSTOM BODY");
        var cfg = new LlmConfig();
        cfg.SetInstructionSelection(new InstructionSelectionState(
            new[] { "custom:custom-review" },
            Array.Empty<string>()));
        cfg.Save();

        var activeInstructionHash = LlmChatViewModel.LoadInstructionPromptHashForProvider();
        Assert.False(LlmChatViewModel.IsInstructionPromptRestartRequired(activeInstructionHash));

        File.WriteAllText(entryPath, "NEW CUSTOM BODY", Utf8NoBom);

        Assert.True(LlmChatViewModel.IsInstructionPromptRestartRequired(activeInstructionHash));
    }

    [Fact]
    public void Reloaded_config_snapshot_preserves_instruction_selection_when_provider_is_saved()
    {
        var cfg = new LlmConfig();
        cfg.Save();

        var savedSelection = LlmConfig.Load();
        savedSelection.SetInstructionSelection(new InstructionSelectionState(
            Array.Empty<string>(),
            new[] { "builtin:promaker-yaml" }));
        savedSelection.Save();

        LlmChatViewModel.SaveDefaultProviderSnapshot(LlmProviderKind.Codex);

        var final = LlmConfig.Load();
        Assert.Equal(new[] { "builtin:promaker-yaml" }, final.DisabledInstructionIds);
        Assert.Equal(LlmProviderKind.Codex.ToString(), final.DefaultProvider);
    }

    private string CreateCustomInstruction(string folder, string id, string content)
    {
        var dir = Path.Combine(_customInstructionsDir, folder);
        Directory.CreateDirectory(dir);
        var manifest = JsonSerializer.Serialize(new
        {
            id,
            displayName = id,
            entry = "INSTRUCTION.md",
            defaultEnabled = true,
            order = 100,
        });
        var entryPath = Path.Combine(dir, "INSTRUCTION.md");
        File.WriteAllText(Path.Combine(dir, "instruction.json"), manifest, Utf8NoBom);
        File.WriteAllText(entryPath, content, Utf8NoBom);
        return entryPath;
    }
}
