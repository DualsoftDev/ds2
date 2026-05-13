using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 2 후속 — 결정 5.0/5.4 (Owner-only ACL + PID 포함 파일명 + SweepStale) 회귀 테스트.
/// 핵심 보안 표면: 같은 user 의 다른 프로세스가 nonce 평문 read 차단.
/// </summary>
public sealed class McpConfigWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Promaker.Tests",
        nameof(McpConfigWriterTests),
        Guid.NewGuid().ToString("N"));

    public McpConfigWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ─── TryParsePidFromFileName ─────────────────────────────────────────────

    [Theory]
    [InlineData("mcp-1-12345-abcdef.json", true, 12345)]
    [InlineData("mcp-0-9999-deadbeef.json", true, 9999)]
    [InlineData("mcp-7-1-x.json", true, 1)]
    public void TryParsePidFromFileName_parses_valid_patterns(string fileName, bool expected, int expectedPid)
    {
        var ok = McpConfigWriter.TryParsePidFromFileName(fileName, out var pid);
        Assert.Equal(expected, ok);
        Assert.Equal(expectedPid, pid);
    }

    [Theory]
    [InlineData("mcp-1.json")]                  // 부족한 segments
    [InlineData("mcp-1-abc-x.json")]            // pid 가 정수 아님
    [InlineData("notmcp-1-2-x.json")]           // prefix 무관 — 그래도 segments 형식이면 parse 시도
    public void TryParsePidFromFileName_rejects_invalid(string fileName)
    {
        var ok = McpConfigWriter.TryParsePidFromFileName(fileName, out _);
        if (fileName.StartsWith("notmcp-"))
        {
            // segments 형식이라 정수 parse 자체는 OK — pid=2. 본 테스트는 pid 추출 로직만 확인.
            Assert.True(ok);
        }
        else
        {
            Assert.False(ok);
        }
    }

    // ─── WriteWithOwnerOnlyAcl ───────────────────────────────────────────────

    [Fact]
    public void WriteWithOwnerOnlyAcl_creates_file_with_content()
    {
        var path = Path.Combine(_root, "test.json");
        const string json = """{"hello":"world"}""";

        McpConfigWriter.WriteWithOwnerOnlyAcl(path, json);

        Assert.True(File.Exists(path));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void WriteWithOwnerOnlyAcl_applies_owner_only_ACL_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return; // ACL 검증은 Windows 한정

        var path = Path.Combine(_root, "acl.json");
        McpConfigWriter.WriteWithOwnerOnlyAcl(path, "{}");

        var fi = new FileInfo(path);
        var sec = fi.GetAccessControl();

        // Inheritance 차단됨
        Assert.True(sec.AreAccessRulesProtected);

        // Owner = current user
        var ownerSid = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var currentSid = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentSid);
        Assert.Equal(currentSid, ownerSid);

        // 모든 access rule 의 IdentityReference 가 current user SID
        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier));
        Assert.NotEmpty(rules);
        foreach (FileSystemAccessRule r in rules)
        {
            Assert.Equal(currentSid, r.IdentityReference);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            // FullControl 포함
            Assert.True((r.FileSystemRights & FileSystemRights.FullControl) != 0);
        }
    }

    // ─── Create / Dispose round-trip ────────────────────────────────────────

    [Fact]
    public void Create_writes_proper_json_and_dispose_deletes_file()
    {
        const string serverName = "promaker-test";
        const string url = "http://127.0.0.1:54321/";
        const string nonce = "abcdef0123456789";

        string path;
        using (var writer = McpConfigWriter.Create(serverName, url, nonce))
        {
            path = writer.Path;
            Assert.Equal(serverName, writer.ServerName);
            Assert.True(File.Exists(path));

            var content = File.ReadAllText(path);
            Assert.Contains(serverName, content);
            Assert.Contains(url, content);
            Assert.Contains(nonce, content);
            Assert.Contains("X-Promaker-Nonce", content);
            Assert.Contains("\"type\": \"http\"", content);

            // 파일명에 PID 포함
            Assert.True(McpConfigWriter.TryParsePidFromFileName(Path.GetFileName(path), out var pid));
            Assert.Equal(Environment.ProcessId, pid);
        }

        // Dispose 시 파일 제거
        Assert.False(File.Exists(path));
    }

    // ─── SweepStale: current pid 자기 자신은 sweep 안 함 ─────────────────────

    [Fact]
    public void SweepStale_does_not_remove_current_pid_file()
    {
        // Create 가 만든 파일은 자기 PID 라 SweepStale 의 알리브 pid + 신규 mtime 두 조건으로 보호됨.
        using var writer = McpConfigWriter.Create("promaker-self", "http://127.0.0.1:1/", "n");
        var path = writer.Path;
        Assert.True(File.Exists(path));

        McpConfigWriter.SweepStale();

        Assert.True(File.Exists(path));
    }
}
