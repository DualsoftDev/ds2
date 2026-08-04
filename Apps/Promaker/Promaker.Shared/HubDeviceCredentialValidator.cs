using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>
/// Validates delegated SignalR clients using a public device id plus a separate random credential.
/// The credential file stores only SHA-256 hashes and must be private on Unix.
/// </summary>
public sealed class HubDeviceCredentialValidator
{
    private const int MaxDocumentBytes = 4 * 1024 * 1024;
    private const int MaxDevices = 10_000;
    private const int MaxDeviceIdLength = 256;
    private readonly string _path;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, byte[]> _credentials;
    private DateTime _lastWriteUtc;
    private long _lastLength;

    private HubDeviceCredentialValidator(
        string path,
        IReadOnlyDictionary<string, byte[]> credentials,
        DateTime lastWriteUtc,
        long lastLength)
    {
        _path = path;
        _credentials = credentials;
        _lastWriteUtc = lastWriteUtc;
        _lastLength = lastLength;
    }

    public int Count
    {
        get { lock (_gate) return _credentials.Count; }
    }

    public static HubDeviceCredentialValidator FromFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Delegated Hub device credential file does not exist: {path}");
        var credentials = ReadCredentials(path);
        var info = new FileInfo(path);
        return new HubDeviceCredentialValidator(path, credentials, info.LastWriteTimeUtc, info.Length);
    }

    private static IReadOnlyDictionary<string, byte[]> ReadCredentials(string path)
    {
        EnsurePrivateFile(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxDocumentBytes)
            throw new InvalidOperationException(
                $"Delegated Hub device credential file must not exceed {MaxDocumentBytes} bytes.");
        var document = JsonSerializer.Deserialize<DeviceCredentialsFile>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (document?.Version != 2 || document.Devices is null || document.Devices.Length == 0)
            throw new InvalidOperationException(
                "Delegated Hub requires device-credentials.json version 2 with credential hashes.");
        if (document.Devices.Length > MaxDevices)
            throw new InvalidOperationException(
                $"Delegated Hub device credential file exceeds {MaxDevices} devices.");

        var credentials = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var device in document.Devices)
        {
            var id = device.DeviceId?.Trim();
            var hex = device.CredentialSha256?.Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxDeviceIdLength
                || id.Any(char.IsControl) || string.IsNullOrWhiteSpace(hex))
                throw new InvalidOperationException(
                    "Each delegated Hub device requires deviceId and credentialSha256.");

            byte[] hash;
            try { hash = Convert.FromHexString(hex); }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Invalid credentialSha256 for device '{id}'.");
            }
            if (hash.Length != 32 || !credentials.TryAdd(id, hash))
                throw new InvalidOperationException($"Invalid or duplicate delegated Hub device '{id}'.");
        }
        return credentials;
    }

    public bool Validate(string? deviceId, string? credential)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(credential)
            || deviceId.Length > MaxDeviceIdLength
            || credential.Length > 4096)
            return false;
        byte[] expected;
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists) return false;
                if (info.LastWriteTimeUtc != _lastWriteUtc || info.Length != _lastLength)
                {
                    var reloaded = ReadCredentials(_path);
                    info.Refresh();
                    _credentials = reloaded;
                    _lastWriteUtc = info.LastWriteTimeUtc;
                    _lastLength = info.Length;
                }
            }
            catch
            {
                // A missing, partially rotated, permission-weakened, or corrupt file fails closed.
                return false;
            }
            if (!_credentials.TryGetValue(deviceId, out var found)) return false;
            expected = found;
        }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((mode & exposed) != 0)
            throw new InvalidOperationException(
                $"Device credential file must not be accessible by group or other users: {path}");
    }

    private sealed record DeviceCredentialsFile
    {
        public int Version { get; init; }
        public DeviceCredentialEntry[]? Devices { get; init; }
    }

    private sealed record DeviceCredentialEntry
    {
        public string? DeviceId { get; init; }
        public string? CredentialSha256 { get; init; }
    }
}
