using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class AgentLastKnownGoodStoreTests
{
    [Fact]
    public void Save_then_load_roundtrips_an_isolated_activation_snapshot()
    {
        var root = NewRoot();
        try
        {
            var source = CreateSource(root);
            var expectedHash = Hash(source.AasxPath);
            var ua = SecureUaSettings();

            Assert.True(AgentLastKnownGoodStore.TrySave(root, source, ua, expectedHash, out var saveError), saveError);
            Assert.True(AgentLastKnownGoodStore.TryLoad(root, out var snapshot, out var loadError), loadError);
            Assert.NotNull(snapshot);
            Assert.Equal(expectedHash, snapshot!.ModelHash);
            Assert.Equal("Monitoring", snapshot.Session.RuntimeMode);
            Assert.Equal("agent-recovery", snapshot.Session.RequestedBy);
            Assert.NotEqual(source.AasxPath, snapshot.Session.AasxPath);
            Assert.Equal(File.ReadAllBytes(source.AasxPath), File.ReadAllBytes(snapshot.Session.AasxPath));
            Assert.Equal(File.ReadAllText(source.PlcConnectionPath), File.ReadAllText(snapshot.Session.PlcConnectionPath));
            Assert.False(snapshot.UaSettings.AllowAnonymous);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_rejects_a_tampered_snapshot()
    {
        var root = NewRoot();
        try
        {
            var source = CreateSource(root);
            Assert.True(AgentLastKnownGoodStore.TrySave(
                root, source, SecureUaSettings(), Hash(source.AasxPath), out var saveError), saveError);
            var snapshotDirectory = Directory.GetDirectories(Path.Combine(root, "snapshots"))
                .Single(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal));
            File.AppendAllText(Path.Combine(snapshotDirectory, "project.aasx"), "tampered");

            Assert.False(AgentLastKnownGoodStore.TryLoad(root, out _, out var loadError));
            Assert.Contains("integrity", loadError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentSession CreateSource(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var aasx = Path.Combine(input, "project.aasx");
        var plc = Path.Combine(input, "PlcConnection.json");
        File.WriteAllBytes(aasx, [0x50, 0x4b, 0x03, 0x04, 0x11, 0x22, 0x33]);
        File.WriteAllText(plc, "{\"scanIntervalMs\":100}");
        return new AgentSession
        {
            AasxPath = aasx,
            PlcConnectionPath = plc,
            ActivatedAtUtc = "2026-08-04T00:00:00Z",
            RequestedBy = "test",
            RuntimeMode = "Monitoring",
            IsRealPlcConnected = true,
        };
    }

    private static OpcUaServerSettings SecureUaSettings() => new()
    {
        Enabled = true,
        EndpointUrl = "opc.tcp://0.0.0.0:62541/Ds2/OpcUa/Server",
        AllowAnonymous = false,
        AllowUnsecuredEndpoint = false,
        AutoAcceptUntrustedCertificates = false,
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Promaker.Tests", nameof(AgentLastKnownGoodStoreTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
