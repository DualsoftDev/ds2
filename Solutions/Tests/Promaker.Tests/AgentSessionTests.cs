using Promaker.Shared;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace Promaker.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public void Exact_load_rejects_corrupt_or_unsupported_session_instead_of_using_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "Promaker.Tests", nameof(AgentSessionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.json");
        try
        {
            File.WriteAllText(path, "{not-json");
            Assert.False(AgentSession.TryLoadExact(path, out _, out var corruptError));
            Assert.Contains("invalid", corruptError, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, """
                {
                  "schemaVersion": 99,
                  "aasxPath": "project.aasx",
                  "activatedAtUtc": "2026-08-04T00:00:00Z",
                  "requestedBy": "test",
                  "runtimeMode": "Monitoring"
                }
                """);
            Assert.False(AgentSession.TryLoadExact(path, out _, out var versionError));
            Assert.Contains("schemaVersion", versionError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exact_load_normalizes_valid_runtime_mode()
    {
        var root = Path.Combine(Path.GetTempPath(), "Promaker.Tests", nameof(AgentSessionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "aasxPath": "project.aasx",
                  "activatedAtUtc": "2026-08-04T00:00:00Z",
                  "requestedBy": "test",
                  "runtimeMode": "control"
                }
                """);
            Assert.True(AgentSession.TryLoadExact(path, out var session, out var error), error);
            Assert.Equal("Control", session!.RuntimeMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Aasx_package_preflight_rejects_unsafe_nested_compression()
    {
        var root = Path.Combine(Path.GetTempPath(), "Promaker.Tests", nameof(AgentSessionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "project.aasx");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("aasx/huge-zero.bin", CompressionLevel.Optimal);
                using var stream = entry.Open();
                var chunk = new byte[1024 * 1024];
                for (var i = 0; i < 11; i++) stream.Write(chunk);
            }

            Assert.False(AasxPackageSafety.TryValidate(path, out var error));
            Assert.Contains("compression ratio", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
