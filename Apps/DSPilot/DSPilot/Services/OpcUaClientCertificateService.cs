// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DSPilot.Infrastructure;

namespace DSPilot.Services;

public sealed record IssuedOpcUaClientCertificate(
    byte[] PfxBytes,
    string FileName,
    string Thumbprint,
    DateTimeOffset NotAfterUtc);

public sealed record OpcUaRejectedCertificate(
    string Thumbprint,
    string Subject,
    string Issuer,
    DateTimeOffset NotAfterUtc);

/// <summary>
/// DSPilot 운영 화면에서 OPC UA 사용자 인증서를 발급하고 Agent PKI 신뢰 목록을 관리한다.
/// 개인키는 메모리에서 PFX로 내보낸 뒤 저장하지 않으며, Agent에는 공개 인증서만 남긴다.
/// </summary>
public sealed class OpcUaClientCertificateService
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private readonly string _certificateRoot;
    private readonly object _gate = new();

    public OpcUaClientCertificateService()
        : this(SharedPaths.AgentOpcUaCertificateDirectory)
    {
    }

    /// <summary>테스트 및 별도 호스트 경로용 생성자.</summary>
    public OpcUaClientCertificateService(string certificateRoot)
    {
        if (string.IsNullOrWhiteSpace(certificateRoot))
            throw new ArgumentException("OPC UA certificate root is required.", nameof(certificateRoot));
        _certificateRoot = Path.GetFullPath(certificateRoot);
    }

    public string TrustedUserCertificateDirectory =>
        Path.Combine(_certificateRoot, "trustedUser", "certs");

    public string RejectedApplicationCertificateDirectory =>
        Path.Combine(_certificateRoot, "rejected", "certs");

    public string TrustedApplicationCertificateDirectory =>
        Path.Combine(_certificateRoot, "trusted", "certs");

    public IssuedOpcUaClientCertificate IssueUserCertificate(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("PFX password is required.", nameof(password));
        if (password.Length > 512)
            throw new ArgumentException("PFX password is too long.", nameof(password));

        lock (_gate)
        {
            var serial = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN=DSPilot OPC UA Client {serial}, O=DualSoft",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(ClientAuthenticationOid, "Client Authentication") },
                critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var now = DateTimeOffset.UtcNow;
            using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
            var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
            var publicBytes = certificate.Export(X509ContentType.Cert);
            var pfxBytes = certificate.Export(X509ContentType.Pfx, password);

            EnsurePrivateDirectory(TrustedUserCertificateDirectory);
            WriteAtomically(
                Path.Combine(TrustedUserCertificateDirectory, $"{thumbprint}.der"),
                publicBytes);

            return new IssuedOpcUaClientCertificate(
                pfxBytes,
                $"dspilot-opcua-client-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pfx",
                thumbprint,
                certificate.NotAfter.ToUniversalTime());
        }
    }

    public IReadOnlyList<OpcUaRejectedCertificate> ListRejectedApplicationCertificates()
    {
        lock (_gate)
        {
            if (!Directory.Exists(RejectedApplicationCertificateDirectory))
                return Array.Empty<OpcUaRejectedCertificate>();

            var result = new List<OpcUaRejectedCertificate>();
            foreach (var path in Directory.EnumerateFiles(RejectedApplicationCertificateDirectory))
            {
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
                    result.Add(new OpcUaRejectedCertificate(
                        NormalizeThumbprint(certificate.Thumbprint),
                        certificate.Subject,
                        certificate.Issuer,
                        certificate.NotAfter.ToUniversalTime()));
                }
                catch (CryptographicException)
                {
                    // OPC UA 저장소의 인증서 파일만 노출한다. 손상/임시 파일은 목록에서 제외한다.
                }
            }

            return result
                .OrderBy(item => item.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Thumbprint, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool TrustRejectedApplicationCertificate(string thumbprint)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (normalized.Length != 40 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            return false;

        lock (_gate)
        {
            if (!Directory.Exists(RejectedApplicationCertificateDirectory))
                return false;

            foreach (var path in Directory.EnumerateFiles(RejectedApplicationCertificateDirectory))
            {
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
                    if (!string.Equals(
                            NormalizeThumbprint(certificate.Thumbprint),
                            normalized,
                            StringComparison.Ordinal))
                        continue;

                    EnsurePrivateDirectory(TrustedApplicationCertificateDirectory);
                    WriteAtomically(
                        Path.Combine(TrustedApplicationCertificateDirectory, $"{normalized}.der"),
                        certificate.Export(X509ContentType.Cert));
                    File.Delete(path);
                    return true;
                }
                catch (CryptographicException)
                {
                    // 다른 파일을 계속 검사한다.
                }
            }
            return false;
        }
    }

    private static string NormalizeThumbprint(string? value) =>
        new((value ?? string.Empty)
            .Where(Uri.IsHexDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var temp = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
