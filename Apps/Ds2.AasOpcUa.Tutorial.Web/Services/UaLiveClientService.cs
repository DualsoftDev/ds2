using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using UaStatus = Opc.Ua.StatusCodes;

namespace Ds2.AasOpcUa.Tutorial.Web.Services;

/// <summary>
/// UA client Session 하나를 재사용해서 여러 페이지에 값을 노출.
/// Phase 3 스모크에서 사용한 것과 동일 패턴.
/// </summary>
public sealed class UaLiveClientService : IAsyncDisposable
{
    private Session? _session;
    private ApplicationConfiguration? _config;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string EndpointUrl { get; private set; } = "";
    public bool IsConnected => _session is { Connected: true };

    public async Task<string> ConnectAsync(string endpointUrl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_session is { Connected: true } && EndpointUrl == endpointUrl) return "이미 연결됨";
            await DisconnectInternalAsync();

            EndpointUrl = endpointUrl;
            _config = BuildClientConfig();
            await _config.Validate(ApplicationType.Client);
            var appInstance = new ApplicationInstance { ApplicationConfiguration = _config };
            var certOk = await appInstance.CheckApplicationInstanceCertificates(silent: true);
            if (!certOk) return "인증서 발급 실패";

            var ed = await Task.Run(
                () => CoreClientUtils.SelectEndpoint(_config, endpointUrl, useSecurity: false, discoverTimeout: 5_000), ct);
            var epCfg = EndpointConfiguration.Create(_config);
            var endpoint = new ConfiguredEndpoint(null, ed, epCfg);
            _session = await Session.Create(
                _config, endpoint, false,
                "Ds2.Tutorial.Web",
                60_000,
                new UserIdentity(new AnonymousIdentityToken()),
                null);

            _session.FetchNamespaceTables();
            return $"연결됨 · endpoint={endpointUrl}, session={_session.SessionId}";
        }
        catch (Exception ex)
        {
            return $"연결 실패: {ex.Message}";
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> DisconnectAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
            return "연결 해제";
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DisconnectInternalAsync()
    {
        if (_session != null)
        {
            try
            {
                await _session.CloseAsync();
                _session.Dispose();
            }
            catch { /* ignore */ }
            _session = null;
        }
    }

    public IReadOnlyList<string> NamespaceUris()
    {
        if (_session == null) return Array.Empty<string>();
        var t = _session.NamespaceUris;
        var list = new List<string>(t.Count);
        for (uint i = 0; i < t.Count; i++)
        {
            var v = t.GetString((ushort)i);
            if (!string.IsNullOrEmpty(v)) list.Add(v);
        }
        return list;
    }

    public (int, double?, uint) ReadValue(string globalAssetIdUrn, string signalId)
    {
        if (_session == null) return (-1, null, UaStatus.BadSessionClosed);
        // Server-side namespace URI = urn:ds:asset:{Base64Url(globalAssetId)}.
        var enc = Ds2.Core.Encoding.Base64Url.encode(globalAssetIdUrn);
        var uri = $"urn:ds:asset:{enc}";
        var idx = _session.NamespaceUris.GetIndex(uri);
        if (idx < 0) return (-1, null, UaStatus.BadNodeIdUnknown);

        var nodeId = new NodeId(signalId, (ushort)idx);
        var ids = new ReadValueIdCollection { new() { NodeId = nodeId, AttributeId = Attributes.Value } };
        var results = new DataValueCollection();
        var diagnostics = new DiagnosticInfoCollection();
        _session.Read(null, 0.0, TimestampsToReturn.Both, ids, out results, out diagnostics);
        var status = results[0].StatusCode.Code;
        var val = results[0].Value as double?;
        return (idx, val, status);
    }

    public async Task<(bool ok, string message)> RaiseAssetEventAsync(
        string globalAssetIdUrn,
        string eventTypeSemanticId,
        string signalId,
        string payloadJson)
    {
        if (_session == null) return (false, "세션 없음");
        try
        {
            var enc = Ds2.Core.Encoding.Base64Url.encode(globalAssetIdUrn);
            var uri = $"urn:ds:asset:{enc}";
            var idx = _session.NamespaceUris.GetIndex(uri);
            if (idx < 0) return (false, $"자산 namespace 미등록: {uri}");

            var eventsObj = new NodeId("Events", (ushort)idx);
            var methodNode = new NodeId("Events/RaiseAssetEvent", (ushort)idx);
            var args = new object[]
            {
                eventTypeSemanticId,
                signalId,
                DateTime.UtcNow,
                payloadJson
            };
            var outputs = await Task.Run(() => _session.Call(eventsObj, methodNode, args));
            return (true, $"outputs.Count={outputs?.Count ?? 0}");
        }
        catch (ServiceResultException ex)
        {
            return (false, $"{ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static ApplicationConfiguration BuildClientConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds2-tutorial-client");
        Directory.CreateDirectory(root);
        return new ApplicationConfiguration
        {
            ApplicationName = "Ds2.Tutorial.Web",
            ApplicationUri = "urn:dualsoft:tutorial:web",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(root, "own"),
                    SubjectName = "CN=Ds2.Tutorial.Web, O=DualSoft"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(root, "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(root, "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(root, "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            ClientConfiguration = new ClientConfiguration(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            CertificateValidator = new CertificateValidator()
        };
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _lock.Dispose();
    }
}
