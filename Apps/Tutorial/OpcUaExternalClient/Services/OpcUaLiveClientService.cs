using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using UaStatusCodes = Opc.Ua.StatusCodes;

namespace Ds2.Tutorial.OpcUaExternalClient.Web.Services;

public sealed record UaConnectionResult(bool Success, string Message);

public sealed record UaServerCertificateInfo(
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter);

public sealed record UaVariableNode(
    string NodeId,
    string BrowsePath,
    string DisplayName,
    string Value,
    string StatusCode,
    DateTime? SourceTimestamp);

public sealed record UaLiveValue(
    long Sequence,
    DateTime ReceivedAt,
    string NodeId,
    string BrowsePath,
    string Value,
    string StatusCode,
    DateTime? SourceTimestamp,
    DateTime? ServerTimestamp);

/// <summary>
/// DSPilot 발급 PFX를 User Identity로 사용해 Agent의 secure OPC UA endpoint에 접속하고
/// 실제 MonitoredItem notification을 Web UI로 전달한다.
/// </summary>
public sealed class OpcUaLiveClientService : IAsyncDisposable
{
    private const int MaxBrowseNodes = 1_000;
    private const int MaxHistoryRows = 300;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _certificateRoot;
    private readonly Dictionary<string, MonitoredItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UaLiveValue> _latest = new(StringComparer.Ordinal);
    private readonly List<UaLiveValue> _history = [];

    private ApplicationConfiguration? _configuration;
    private Session? _session;
    private Subscription? _subscription;
    private X509Certificate2? _applicationCertificate;
    private X509Certificate2? _userCertificate;
    private byte[]? _pendingServerCertificateRaw;
    private long _sequence;

    public OpcUaLiveClientService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = AppContext.BaseDirectory;
        _certificateRoot = Path.Combine(
            localData,
            "DualSoft",
            "Tutorial",
            "OpcUaExternalClient",
            "certs");
    }

    public event Action? StateChanged;

    public bool IsConnected => _session is { Connected: true };
    public string EndpointUrl { get; private set; } = "";
    public string AdvertisedEndpointUrl { get; private set; } = "-";
    public string EffectiveEndpointUrl { get; private set; } = "-";
    public string Status { get; private set; } = "연결되지 않음";
    public string ApplicationCertificateSubject { get; private set; } = "-";
    public string ApplicationCertificateThumbprint { get; private set; } = "-";
    public UaServerCertificateInfo? PendingServerCertificate { get; private set; }

    public IReadOnlyList<UaLiveValue> LatestValues
    {
        get
        {
            lock (_stateGate)
                return _latest.Values.OrderBy(value => value.BrowsePath, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<UaLiveValue> History
    {
        get
        {
            lock (_stateGate)
                return _history.ToArray();
        }
    }

    public IReadOnlyCollection<string> SubscribedNodeIds
    {
        get
        {
            lock (_stateGate)
                return _items.Keys.ToArray();
        }
    }

    public async Task<UaConnectionResult> ConnectAsync(
        string endpointUrl,
        byte[] pfxBytes,
        string pfxPassword,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri)
                || !endpointUri.Scheme.Equals("opc.tcp", StringComparison.OrdinalIgnoreCase))
                return Fail("Endpoint는 opc.tcp:// 절대 주소여야 합니다.");

            await DisconnectInternalAsync(disposeIdentity: true);
            EndpointUrl = endpointUrl.Trim();
            AdvertisedEndpointUrl = "-";
            EffectiveEndpointUrl = EndpointUrl;
            PendingServerCertificate = null;
            _pendingServerCertificateRaw = null;
            Status = "PFX 확인 중";
            NotifyStateChanged();

            try
            {
                _userCertificate = X509CertificateLoader.LoadPkcs12(
                    pfxBytes,
                    pfxPassword,
                    X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (Exception ex)
            {
                return Fail($"PFX를 열 수 없습니다: {ex.Message}");
            }

            if (!_userCertificate.HasPrivateKey)
                return Fail("선택한 PFX에 사용자 인증용 개인키가 없습니다.");

            _configuration = BuildClientConfiguration();
            await _configuration.Validate(ApplicationType.Client);
            _configuration.CertificateValidator.CertificateValidation += OnServerCertificateValidation;

            var instance = new ApplicationInstance { ApplicationConfiguration = _configuration };
            var certificateOk = await instance.CheckApplicationInstanceCertificates(silent: true);
            if (!certificateOk)
                return Fail("클라이언트 Application Certificate를 만들거나 읽을 수 없습니다.");

            _applicationCertificate = await _configuration.SecurityConfiguration.ApplicationCertificate.Find(true);
            if (_applicationCertificate is null)
                return Fail("클라이언트 Application Certificate가 없습니다.");
            ApplicationCertificateSubject = _applicationCertificate.Subject;
            ApplicationCertificateThumbprint = _applicationCertificate.Thumbprint;

            Status = "보안 endpoint 확인 중";
            NotifyStateChanged();
            var description = await Task.Run(
                () => CoreClientUtils.SelectEndpoint(_configuration, EndpointUrl, useSecurity: true, discoverTimeout: 15_000),
                cancellationToken);
            AdvertisedEndpointUrl = description.EndpointUrl;

            if (description.SecurityMode != MessageSecurityMode.SignAndEncrypt
                || description.SecurityPolicyUri != SecurityPolicies.Basic256Sha256)
                return Fail(
                    $"서버가 요구 보안을 선택하지 않았습니다: {description.SecurityMode} / {description.SecurityPolicyUri}");

            // NAT·reverse proxy·외부 공인 IP 구성에서는 GetEndpoints 응답이 localhost 또는 내부 hostname을
            // 광고할 수 있다. 보안정책과 ServerCertificate는 discovery 결과를 그대로 검증하되,
            // 실제 SecureChannel은 사용자가 입력해 TCP 도달이 확인된 endpoint로 연다.
            description.EndpointUrl = EndpointUrl;
            EffectiveEndpointUrl = description.EndpointUrl;

            var endpoint = new ConfiguredEndpoint(
                null,
                description,
                EndpointConfiguration.Create(_configuration));
            var identity = new UserIdentity(_userCertificate);

            Status = "보안 세션 연결 중";
            NotifyStateChanged();
            _session = await Session.Create(
                _configuration,
                endpoint,
                false,
                "Ds2 OPC UA External Client Tutorial",
                60_000u,
                identity,
                null,
                cancellationToken);
            _session.KeepAlive += OnKeepAlive;
            _session.FetchNamespaceTables();

            Status = $"연결됨 · Session {_session.SessionId}";
            NotifyStateChanged();
            return new UaConnectionResult(true, Status);
        }
        catch (ServiceResultException ex)
        {
            var code = ex.StatusCode;
            var detail = ExceptionChain(ex);
            if (PendingServerCertificate is not null)
                return Fail($"0x{code:X8}: Agent 서버 인증서가 아직 신뢰되지 않았습니다. 아래 인증서를 확인하고 신뢰한 뒤 다시 연결하세요. · {detail}");
            if (code == UaStatusCodes.BadNotConnected
                || detail.Contains("BadNotConnected", StringComparison.OrdinalIgnoreCase))
                return Fail($"0x{code:X8} BadNotConnected: UA SecureChannel을 열지 못했습니다. 이 단계는 인증서 승인 대기 목록에 들어가기 전이므로 목록이 비어 있는 것이 정상입니다. 요청={EndpointUrl}, 광고={AdvertisedEndpointUrl}, 연결={EffectiveEndpointUrl} · {detail}");
            if (code == UaStatusCodes.BadCertificateUntrusted
                || code == UaStatusCodes.BadSecurityChecksFailed
                || code == UaStatusCodes.BadSecureChannelClosed)
                return Fail($"0x{code:X8}: DSPilot의 접속 승인 대기 인증서에서 Application Certificate {ApplicationCertificateThumbprint}를 승인한 뒤 다시 연결하세요. · {detail}");
            return Fail($"0x{code:X8}: {detail}");
        }
        catch (Exception ex)
        {
            return Fail($"연결 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UaConnectionResult> TrustPendingServerCertificateAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pendingServerCertificateRaw is null || PendingServerCertificate is null)
                return new UaConnectionResult(false, "신뢰 대기 중인 서버 인증서가 없습니다.");

            using var certificate = X509CertificateLoader.LoadCertificate(_pendingServerCertificateRaw);
            var trustedStore = new CertificateStoreIdentifier
            {
                StoreType = "Directory",
                StorePath = Path.Combine(_certificateRoot, "trusted")
            };
            await X509Utils.AddToStoreAsync(certificate, trustedStore, null, cancellationToken);
            var thumbprint = certificate.Thumbprint;
            PendingServerCertificate = null;
            _pendingServerCertificateRaw = null;
            Status = $"서버 인증서 신뢰 완료 · {thumbprint}";
            NotifyStateChanged();
            return new UaConnectionResult(true, Status);
        }
        catch (Exception ex)
        {
            return Fail($"서버 인증서 신뢰 저장 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<UaVariableNode>> BrowseVariablesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            var queue = new Queue<(NodeId NodeId, string Path, int Depth)>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var variables = new List<UaVariableNode>();
            queue.Enqueue((ObjectIds.ObjectsFolder, "Objects", 0));

            while (queue.Count > 0 && variables.Count < MaxBrowseNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                if (!visited.Add(current.NodeId.ToString()) || current.Depth > 12) continue;

                foreach (var reference in BrowseChildren(session, current.NodeId))
                {
                    var childId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                    if (childId is null) continue;
                    var name = string.IsNullOrWhiteSpace(reference.DisplayName?.Text)
                        ? reference.BrowseName?.Name ?? childId.ToString()
                        : reference.DisplayName.Text;
                    var path = $"{current.Path}/{name}";

                    if (reference.NodeClass == NodeClass.Object)
                    {
                        queue.Enqueue((childId, path, current.Depth + 1));
                    }
                    else if (reference.NodeClass == NodeClass.Variable)
                    {
                        var value = ReadValue(session, childId);
                        variables.Add(new UaVariableNode(
                            childId.ToString(),
                            path,
                            name,
                            FormatValue(value.Value),
                            value.StatusCode.ToString(),
                            NormalizeTimestamp(value.SourceTimestamp)));
                    }
                }
            }

            var assetVariables = variables
                .Where(variable => variable.BrowsePath.Contains("/DS/Assets/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(variable => variable.BrowsePath, StringComparer.Ordinal)
                .ToArray();
            return assetVariables.Length > 0
                ? assetVariables
                : variables.OrderBy(variable => variable.BrowsePath, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UaConnectionResult> SubscribeAsync(
        UaVariableNode node,
        int samplingIntervalMs,
        int publishingIntervalMs,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            lock (_stateGate)
            {
                if (_items.ContainsKey(node.NodeId))
                    return new UaConnectionResult(true, "이미 구독 중인 Variable입니다.");
            }

            var item = new MonitoredItem(
                _subscription?.DefaultItem ?? session.DefaultSubscription.DefaultItem)
            {
                DisplayName = node.BrowsePath,
                StartNodeId = NodeId.Parse(node.NodeId),
                AttributeId = Attributes.Value,
                SamplingInterval = Math.Clamp(samplingIntervalMs, 10, 60_000),
                QueueSize = 100,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting
            };
            item.Notification += (_, _) => OnValueNotification(item, node);

            if (_subscription is null)
            {
                _subscription = new Subscription(session.DefaultSubscription)
                {
                    DisplayName = "Ds2 Tutorial Live Values",
                    PublishingInterval = Math.Clamp(publishingIntervalMs, 50, 60_000),
                    KeepAliveCount = 10,
                    LifetimeCount = 100,
                    MaxNotificationsPerPublish = 1_000,
                    PublishingEnabled = true
                };
                _subscription.AddItem(item);
                session.AddSubscription(_subscription);
                _subscription.Create();
            }
            else
            {
                _subscription.AddItem(item);
                _subscription.ApplyChanges();
            }

            if (ServiceResult.IsBad(item.Status.Error))
            {
                _subscription.RemoveItem(item);
                _subscription.ApplyChanges();
                return new UaConnectionResult(false, $"MonitoredItem 생성 실패: {item.Status.Error}");
            }

            lock (_stateGate) _items[node.NodeId] = item;
            Status = $"구독 중 · {_items.Count} Variable";
            NotifyStateChanged();
            return new UaConnectionResult(true, $"구독 시작: {node.BrowsePath}");
        }
        catch (Exception ex)
        {
            return Fail($"구독 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UaConnectionResult> UnsubscribeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            MonitoredItem? item;
            lock (_stateGate)
            {
                if (!_items.Remove(nodeId, out item))
                    return new UaConnectionResult(false, "구독 중인 NodeId가 아닙니다.");
                _latest.Remove(nodeId);
            }

            if (_subscription is not null && item is not null)
            {
                _subscription.RemoveItem(item);
                _subscription.ApplyChanges();
            }

            Status = $"구독 중 · {_items.Count} Variable";
            NotifyStateChanged();
            return new UaConnectionResult(true, "구독 해제 완료");
        }
        catch (Exception ex)
        {
            return Fail($"구독 해제 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectInternalAsync(disposeIdentity: true);
            Status = "연결 해제";
            NotifyStateChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    private ApplicationConfiguration BuildClientConfiguration()
    {
        Directory.CreateDirectory(_certificateRoot);
        return new ApplicationConfiguration
        {
            ApplicationName = "Ds2 OPC UA External Client Tutorial",
            ApplicationUri = $"urn:{Dns.GetHostName()}:DualSoft:Ds2Tutorial:OpcUaExternalClient",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(_certificateRoot, "own"),
                    SubjectName = "CN=Ds2.OpcUa.ExternalClient.Tutorial, O=DualSoft"
                },
                TrustedIssuerCertificates = TrustList("issuers"),
                TrustedPeerCertificates = TrustList("trusted"),
                RejectedCertificateStore = TrustList("rejected"),
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60_000
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15_000,
                MaxMessageSize = 4 * 1024 * 1024,
                MaxByteStringLength = 4 * 1024 * 1024,
                MaxStringLength = 1024 * 1024
            },
            CertificateValidator = new CertificateValidator()
        };
    }

    private CertificateTrustList TrustList(string name) => new()
    {
        StoreType = "Directory",
        StorePath = Path.Combine(_certificateRoot, name)
    };

    private void OnServerCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs args)
    {
        if (StatusCode.IsGood(args.Error.StatusCode)) return;
        var certificate = args.Certificate;
        _pendingServerCertificateRaw = certificate.RawData.ToArray();
        PendingServerCertificate = new UaServerCertificateInfo(
            certificate.Subject,
            certificate.Issuer,
            certificate.Thumbprint,
            certificate.NotBefore,
            certificate.NotAfter);
        args.Accept = false;
        NotifyStateChanged();
    }

    private void OnKeepAlive(Opc.Ua.Client.ISession session, KeepAliveEventArgs args)
    {
        if (ServiceResult.IsBad(args.Status))
        {
            Status = $"연결 품질 오류 · {args.Status}";
            NotifyStateChanged();
        }
    }

    private void OnValueNotification(MonitoredItem item, UaVariableNode node)
    {
        foreach (var value in item.DequeueValues())
        {
            var update = new UaLiveValue(
                Interlocked.Increment(ref _sequence),
                DateTime.Now,
                node.NodeId,
                node.BrowsePath,
                FormatValue(value.Value),
                value.StatusCode.ToString(),
                NormalizeTimestamp(value.SourceTimestamp),
                NormalizeTimestamp(value.ServerTimestamp));

            lock (_stateGate)
            {
                _latest[node.NodeId] = update;
                _history.Insert(0, update);
                if (_history.Count > MaxHistoryRows)
                    _history.RemoveRange(MaxHistoryRows, _history.Count - MaxHistoryRows);
            }
        }
        NotifyStateChanged();
    }

    private static IReadOnlyList<ReferenceDescription> BrowseChildren(Session session, NodeId nodeId)
    {
        var descriptions = new BrowseDescriptionCollection
        {
            new()
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            }
        };
        session.Browse(
            null,
            null,
            0u,
            descriptions,
            out var results,
            out _);
        if (results.Count == 0 || StatusCode.IsBad(results[0].StatusCode))
            return [];
        return results[0].References.ToArray();
    }

    private static DataValue ReadValue(Session session, NodeId nodeId)
    {
        try
        {
            return session.ReadValue(nodeId);
        }
        catch (ServiceResultException ex)
        {
            return new DataValue(new StatusCode(ex.StatusCode));
        }
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "(null)";
        if (value is byte[] bytes) return Convert.ToHexString(bytes);
        if (value is Array array)
            return "[" + string.Join(", ", array.Cast<object?>().Select(FormatValue)) + "]";
        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? "(null)"
            : value.ToString() ?? "(null)";
    }

    private static DateTime? NormalizeTimestamp(DateTime timestamp) =>
        timestamp == DateTime.MinValue ? null : timestamp.ToLocalTime();

    private static string ExceptionChain(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null && messages.Count < 6; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
                messages.Add(current.Message.Trim());
        }
        return string.Join(" → ", messages);
    }

    private Session RequireSession() =>
        _session is { Connected: true } session
            ? session
            : throw new InvalidOperationException("먼저 Agent OPC UA 서버에 연결하세요.");

    private UaConnectionResult Fail(string message)
    {
        Status = message;
        NotifyStateChanged();
        return new UaConnectionResult(false, message);
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { /* UI circuit가 종료되는 순간의 callback은 무시한다. */ }
    }

    private async Task DisconnectInternalAsync(bool disposeIdentity)
    {
        if (_subscription is not null)
        {
            try { _subscription.Delete(true); }
            catch { /* 연결 단절 상태에서는 서버 delete가 실패할 수 있다. */ }
            _subscription.Dispose();
            _subscription = null;
        }

        lock (_stateGate)
        {
            _items.Clear();
            _latest.Clear();
            _history.Clear();
        }

        if (_session is not null)
        {
            _session.KeepAlive -= OnKeepAlive;
            try { await _session.CloseAsync(); }
            catch { /* 이미 끊긴 세션은 닫기 오류를 무시한다. */ }
            _session.Dispose();
            _session = null;
        }

        if (_configuration is not null)
        {
            _configuration.CertificateValidator.CertificateValidation -= OnServerCertificateValidation;
            _configuration = null;
        }

        _applicationCertificate?.Dispose();
        _applicationCertificate = null;

        if (disposeIdentity)
        {
            _userCertificate?.Dispose();
            _userCertificate = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await DisconnectInternalAsync(disposeIdentity: true); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
