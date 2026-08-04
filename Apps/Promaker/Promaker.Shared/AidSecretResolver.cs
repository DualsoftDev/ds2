using System.Text.Json;

namespace Promaker.Shared;

public sealed record AidCredentials(
    string? Username = null,
    string? Password = null,
    string? BearerToken = null,
    string? ClientId = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public static AidCredentials Anonymous { get; } = new();
}

public interface IAidSecretResolver
{
    Task<AidCredentials> ResolveAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves AID @vault references without ever putting credentials in AASX.
/// HashiCorp Vault is preferred; an explicitly configured local JSON file is
/// available for disconnected commissioning.
/// </summary>
public sealed class AidSecretResolver : IAidSecretResolver, IDisposable
{
    private const int MaxSecretDocumentBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _vaultAddress = Environment.GetEnvironmentVariable("DS2_VAULT_ADDR")?.TrimEnd('/');
    private readonly string? _tokenFile = Environment.GetEnvironmentVariable("DS2_VAULT_TOKEN_FILE");
    private readonly string? _vaultNamespace = Environment.GetEnvironmentVariable("DS2_VAULT_NAMESPACE");
    private readonly string? _localSecretsFile = Environment.GetEnvironmentVariable("DS2_AID_SECRETS_PATH");

    public async Task<AidCredentials> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference)) return AidCredentials.Anonymous;
        if (!reference.StartsWith("@vault:", StringComparison.Ordinal))
            throw new InvalidOperationException("AID credentials must be referenced with @vault:.");

        var (path, selector) = ParseReference(reference);
        JsonElement secret;
        if (!string.IsNullOrWhiteSpace(_vaultAddress))
            secret = await ReadVaultAsync(path, cancellationToken).ConfigureAwait(false);
        else if (!string.IsNullOrWhiteSpace(_localSecretsFile))
            secret = await ReadLocalAsync(path, cancellationToken).ConfigureAwait(false);
        else
            throw new InvalidOperationException(
                "AID requires credentials but neither DS2_VAULT_ADDR nor DS2_AID_SECRETS_PATH is configured.");

        if (!string.IsNullOrWhiteSpace(selector))
        {
            if (secret.ValueKind != JsonValueKind.Object || !secret.TryGetProperty(selector, out var selected))
                throw new InvalidOperationException($"Vault secret '{path}' has no selector '{selector}'.");
            secret = selected;
        }
        return ToCredentials(secret);
    }

    private static (string Path, string? Selector) ParseReference(string reference)
    {
        var raw = reference["@vault:".Length..];
        var hash = raw.IndexOf('#');
        var path = (hash < 0 ? raw : raw[..hash]).Trim('/');
        var selector = hash < 0 ? null : raw[(hash + 1)..];
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid @vault path.");
        return (path, selector);
    }

    private async Task<JsonElement> ReadVaultAsync(string path, CancellationToken cancellationToken)
    {
        var vaultUri = new Uri(_vaultAddress!, UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(vaultUri.UserInfo))
            throw new InvalidOperationException("DS2_VAULT_ADDR must not contain inline credentials.");
        var allowInsecureLocal = bool.TryParse(
            Environment.GetEnvironmentVariable("DS2_VAULT_ALLOW_INSECURE_LOCAL"), out var allow)
            && allow;
        if (!vaultUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            && !(allowInsecureLocal && vaultUri.IsLoopback && vaultUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Vault must use HTTPS; loopback HTTP requires DS2_VAULT_ALLOW_INSECURE_LOCAL=true.");
        if (string.IsNullOrWhiteSpace(_tokenFile) || !File.Exists(_tokenFile))
            throw new InvalidOperationException("DS2_VAULT_TOKEN_FILE is not configured or does not exist.");
        EnsurePrivateFile(_tokenFile, "Vault token");
        var token = (await File.ReadAllTextAsync(_tokenFile, cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16_384)
            throw new InvalidOperationException("Vault token file is empty or exceeds the supported size.");

        var response = await SendAsync(path).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !path.Contains("/data/", StringComparison.Ordinal))
        {
            response.Dispose();
            var separator = path.IndexOf('/');
            if (separator > 0)
                response = await SendAsync(path[..separator] + "/data/" + path[(separator + 1)..]).ConfigureAwait(false);
        }
        using (response)
        {
        response.EnsureSuccessStatusCode();
        var responseBytes = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseBytes);
        if (!document.RootElement.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Vault response for '{path}' contains no data.");
        // KV v2 wraps the user object in data.data; KV v1 does not.
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested)) data = nested;
        return data.Clone();
        }

        async Task<HttpResponseMessage> SendAsync(string requestPath)
        {
            var encodedPath = string.Join('/', requestPath.Split('/').Select(Uri.EscapeDataString));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_vaultAddress}/v1/{encodedPath}");
            request.Headers.Add("X-Vault-Token", token);
            if (!string.IsNullOrWhiteSpace(_vaultNamespace))
                request.Headers.Add("X-Vault-Namespace", _vaultNamespace);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ReadLocalAsync(string path, CancellationToken cancellationToken)
    {
        var secretPath = Path.GetFullPath(_localSecretsFile!);
        EnsurePrivateFile(secretPath, "Local AID secret");
        await using var stream = File.OpenRead(secretPath);
        if (stream.Length > MaxSecretDocumentBytes)
            throw new InvalidOperationException("Local AID secret file exceeds 4 MiB.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(path, out var value))
            throw new InvalidOperationException($"Local AID secret file contains no key '{path}'.");
        return value.Clone();
    }

    private static AidCredentials ToCredentials(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return new AidCredentials(BearerToken: value.GetString());
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AID credential value must be a string or JSON object.");

        string? Get(string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        Dictionary<string, string>? headers = null;
        if (value.TryGetProperty("headers", out var headerObject) && headerObject.ValueKind == JsonValueKind.Object)
        {
            headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headerObject.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    headers[property.Name] = property.Value.GetString()!;
        }
        return new AidCredentials(
            Get("username"), Get("password"), Get("bearerToken") ?? Get("token"),
            Get("clientId"), headers);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxSecretDocumentBytes)
            throw new InvalidOperationException("Vault response exceeds 4 MiB.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length + read > MaxSecretDocumentBytes)
                throw new InvalidOperationException("Vault response exceeds 4 MiB.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    private static void EnsurePrivateFile(string path, string label)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((mode & exposed) != 0)
            throw new InvalidOperationException($"{label} file must not be accessible by group or other users: {path}");
    }

    public void Dispose() => _http.Dispose();
}
