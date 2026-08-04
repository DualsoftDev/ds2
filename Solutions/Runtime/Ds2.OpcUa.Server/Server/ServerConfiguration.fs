namespace Ds2.OpcUa.Server.Server

open System
open System.Collections.Generic
open System.IO
open Opc.Ua
open Opc.Ua.Configuration

/// ADR-005 · TLS + mTLS 준비 · dev 모드에서는 Anonymous / self-signed cert.
/// 실 프로덕션은 Vault PKI (Phase 7) 로 이관.
type DsServerConfig = {
    ApplicationName : string
    ApplicationUri  : string
    ProductUri      : string
    EndpointUrl     : string
    CertificateDir  : string
    /// dev 편의 · Anonymous 세션 허용 여부
    AllowAnonymous  : bool
    /// MessageSecurityMode.None endpoint 허용 여부. 운영에서는 false.
    AllowUnsecuredEndpoint : bool
    /// 미등록 peer 인증서 자동 신뢰. 운영에서는 false이고 trusted store를 명시 관리한다.
    AutoAcceptUntrustedCertificates : bool
    MaxSessionCount : int
    SessionTimeoutMs : int
    MinSamplingIntervalMs : int
}

module ServerConfiguration =

    let defaultConfig root =
        // Windows 에서 0.0.0.0 은 관리자 권한 요구.
        // Dev/local 은 localhost, 프로덕션·Docker 는 DS2_UASERVER_ENDPOINT env 로 오버라이드.
        // Default 48400 (privileged port 4840 회피 · WSL2/Windows 개발 환경 우호).
        let endpoint =
            match Environment.GetEnvironmentVariable "DS2_UASERVER_ENDPOINT" with
            | null | "" -> "opc.tcp://localhost:48400"
            | v -> v
        let insecureDev =
            match Boolean.TryParse(Environment.GetEnvironmentVariable "DS2_UASERVER_INSECURE_DEV") with
            | true, true -> true
            | _ -> false
        if insecureDev then
            let uri = Uri(endpoint, UriKind.Absolute)
            if not uri.IsLoopback then
                invalidOp "DS2_UASERVER_INSECURE_DEV is restricted to a loopback endpoint."
        {
            ApplicationName = "Ds2.OpcUa.Server"
            ApplicationUri = "urn:dualsoft:opcua:server"
            ProductUri     = "https://dualsoft.com/ds2/opcua"
            EndpointUrl    = endpoint
            CertificateDir = Path.Combine(root, "certs")
            AllowAnonymous = insecureDev
            AllowUnsecuredEndpoint = insecureDev
            AutoAcceptUntrustedCertificates = insecureDev
            MaxSessionCount = 100
            SessionTimeoutMs = 60_000
            MinSamplingIntervalMs = 100
        }

    /// Programmatic ApplicationConfiguration (Config XML 없이).
    /// 개발용 · 프로덕션은 Config XML 로 이관 권장.
    let build (cfg: DsServerConfig) : ApplicationConfiguration =
        Directory.CreateDirectory cfg.CertificateDir |> ignore

        let securityConfig =
            SecurityConfiguration(
                ApplicationCertificate = CertificateIdentifier(
                    StoreType = "Directory",
                    StorePath = Path.Combine(cfg.CertificateDir, "own"),
                    SubjectName = "CN=Ds2.OpcUa.Server, O=DualSoft, DC=localhost"),
                TrustedIssuerCertificates = CertificateTrustList(
                    StoreType = "Directory",
                    StorePath = Path.Combine(cfg.CertificateDir, "issuers")),
                TrustedPeerCertificates = CertificateTrustList(
                    StoreType = "Directory",
                    StorePath = Path.Combine(cfg.CertificateDir, "trusted")),
                RejectedCertificateStore = CertificateTrustList(
                    StoreType = "Directory",
                    StorePath = Path.Combine(cfg.CertificateDir, "rejected")),
                AutoAcceptUntrustedCertificates = cfg.AutoAcceptUntrustedCertificates,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048us,
                AddAppCertToTrustedStore = true)

        // Server transport settings — opc.tcp binary policy 만 (HTTPS 은 후속).
        let serverConfig =
            ServerConfiguration(
                BaseAddresses = StringCollection([| cfg.EndpointUrl |]),
                SecurityPolicies = ServerSecurityPolicyCollection([|
                    if cfg.AllowUnsecuredEndpoint then
                        // 명시적인 개발 모드에서만 None endpoint를 연다.
                        ServerSecurityPolicy(
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None)
                    ServerSecurityPolicy(
                        SecurityMode = MessageSecurityMode.Sign,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256)
                    ServerSecurityPolicy(
                        SecurityMode = MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256)
                |]),
                UserTokenPolicies = UserTokenPolicyCollection([|
                    if cfg.AllowAnonymous then
                        UserTokenPolicy(UserTokenType.Anonymous,
                            SecurityPolicyUri = SecurityPolicies.None)
                    // Username/password is not advertised until an explicit credential validator exists.
                    UserTokenPolicy(UserTokenType.Certificate,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256)
                |]),
                DiagnosticsEnabled = true,
                MaxSessionCount = cfg.MaxSessionCount,
                MaxSessionTimeout = cfg.SessionTimeoutMs,
                MinSessionTimeout = min 10_000 cfg.SessionTimeoutMs,
                MaxBrowseContinuationPoints = 10,
                MaxQueryContinuationPoints = 10,
                MaxHistoryContinuationPoints = 100,
                MaxRequestAge = 600_000,
                MinPublishingInterval = cfg.MinSamplingIntervalMs,
                MaxPublishingInterval = 3_600_000,
                PublishingResolution = cfg.MinSamplingIntervalMs,
                MaxSubscriptionLifetime = 3_600_000,
                MaxMessageQueueSize = 100,
                MaxNotificationQueueSize = 100,
                MaxNotificationsPerPublish = 1000,
                MinMetadataSamplingInterval = cfg.MinSamplingIntervalMs,
                AvailableSamplingRates = SamplingRateGroupCollection([|
                    SamplingRateGroup(Start = 5.0, Increment = 5.0, Count = 20)
                |]))

        ApplicationConfiguration(
            ApplicationName = cfg.ApplicationName,
            ApplicationUri = cfg.ApplicationUri,
            ProductUri = cfg.ProductUri,
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = securityConfig,
            ServerConfiguration = serverConfig,
            TransportQuotas = TransportQuotas(
                OperationTimeout = 15_000,
                MaxStringLength = 1_048_576,
                MaxByteStringLength = 1_048_576,
                MaxArrayLength = 65_535,
                MaxMessageSize = 4_194_304,
                MaxBufferSize = 65_535,
                ChannelLifetime = 300_000,
                SecurityTokenLifetime = 3_600_000),
            TraceConfiguration = TraceConfiguration(
                DeleteOnLoad = true,
                OutputFilePath = Path.Combine(cfg.CertificateDir, "..", "logs", "opcua.log"),
                TraceMasks = 519),
            CertificateValidator = CertificateValidator())

    /// Config 검증 · 인증서 자동 발급 (없으면).
    let validateAndPrepare (config: ApplicationConfiguration) : System.Threading.Tasks.Task<bool> =
        task {
            do! config.Validate ApplicationType.Server
            // 기존 인증서는 검증하고, 없으면 설치별 self-signed 인증서를 생성한다.
            // task 내부의 Async.RunSynchronously를 제거해 UI SynchronizationContext 교착을 방지한다.
            let appInstance = ApplicationInstance(ApplicationConfiguration = config)
            return! appInstance.CheckApplicationInstanceCertificates(silent = true)
        }
