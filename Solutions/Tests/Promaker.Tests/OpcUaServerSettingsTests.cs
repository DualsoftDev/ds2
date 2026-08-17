using System;
using System.IO;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class OpcUaServerSettingsTests
{
    [Fact]
    public void Defaults_Enabled_is_false_and_endpoint_matches_default()
    {
        var s = new OpcUaServerSettings();

        Assert.False(s.Enabled);
        Assert.Equal(OpcUaServerSettings.DefaultEndpointUrl, s.EndpointUrl);
        Assert.Equal(OpcUaServerSettings.DefaultApplicationName, s.ApplicationName);
        Assert.Equal(OpcUaServerSettings.DefaultApplicationUri, s.ApplicationUri);
        Assert.Equal(OpcUaServerSettings.DefaultMaxSessions, s.MaxSessions);
        Assert.Equal(OpcUaServerSettings.DefaultSessionTimeoutMs, s.SessionTimeoutMs);
        Assert.Equal(OpcUaServerSettings.DefaultMinSamplingIntervalMs, s.MinSamplingIntervalMs);
        Assert.Equal(OpcUaServerSettings.DefaultDefaultSamplingIntervalMs, s.DefaultSamplingIntervalMs);
        Assert.Equal(OpcUaServerSettings.DefaultPublishingIntervalMs, s.PublishingIntervalMs);
        Assert.False(s.AllowAnonymous);
        Assert.False(s.AllowUnsecuredEndpoint);
        Assert.False(s.AutoAcceptUntrustedCertificates);
    }

    [Fact]
    public void LoadOrDefault_missing_file_returns_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "Promaker.Tests", nameof(OpcUaServerSettingsTests),
            Guid.NewGuid().ToString("N") + ".json");

        var s = OpcUaServerSettings.LoadOrDefault(path);

        Assert.False(s.Enabled);
        Assert.Equal(OpcUaServerSettings.DefaultEndpointUrl, s.EndpointUrl);
    }

    [Fact]
    public void Save_then_Load_roundtrips_all_fields()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "Promaker.Tests", nameof(OpcUaServerSettingsTests),
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "OpcUaServer.json");

        try
        {
            var original = new OpcUaServerSettings
            {
                Enabled = true,
                EndpointUrl = "opc.tcp://host:9999/x",
                ApplicationName = "Custom.Server",
                ApplicationUri = "urn:custom",
                MaxSessions = 5,
                SessionTimeoutMs = 30_000,
                MinSamplingIntervalMs = 250,
                DefaultSamplingIntervalMs = 1_000,
                PublishingIntervalMs = 2_000,
                AllowAnonymous = false,
                AllowUnsecuredEndpoint = false,
                AutoAcceptUntrustedCertificates = false,
                AllowInsecureLocalDevelopment = true,
                AllowExternalEventInjection = true,
            };

            Assert.True(original.TrySave(path));

            var loaded = OpcUaServerSettings.LoadOrDefault(path);

            Assert.True(loaded.Enabled);
            Assert.Equal(original.EndpointUrl, loaded.EndpointUrl);
            Assert.Equal(original.ApplicationName, loaded.ApplicationName);
            Assert.Equal(original.ApplicationUri, loaded.ApplicationUri);
            Assert.Equal(original.MaxSessions, loaded.MaxSessions);
            Assert.Equal(original.SessionTimeoutMs, loaded.SessionTimeoutMs);
            Assert.Equal(original.MinSamplingIntervalMs, loaded.MinSamplingIntervalMs);
            Assert.Equal(original.DefaultSamplingIntervalMs, loaded.DefaultSamplingIntervalMs);
            Assert.Equal(original.PublishingIntervalMs, loaded.PublishingIntervalMs);
            Assert.False(loaded.AllowAnonymous);
            Assert.False(loaded.AllowUnsecuredEndpoint);
            Assert.False(loaded.AutoAcceptUntrustedCertificates);
            Assert.True(loaded.AllowInsecureLocalDevelopment);
            Assert.True(loaded.AllowExternalEventInjection);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadOrDefault_corrupt_json_returns_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "Promaker.Tests", nameof(OpcUaServerSettingsTests),
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "OpcUaServer.json");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{ not valid json");

            var s = OpcUaServerSettings.LoadOrDefault(path);

            Assert.False(s.Enabled);
            Assert.Equal(OpcUaServerSettings.DefaultEndpointUrl, s.EndpointUrl);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Agent_validation_rejects_insecure_options_without_explicit_local_opt_in()
    {
        var settings = new OpcUaServerSettings
        {
            Enabled = true,
            EndpointUrl = "opc.tcp://localhost:62541/Ds2/OpcUa/Server",
            AllowAnonymous = true,
            AllowUnsecuredEndpoint = false,
            AutoAcceptUntrustedCertificates = false,
        };

        Assert.False(settings.TryValidateForAgent(out var error));
        Assert.Contains("allowInsecureLocalDevelopment", error);

        settings.AllowInsecureLocalDevelopment = true;
        Assert.True(settings.TryValidateForAgent(out error), error);
    }

    [Fact]
    public void Agent_validation_never_allows_insecure_remote_endpoint()
    {
        var settings = new OpcUaServerSettings
        {
            Enabled = true,
            EndpointUrl = "opc.tcp://0.0.0.0:62541/Ds2/OpcUa/Server",
            AllowAnonymous = false,
            AllowUnsecuredEndpoint = true,
            AutoAcceptUntrustedCertificates = false,
            AllowInsecureLocalDevelopment = true,
        };

        Assert.False(settings.TryValidateForAgent(out var error));
        Assert.Contains("loopback", error);
    }

    [Fact]
    public void Agent_validation_accepts_secure_remote_endpoint()
    {
        var settings = new OpcUaServerSettings
        {
            Enabled = true,
            EndpointUrl = "opc.tcp://0.0.0.0:62541/Ds2/OpcUa/Server",
            AllowAnonymous = false,
            AllowUnsecuredEndpoint = false,
            AutoAcceptUntrustedCertificates = false,
        };

        Assert.True(settings.TryValidateForAgent(out var error), error);
    }
}
