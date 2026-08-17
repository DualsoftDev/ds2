using System.Text.Json;
using System.Collections.Concurrent;
using Ds2.Backend.Plc;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Promaker.Shared;

internal sealed class OpcUaAidEndpointAdapter(
    AidSouthboundEndpointDescriptor endpoint,
    IAidTelemetrySink sink,
    IAidSecretResolver secretResolver,
    string dataRoot) : AidEndpointAdapter(endpoint, sink, secretResolver)
{
    private const int MaxCachedEventTypes = 2048;
    private readonly string _certificateRoot = Path.Combine(dataRoot, "opcua", SafeName(endpoint.Name));
    private readonly ConcurrentDictionary<NodeId, string> _eventTypeNames = new();

    internal static void ValidateConfiguration(AidSouthboundEndpointDescriptor endpoint)
    {
        foreach (var signal in endpoint.Signals) ValidateNodeHref(signal.Href, signal.SignalId);
        foreach (var descriptor in endpoint.Events)
        {
            ValidateNodeHref(descriptor.SourceNodeHref, descriptor.SignalId);
            if (string.IsNullOrWhiteSpace(descriptor.EventTypeSemanticId))
                throw new FormatException($"Event type is empty for signalId '{descriptor.SignalId}'.");
            if (!string.IsNullOrWhiteSpace(descriptor.PayloadPath)
                && descriptor.PayloadPath.Split('.', StringSplitOptions.RemoveEmptyEntries).Length == 0)
                throw new FormatException($"Event payloadPath is invalid for signalId '{descriptor.SignalId}'.");
        }
    }

    protected override void ValidateCredentials(AidCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.BearerToken)
            || !string.IsNullOrWhiteSpace(credentials.ClientId)
            || AidCredentialRules.HasHeaders(credentials))
            throw new InvalidOperationException(
                "AID OPC UA supports username/password credentials only; bearerToken, clientId, and headers are not supported.");
    }

    private static void ValidateNodeHref(string href, string signalId)
    {
        try
        {
            if (href.Contains("nsu=", StringComparison.OrdinalIgnoreCase))
                _ = ExpandedNodeId.Parse(href);
            else
                _ = NodeId.Parse(href);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid OPC UA NodeId '{href}' for signalId '{signalId}'.", ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Session? session = null;
            try
            {
                await RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                session = await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                failures = 0;
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.KeepAlive += (_, args) =>
                {
                    if (ServiceResult.IsBad(args.Status)) disconnected.TrySetResult();
                };
                using var subscription = CreateSubscription(session);
                session.AddSubscription(subscription);
                subscription.Create();
                ValidateMonitoredItems(subscription);
                await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new ServiceResultException(StatusCodes.BadNoCommunication, "OPC UA keep-alive failed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                SetAllQuality(StatusCodes.BadNoCommunication);
                Log.Warn($"AID OPC UA connection failed: endpoint={Endpoint.Name}: {ex.Message}");
                await Task.Delay(RetryDelay(failures), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (session is not null)
                {
                    try { await session.CloseAsync(cancellationToken).ConfigureAwait(false); } catch { }
                    session.Dispose();
                }
            }
        }
    }

    private async Task<Session> CreateSessionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_certificateRoot);
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "Promaker.Agent.AID",
            ApplicationUri = $"urn:dualsoft:promaker-agent:aid:{SafeName(Endpoint.Name)}",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(_certificateRoot, "own"),
                    SubjectName = $"CN=Promaker.Agent.AID.{SafeName(Endpoint.Name)}, O=DualSoft"
                },
                TrustedIssuerCertificates = TrustList("issuers"),
                TrustedPeerCertificates = TrustList("trusted"),
                RejectedCertificateStore = TrustList("rejected"),
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15_000,
                MaxStringLength = AidValueCodec.MaxUaScalarBytes,
                MaxByteStringLength = AidValueCodec.MaxUaScalarBytes,
                MaxArrayLength = 65_535,
                MaxMessageSize = 4_194_304,
                MaxBufferSize = 65_535,
            },
            CertificateValidator = new CertificateValidator()
        };
        await configuration.Validate(ApplicationType.Client).ConfigureAwait(false);
        var instance = new ApplicationInstance { ApplicationConfiguration = configuration };
        if (!await instance.CheckApplicationInstanceCertificates(silent: true).ConfigureAwait(false))
            throw new InvalidOperationException("AID OPC UA client certificate could not be created.");

        var useSecurity = !Endpoint.Security.Contains("none", StringComparison.OrdinalIgnoreCase);
        var description = CoreClientUtils.SelectEndpoint(configuration, Endpoint.BaseAddress, useSecurity, 15_000);
        if (useSecurity && description.SecurityMode != MessageSecurityMode.SignAndEncrypt)
            throw new InvalidOperationException("AID OPC UA endpoint did not offer SignAndEncrypt.");
        if (useSecurity
            && description.SecurityPolicyUri != SecurityPolicies.Basic256Sha256
            && description.SecurityPolicyUri != SecurityPolicies.Aes128_Sha256_RsaOaep
            && description.SecurityPolicyUri != SecurityPolicies.Aes256_Sha256_RsaPss)
            throw new InvalidOperationException(
                $"AID OPC UA endpoint selected an unsupported security policy '{description.SecurityPolicyUri}'.");
        var configured = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration));
        IUserIdentity identity = !string.IsNullOrWhiteSpace(Credentials.Username)
            ? new UserIdentity(Credentials.Username, Credentials.Password ?? "")
            : new UserIdentity(new AnonymousIdentityToken());
        var session = await Session.Create(
            configuration, configured, false, Endpoint.Name, 60_000u, identity, null,
            cancellationToken).ConfigureAwait(false);
        session.FetchNamespaceTables();
        return session;

        CertificateTrustList TrustList(string name) => new()
        {
            StoreType = "Directory",
            StorePath = Path.Combine(_certificateRoot, name)
        };
    }

    private Subscription CreateSubscription(Session session)
    {
        var publishing = Endpoint.Signals
            .Where(item => item.PublishingIntervalMs.HasValue)
            .Select(item => item.PublishingIntervalMs.GetValueOrDefault())
            .DefaultIfEmpty(1000)
            .Min();
        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = Endpoint.Name,
            PublishingInterval = Math.Max(10, publishing),
            KeepAliveCount = 10,
            LifetimeCount = 60,
            MaxNotificationsPerPublish = 1000,
            PublishingEnabled = true
        };
        foreach (var signal in Endpoint.Signals)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = signal.SignalId,
                StartNodeId = ResolveNodeId(session, signal.Href),
                AttributeId = Attributes.Value,
                SamplingInterval = signal.SamplingIntervalMs.HasValue ? signal.SamplingIntervalMs.Value : -1,
                QueueSize = (uint)(signal.QueueSize.HasValue ? Math.Max(1, signal.QueueSize.Value) : 10),
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
                Filter = DataFilter(signal)
            };
            item.Notification += (_, _) =>
            {
                foreach (var value in item.DequeueValues())
                {
                    try
                    {
                        var typed = AidValueCodec.ConvertScalar(value.Value, signal.ValueType);
                        var timestamp = value.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : value.SourceTimestamp;
                        Sink.Publish(signal, typed, timestamp, value.StatusCode.Code);
                    }
                    catch (InvalidDataException ex)
                    {
                        SetQuality(signal, StatusCodes.BadEncodingLimitsExceeded);
                        Log.Warn($"AID OPC UA value exceeds transport limits: signalId={signal.SignalId}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        SetQuality(signal, StatusCodes.BadTypeMismatch);
                        Log.Warn($"AID OPC UA value conversion failed: signalId={signal.SignalId}: {ex.Message}");
                    }
                }
            };
            subscription.AddItem(item);
        }

        foreach (var descriptor in Endpoint.Events)
        {
            var filter = EventFilter(session, descriptor);
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = descriptor.SignalId,
                StartNodeId = ResolveNodeId(session, descriptor.SourceNodeHref),
                AttributeId = Attributes.EventNotifier,
                QueueSize = 100,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
                Filter = filter
            };
            item.Notification += (_, _) =>
            {
                foreach (var fields in item.DequeueEvents()) PublishEvent(session, descriptor, fields);
            };
            subscription.AddItem(item);
        }
        return subscription;
    }

    private void ValidateMonitoredItems(Subscription subscription)
    {
        var valid = 0;
        foreach (var item in subscription.MonitoredItems)
        {
            var error = item.Status?.Error;
            if (error is null || ServiceResult.IsGood(error))
            {
                valid++;
                continue;
            }

            var signal = Endpoint.Signals.FirstOrDefault(candidate => candidate.SignalId == item.DisplayName);
            if (signal is not null) SetQuality(signal, error.StatusCode.Code);
            Log.Warn($"AID OPC UA monitored item rejected: endpoint={Endpoint.Name} " +
                     $"item={item.DisplayName} status={error.StatusCode}");
        }
        if (subscription.MonitoredItemCount > 0 && valid == 0)
            throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, "All AID OPC UA monitored items were rejected.");
    }

    private void PublishEvent(Session session, AidSouthboundEventDescriptor descriptor, EventFieldList fields)
    {
        try
        {
            var values = fields.EventFields;
            if (values.Count < 2 || values[1].Value is not NodeId eventType
                                 || !EventTypeMatches(session, descriptor.EventTypeSemanticId, eventType))
                return;
            var sourceTimestamp = values.Count > 4 && values[4].Value is DateTime time ? time : DateTime.UtcNow;
            var payload = new Dictionary<string, object?>
            {
                ["eventId"] = values.Count > 0 && values[0].Value is byte[] eventId ? Convert.ToHexString(eventId) : null,
                ["sourceName"] = values.Count > 3 ? values[3].Value : null,
                ["message"] = values.Count > 5
                    ? values[5].Value is LocalizedText text ? text.Text : values[5].Value
                    : null,
                ["severity"] = values.Count > 6 ? values[6].Value : null,
                ["value"] = values.Count > 7 ? values[7].Value : null
            };
            if (!Sink.PublishEvent(descriptor, sourceTimestamp, JsonSerializer.Serialize(payload)))
                Log.Warn($"AID OPC UA event was rejected by the UA projection: signalId={descriptor.SignalId}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"AID OPC UA event conversion failed: signalId={descriptor.SignalId}: {ex.Message}");
        }
    }

    private bool EventTypeMatches(Session session, string expectedSemanticId, NodeId eventType)
    {
        if (expectedSemanticId.Contains("nsu=", StringComparison.OrdinalIgnoreCase))
        {
            var expected = ExpandedNodeId.ToNodeId(ExpandedNodeId.Parse(expectedSemanticId), session.NamespaceUris);
            return expected is not null && expected.Equals(eventType);
        }
        if (expectedSemanticId.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)
            || expectedSemanticId.StartsWith("i=", StringComparison.OrdinalIgnoreCase)
            || expectedSemanticId.StartsWith("s=", StringComparison.OrdinalIgnoreCase)
            || expectedSemanticId.StartsWith("g=", StringComparison.OrdinalIgnoreCase))
            return NodeId.Parse(expectedSemanticId).Equals(eventType);

        var expectedName = expectedSemanticId[(expectedSemanticId.LastIndexOfAny(['/', ':', '#']) + 1)..];
        if (!_eventTypeNames.TryGetValue(eventType, out var actualName))
        {
            actualName = session.ReadNode(eventType).BrowseName.Name;
            if (_eventTypeNames.Count < MaxCachedEventTypes)
                _eventTypeNames.TryAdd(eventType, actualName);
        }
        return string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase);
    }

    private static EventFilter EventFilter(Session session, AidSouthboundEventDescriptor descriptor)
    {
        var filter = new EventFilter();
        foreach (var browseName in new[]
                 {
                     BrowseNames.EventId, BrowseNames.EventType, BrowseNames.SourceNode, BrowseNames.SourceName,
                     BrowseNames.Time, BrowseNames.Message, BrowseNames.Severity
                 })
            filter.AddSelectClause(ObjectTypeIds.BaseEventType, browseName);
        if (!string.IsNullOrWhiteSpace(descriptor.PayloadPath))
        {
            var sourceNamespace = ResolveNodeId(session, descriptor.SourceNodeHref).NamespaceIndex;
            var operand = new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.BaseEventType,
                AttributeId = Attributes.Value,
                BrowsePath = new QualifiedNameCollection(
                    descriptor.PayloadPath.Split('.', StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => new QualifiedName(part, sourceNamespace)))
            };
            filter.SelectClauses.Add(operand);
        }
        return filter;
    }

    private static DataChangeFilter? DataFilter(AidSouthboundSignalDescriptor signal)
    {
        // Percent deadband requires EURange on the source server and is therefore applied on
        // the central DS2 UA node/Collector instead. Pushing it to an arbitrary source often
        // makes the external server reject the monitored item.
        if (!signal.DeadbandAbsolute.HasValue) return null;
        var filter = new DataChangeFilter
        {
            Trigger = DataChangeTrigger.StatusValueTimestamp,
            DeadbandType = (uint)DeadbandType.Absolute,
            DeadbandValue = signal.DeadbandAbsolute.GetValueOrDefault()
        };
        return filter;
    }

    private static NodeId ResolveNodeId(Session session, string href)
    {
        if (href.Contains("nsu=", StringComparison.OrdinalIgnoreCase))
            return ExpandedNodeId.ToNodeId(ExpandedNodeId.Parse(href), session.NamespaceUris)
                   ?? throw new FormatException($"Unknown OPC UA namespace in '{href}'.");
        return NodeId.Parse(href);
    }

    private static string SafeName(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
}
