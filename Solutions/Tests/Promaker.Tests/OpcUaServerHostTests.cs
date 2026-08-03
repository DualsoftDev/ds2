using System;
using System.IO;
using System.Threading.Tasks;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// OpcUaServerHost 는 프로세스 전역 싱글턴이라 실제 서버를 기동하는 케이스는 헤드리스 CI 에서
/// 인증서 · 포트 바인딩 문제로 flaky 하다. 여기선 안전한 경로만 검증:
///   - Enabled=false 는 서버를 절대 기동하지 않음.
///   - StopAsync 는 실행 중이 아니어도 no-op 성공.
/// </summary>
public sealed class OpcUaServerHostTests
{
    [Fact]
    public async Task StartAsync_disabled_settings_does_not_start_server()
    {
        var settings = new OpcUaServerSettings { Enabled = false };

        var result = await OpcUaServerHost.Instance.StartAsync(settings);

        Assert.True(result.Success);
        Assert.False(OpcUaServerHost.Instance.IsRunning);
        Assert.Null(result.EndpointUrl);
    }

    [Fact]
    public async Task StopAsync_when_not_running_is_noop_success()
    {
        var result = await OpcUaServerHost.Instance.StopAsync();

        Assert.True(result.Success);
        Assert.False(OpcUaServerHost.Instance.IsRunning);
    }

    [Fact]
    public void DefaultDataRoot_is_under_appdata_dualsoft_promaker()
    {
        var root = OpcUaServerHost.DefaultDataRoot;

        Assert.Contains("Dualsoft", root);
        Assert.Contains("Promaker", root);
        Assert.EndsWith("OpcUa", root);
    }

    [Fact]
    public void Agent_settings_default_to_enabled_only_when_file_is_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds2-agent-ua-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "OpcUaServer.json");
        try
        {
            var missing = OpcUaServerSettings.LoadAgentOrDefault(path);
            Assert.True(missing.Enabled);
            Assert.Equal(OpcUaServerSettings.AgentApplicationUri, missing.ApplicationUri);
            Assert.False(missing.AllowAnonymous);
            Assert.False(missing.AllowUnsecuredEndpoint);
            Assert.False(missing.AutoAcceptUntrustedCertificates);

            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{broken json");
            var corrupt = OpcUaServerSettings.LoadAgentOrDefault(path);
            Assert.False(corrupt.Enabled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
