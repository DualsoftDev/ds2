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
        Assert.True(s.AllowAnonymous);
        Assert.True(s.AllowUnsecuredEndpoint);
        Assert.True(s.AutoAcceptUntrustedCertificates);
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
}
