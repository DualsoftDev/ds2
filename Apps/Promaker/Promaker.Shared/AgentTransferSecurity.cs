using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Promaker.Shared;

/// <summary>Fail-closed configuration for the Agent model transfer listener.</summary>
public sealed record AgentTransferSecurityOptions
{
    public const int DefaultMaxUploadBytes = 256 * 1024 * 1024;

    public required string BindHost { get; init; }
    public required bool ExternalBinding { get; init; }
    public required string? ApiKeyFile { get; init; }
    public required int MaxUploadBytes { get; init; }

    public bool RequireAuthentication => ExternalBinding;

    public string ListenerPrefix(int port)
    {
        var listenerHost = BindHost is "0.0.0.0" or "::" ? "*" : BindHost;
        return $"http://{listenerHost}:{port}/";
    }

    public static AgentTransferSecurityOptions FromEnvironment()
    {
        var bindHost = Environment.GetEnvironmentVariable("DS2_AGENT_TRANSFER_BIND_HOST")?.Trim();
        if (string.IsNullOrWhiteSpace(bindHost)) bindHost = "127.0.0.1";
        var external = !IsLoopbackHost(bindHost);
        var allowPrivateHttp = ReadBool("DS2_AGENT_TRANSFER_ALLOW_PRIVATE_HTTP", false);
        var keyFile = Environment.GetEnvironmentVariable("DS2_AGENT_TRANSFER_API_KEY_FILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(keyFile)) keyFile = Path.GetFullPath(keyFile);

        if (external && !allowPrivateHttp)
            throw new InvalidOperationException(
                "External Agent transfer over HTTP is disabled. Use a private network and explicitly set " +
                "DS2_AGENT_TRANSFER_ALLOW_PRIVATE_HTTP=true, or keep the listener on loopback.");
        if (external && string.IsNullOrWhiteSpace(keyFile))
            throw new InvalidOperationException(
                "DS2_AGENT_TRANSFER_API_KEY_FILE is required for an externally bound Agent transfer listener.");
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile))
                throw new InvalidOperationException($"Agent transfer API key file does not exist: {keyFile}");
            EnsurePrivateFile(keyFile);
        }

        return new AgentTransferSecurityOptions
        {
            BindHost = bindHost,
            ExternalBinding = external,
            ApiKeyFile = keyFile,
            MaxUploadBytes = ReadInt(
                "DS2_AGENT_TRANSFER_MAX_UPLOAD_BYTES",
                DefaultMaxUploadBytes,
                1024 * 1024,
                1024 * 1024 * 1024),
        };
    }

    public static string? ReadClientApiKey()
    {
        var path = Environment.GetEnvironmentVariable("DS2_AGENT_TRANSFER_API_KEY_FILE")?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Agent transfer API key file does not exist: {path}");
        var key = File.ReadAllText(path).Trim();
        if (key.Length < 32) throw new InvalidOperationException("Agent transfer API key must contain at least 32 characters.");
        return key;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Returns true only for loopback, RFC1918 IPv4, link-local, or IPv6 ULA addresses.
    /// Externally bound plaintext transfer endpoints use this to reject Internet peers even
    /// when the operator has explicitly enabled private-network HTTP.
    /// </summary>
    public static bool IsPrivateOrLoopbackAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
               && (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static bool ReadBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) ? parsed : fallback;

    private static int ReadInt(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((mode & exposed) != 0)
            throw new InvalidOperationException(
                $"Agent transfer API key file must not be accessible by group or other users: {path}");
    }
}

/// <summary>Reloadable fixed-time API-key validator. The raw secret is never retained.</summary>
public sealed class AgentTransferApiKeyValidator
{
    private readonly string _path;
    private readonly object _gate = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private byte[] _expectedHash = [];

    public AgentTransferApiKeyValidator(string path)
    {
        _path = path;
        ReloadIfChanged();
    }

    public bool Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 4096) return false;
        ReloadIfChanged();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(actual, _expectedHash);
    }

    private void ReloadIfChanged()
    {
        lock (_gate)
        {
            var currentWrite = File.GetLastWriteTimeUtc(_path);
            if (_expectedHash.Length != 0 && currentWrite == _lastWriteUtc) return;
            var key = File.ReadAllText(_path).Trim();
            if (key.Length < 32)
                throw new InvalidOperationException("Agent transfer API key must contain at least 32 characters.");
            _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            _lastWriteUtc = currentWrite;
        }
    }
}
