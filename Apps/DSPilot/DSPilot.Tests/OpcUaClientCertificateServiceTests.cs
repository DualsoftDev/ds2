// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

public sealed class OpcUaClientCertificateServiceTests
{
    [Fact]
    public void IssueUserCertificate_returns_password_protected_pfx_and_trusts_only_public_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "dspilot-opcua-cert-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new OpcUaClientCertificateService(root);
            const string password = "field-test-password";

            var issued = service.IssueUserCertificate(password);

            using var fromPfx = X509CertificateLoader.LoadPkcs12(
                issued.PfxBytes,
                password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            Assert.True(fromPfx.HasPrivateKey);
            Assert.Equal(issued.Thumbprint, fromPfx.Thumbprint);
            Assert.Contains(fromPfx.Extensions.OfType<X509EnhancedKeyUsageExtension>(), extension =>
                extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

            var trustedPath = Path.Combine(
                service.TrustedUserCertificateDirectory,
                issued.Thumbprint + ".der");
            Assert.True(File.Exists(trustedPath));
            using var trusted = X509CertificateLoader.LoadCertificateFromFile(trustedPath);
            Assert.False(trusted.HasPrivateKey);
            Assert.Equal(issued.Thumbprint, trusted.Thumbprint);
            Assert.ThrowsAny<CryptographicException>(() =>
                X509CertificateLoader.LoadPkcs12(issued.PfxBytes, "wrong-password"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustRejectedApplicationCertificate_moves_matching_public_certificate_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "dspilot-opcua-trust-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new OpcUaClientCertificateService(root);
            Directory.CreateDirectory(service.RejectedApplicationCertificateDirectory);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Softing OPC UA Client",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(30));
            var rejectedPath = Path.Combine(service.RejectedApplicationCertificateDirectory, "softing.der");
            File.WriteAllBytes(rejectedPath, certificate.Export(X509ContentType.Cert));

            var listed = Assert.Single(service.ListRejectedApplicationCertificates());
            Assert.Contains("Softing OPC UA Client", listed.Subject);
            Assert.True(service.TrustRejectedApplicationCertificate(listed.Thumbprint));
            Assert.False(File.Exists(rejectedPath));
            Assert.True(File.Exists(Path.Combine(
                service.TrustedApplicationCertificateDirectory,
                listed.Thumbprint + ".der")));
            Assert.Empty(service.ListRejectedApplicationCertificates());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
