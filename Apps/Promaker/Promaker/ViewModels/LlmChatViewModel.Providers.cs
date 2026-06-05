using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;
using Llm.Shared.Instructions;
using Llm.Shared.Mcp;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — 각 ILlmProvider 인스턴스 생성. ConfigureProviderAsync 의 분기에서 호출.
/// 본체와의 share: _mcpHost / _mcpConfig / _codexWorkspacePath / _codexInstructionsPath / _config + Log.
/// </summary>
public partial class LlmChatViewModel
{
    private static LlmProviderDeclinedException ProviderDeclined(string providerLabel, string declineReason) =>
        new($"{providerLabel} {declineReason} — provider 비활성화. 다른 provider 선택 또는 재선택 시 다이얼로그 다시 표시됩니다.");

    internal static string LoadSystemPromptForProvider() =>
        SystemPromptText.Phase1c(PromakerProfile.Instance);

    internal static string LoadInstructionPromptForProvider()
    {
        ILlmAppProfile profile = PromakerProfile.Instance;
        var catalog = InstructionCatalog.Discover(
            profile.InstructionSources,
            profile.InstructionCatalogOptions);
        var selection = InstructionSelection.Resolve(catalog, profile.InstructionSelection);
        return InstructionPromptComposer.Compose(
            selection.EnabledInstructions,
            profile.InstructionPromptComposerOptions).Text;
    }

    internal static string ComputeSystemPromptHash(string systemPrompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt)));

    internal static string LoadSystemPromptHashForProvider() =>
        ComputeSystemPromptHash(LoadSystemPromptForProvider());

    internal static string LoadInstructionPromptHashForProvider() =>
        ComputeSystemPromptHash(LoadInstructionPromptForProvider());

    internal static bool IsSystemPromptRestartRequired(string? activeSystemPromptHash)
    {
        var pendingHash = LoadSystemPromptHashForProvider();
        return activeSystemPromptHash is null
               || !string.Equals(activeSystemPromptHash, pendingHash, StringComparison.Ordinal);
    }

    internal static bool IsInstructionPromptRestartRequired(string? activeInstructionPromptHash)
    {
        var pendingHash = LoadInstructionPromptHashForProvider();
        return activeInstructionPromptHash is null
               || !string.Equals(activeInstructionPromptHash, pendingHash, StringComparison.Ordinal);
    }

    internal static void WriteCodexInstructionsFile(string path, string systemPrompt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, systemPrompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string CreateProviderSystemPrompt()
    {
        var prompt = LoadSystemPromptForProvider();
        _activeSystemPromptHash = ComputeSystemPromptHash(prompt);
        _activeInstructionPromptHash = LoadInstructionPromptHashForProvider();
        return prompt;
    }

    private ILlmProvider CreateClaudeProvider()
    {
        // **Plan B (2026-05-27)** — 정적 12종 (Promaker 6 + LightHouse wrapper 6).
        // lighthouse server 별 동적 명명 폐기 — wrapper 가 multi-service fan-out 자체 처리.
        var allowed = PromakerToolNames.All;

        var options = new ClaudeCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            mcpConfigPath: Microsoft.FSharp.Core.FSharpOption<string>.Some(_mcpConfig!.Path),
            permissionMode: Microsoft.FSharp.Core.FSharpOption<string>.Some("bypassPermissions"),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            systemPrompt: Microsoft.FSharp.Core.FSharpOption<string>.Some(CreateProviderSystemPrompt()),
            strictMcpConfig: true,
            allowedTools: Microsoft.FSharp.Core.FSharpOption<string[]>.Some(allowed),
            channelCapacity: 256,
            onProcessStarted: Microsoft.FSharp.Core.FSharpOption<Action<System.Diagnostics.Process>>.Some(
                new Action<System.Diagnostics.Process>(ChildProcessTracker.AddProcess)));
        return new ClaudeCliProvider(options);
    }

    /// <summary>
    /// Codex CLI provider. config override 4종 (mcp url + http_headers + approval_policy + sandbox_mode) +
    /// `experimental_instructions_file` 로 system prompt override + `cd:` 격리 워크스페이스.
    ///
    /// **`sandbox_mode = "danger-full-access"` 사유**: codex 0.125 는 read-only / workspace-write 모드에서
    /// MCP tool call 을 "user cancelled" 로 자동 cancel (community issue 1379772). danger-full-access 만 통과.
    /// 부작용 (file system / network 자유 접근) 은 `cd:` 격리로 1차 완화 — codex 가 빈 임시 폴더에서
    /// 실행되어 사용자 git working tree / 작업 디렉토리에 닿지 않음.
    /// </summary>
    private ILlmProvider CreateCodexProvider()
    {
        // Codex 추가 권한 동의 — danger-full-access sandbox + ~/.codex/sessions rollout 이 일반 LLM 동의보다
        // 강한 위임이라 별도 confirm. 거부 시 LlmProviderDeclinedException → ConfigureProviderAsync catch 의
        // 별도 분기로 Info + 안내 메시지 처리 (Error 톤 아님).
        if (!LlmConfig.EnsureCodexConsent())
            throw ProviderDeclined("Codex", "추가 권한 (danger-full-access sandbox) 동의 미완료");

        // 워크스페이스 디렉토리 lazy 생성, instructions 파일은 provider 생성 때마다 덮어쓰기.
        // experimental_instructions_file 은 path 만 받음 → stale instructions.md 재사용 차단.
        if (_codexWorkspacePath == null)
        {
            _codexWorkspacePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Promaker", $"codex-workspace-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(_codexWorkspacePath);
            _codexInstructionsPath = System.IO.Path.Combine(_codexWorkspacePath, "instructions.md");
            Log.Info($"Codex 워크스페이스 격리 디렉토리 생성 — {_codexWorkspacePath}");
        }
        _codexInstructionsPath ??= System.IO.Path.Combine(_codexWorkspacePath, "instructions.md");
        WriteCodexInstructionsFile(_codexInstructionsPath, CreateProviderSystemPrompt());

        var configOverrides = new[]
        {
            new System.Tuple<string, string>(
                "mcp_servers.promaker.url",
                $"\"{_mcpHost.ServerUrl}\""),
            new System.Tuple<string, string>(
                "mcp_servers.promaker.http_headers",
                $"{{ \"X-Promaker-Nonce\" = \"{_mcpHost.HandshakeNonce}\" }}"),
            new System.Tuple<string, string>("approval_policy", "\"never\""),
            new System.Tuple<string, string>("sandbox_mode", "\"danger-full-access\""),
        };
        var options = new CodexCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            cd: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexWorkspacePath!),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            json: true,
            ephemeral: false,
            ignoreUserConfig: true,
            skipGitRepoCheck: true,
            fullAuto: false,
            dangerouslyBypassApprovalsAndSandbox: false,
            configOverrides: Microsoft.FSharp.Core.FSharpOption<System.Tuple<string, string>[]>.Some(configOverrides),
            experimentalInstructionsFile: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexInstructionsPath!),
            codexHome: Microsoft.FSharp.Core.FSharpOption<string>.None,
            channelCapacity: 256);
        return new CodexCliProvider(options);
    }

    private async Task<ILlmProvider> CreateAnthropicApiProviderAsync()
    {
        if (!LlmConfig.EnsureApiCostConsent("Anthropic API"))
            throw ProviderDeclined("Anthropic API", "비용 경고 동의 미완료");

        var apiKey = _config.GetApiKey(ApiProviderFactory.AnthropicKey)
                     ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                     ?? "";
        return await ApiProviderFactory.CreateAnthropicAsync(
            apiKey: apiKey,
            model: _config.AnthropicModel,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    private async Task<ILlmProvider> CreateOpenAiApiProviderAsync()
    {
        if (!LlmConfig.EnsureApiCostConsent("OpenAI API"))
            throw ProviderDeclined("OpenAI API", "비용 경고 동의 미완료");

        var apiKey = _config.GetApiKey(ApiProviderFactory.OpenAiKey)
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? "";
        return await ApiProviderFactory.CreateOpenAiAsync(
            apiKey: apiKey,
            model: _config.OpenAiModel,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }

    private async Task<ILlmProvider> CreateOllamaApiProviderAsync() =>
        await ApiProviderFactory.CreateOllamaAsync(
            baseUrl: _config.OllamaBaseUrl,
            model: _config.OllamaModel,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);

    /// <summary>
    /// F-1 spike — Groq OpenAI 호환 endpoint provider. API key 는 다른 provider 와 동일 2-tier
    /// (DPAPI dict <c>GroqKey="groq"</c> 우선 / env-var <c>GROQ_API_KEY</c> fallback). 모델은
    /// <c>meta-llama/llama-4-scout-17b-16e-instruct</c> 하드코딩 (모델 변경 시 본 메서드 재컴파일 필요).
    /// </summary>
    private async Task<ILlmProvider> CreateGroqApiProviderAsync()
    {
        var apiKey = _config.GetApiKey(ApiProviderFactory.GroqKey)
                     ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")?.Trim()
                     ?? "";
        const string model = "meta-llama/llama-4-scout-17b-16e-instruct";
        return await ApiProviderFactory.CreateGroqAsync(
            apiKey: apiKey,
            model: model,
            systemPrompt: CreateProviderSystemPrompt(),
            mcpServerUrl: _mcpHost.ServerUrl,
            mcpNonce: _mcpHost.HandshakeNonce).ConfigureAwait(true);
    }
}
