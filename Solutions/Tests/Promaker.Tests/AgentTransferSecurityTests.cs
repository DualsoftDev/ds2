using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class AgentTransferSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Private_network_detection_is_fail_closed(string value, bool expected)
    {
        Assert.Equal(expected,
            AgentTransferSecurityOptions.IsPrivateOrLoopbackAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public void Api_key_validator_uses_fixed_value_and_reloads_rotated_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-transfer-security-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "key");
        try
        {
            Directory.CreateDirectory(root);
            var first = new string('a', 48);
            var second = new string('b', 48);
            File.WriteAllText(path, first);
            var validator = new AgentTransferApiKeyValidator(path);

            Assert.True(validator.Validate(first));
            Assert.False(validator.Validate(second));

            File.WriteAllText(path, second);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert.True(validator.Validate(second));
            Assert.False(validator.Validate(first));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Api_key_validator_rejects_short_secret()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-transfer-security-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "key");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "short");
            Assert.Throws<InvalidOperationException>(() => new AgentTransferApiKeyValidator(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hub_device_validator_requires_id_and_secret_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), "hub-device-security-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "device-credentials.json");
        try
        {
            Directory.CreateDirectory(root);
            const string secret = "a-unique-device-secret-with-more-than-32-characters";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            File.WriteAllText(path,
                $$"""{"version":2,"devices":[{"deviceId":"pi-01","credentialSha256":"{{hash}}"}]}""");
            MakePrivate(path);

            var validator = HubDeviceCredentialValidator.FromFile(path);

            Assert.Equal(1, validator.Count);
            Assert.True(validator.Validate("pi-01", secret));
            Assert.False(validator.Validate("pi-01", "wrong"));
            Assert.False(validator.Validate("pi-02", secret));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hub_device_validator_rejects_legacy_id_only_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "hub-device-security-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "device-credentials.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{\"version\":1,\"deviceIds\":[\"pi-01\"]}");
            MakePrivate(path);
            Assert.Throws<InvalidOperationException>(() => HubDeviceCredentialValidator.FromFile(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hub_device_validator_reloads_rotated_file_and_fails_closed_on_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "hub-device-security-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "device-credentials.json");
        try
        {
            Directory.CreateDirectory(root);
            const string first = "first-device-secret-with-more-than-32-characters";
            const string second = "second-device-secret-with-more-than-32-characters";
            WriteDeviceFile(path, "pi-01", first);
            var validator = HubDeviceCredentialValidator.FromFile(path);
            Assert.True(validator.Validate("pi-01", first));

            WriteDeviceFile(path, "pi-01", second);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert.True(validator.Validate("pi-01", second));
            Assert.False(validator.Validate("pi-01", first));

            File.WriteAllText(path, "{broken");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            Assert.False(validator.Validate("pi-01", second));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteDeviceFile(string path, string deviceId, string secret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        File.WriteAllText(path,
            $$"""{"version":2,"devices":[{"deviceId":"{{deviceId}}","credentialSha256":"{{hash}}"}]}""");
        MakePrivate(path);
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
