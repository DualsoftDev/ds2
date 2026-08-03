using System.IO;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>
/// Agent 및 Promaker WPF 데모가 공유하는 OPC UA 서버 설정 POCO. JSON 직렬화 단일 책임 (MVVM 무관).
///
/// 정식 배포에서는 Agent가 <b>OPC UA 서버</b> 역할을 수행한다. 런타임 시작 시
/// Ds2.OpcUa.Server 의 <c>EmbeddedUaServer</c> 를 인프로세스 구동해 KPI/Runtime IO 를
/// UA Variable 로 노출한다. <see cref="Enabled"/> 가 false 이면 서버 자체를 구동하지 않는다.
///
/// 기본 EndpointUrl 은 Ds2.OpcUa.Server 의 기본 포트 62541 · 경로 /Ds2/OpcUa/Server 와 일치.
/// </summary>
public sealed class OpcUaServerSettings
{
    public const string DefaultEndpointUrl = "opc.tcp://localhost:62541/Ds2/OpcUa/Server";
    public const string DefaultApplicationName = "Promaker.OpcUa.Server";
    public const string DefaultApplicationUri = "urn:dualsoft:promaker:opcua";
    public const string AgentApplicationName = "Promaker.Agent.OpcUa.Server";
    public const string AgentApplicationUri = "urn:dualsoft:promaker-agent:opcua";

    public const int DefaultMaxSessions = 100;
    public const int DefaultSessionTimeoutMs = 60_000;
    public const int DefaultMinSamplingIntervalMs = 100;
    public const int DefaultDefaultSamplingIntervalMs = 500;
    public const int DefaultPublishingIntervalMs = 1_000;

    /// <summary>true 면 런타임 시작 시 UA 서버 구동. false 면 구동하지 않는다 (사용/안사용 토글).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>서버가 바인딩할 endpoint. `opc.tcp://호스트:포트/경로` 형식.</summary>
    public string EndpointUrl { get; set; } = DefaultEndpointUrl;

    /// <summary>UA 스택 ApplicationName. 클라이언트가 세션 정보로 확인.</summary>
    public string ApplicationName { get; set; } = DefaultApplicationName;

    /// <summary>UA 스택 ApplicationUri. 인증서 SubjectAlternativeName 에도 반영.</summary>
    public string ApplicationUri { get; set; } = DefaultApplicationUri;

    /// <summary>true 면 UserIdentityToken 없이 anonymous 세션 허용 (개발 편의). 운영은 false 권장.</summary>
    public bool AllowAnonymous { get; set; } = true;

    /// <summary>true 면 MessageSecurityMode.None endpoint도 노출한다. 운영에서는 false.</summary>
    public bool AllowUnsecuredEndpoint { get; set; } = true;

    /// <summary>동시 세션 상한.</summary>
    public int MaxSessions { get; set; } = DefaultMaxSessions;

    /// <summary>세션 타임아웃 (ms). 서버가 유휴 세션을 정리하는 기준.</summary>
    public int SessionTimeoutMs { get; set; } = DefaultSessionTimeoutMs;

    /// <summary>서버가 허용하는 최소 sampling interval (ms). 값이 작을수록 부하 커짐.</summary>
    public int MinSamplingIntervalMs { get; set; } = DefaultMinSamplingIntervalMs;

    /// <summary>기본 sampling interval — MonitoredItem 이 값을 지정하지 않을 때.</summary>
    public int DefaultSamplingIntervalMs { get; set; } = DefaultDefaultSamplingIntervalMs;

    /// <summary>서버 subscription publishing interval 기본값.</summary>
    public int PublishingIntervalMs { get; set; } = DefaultPublishingIntervalMs;

    /// <summary>true 면 신뢰 저장소에 없는 클라이언트 인증서를 자동 신뢰(개발 편의). 운영은 false.</summary>
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static OpcUaServerSettings LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path)) return new OpcUaServerSettings();
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OpcUaServerSettings>(text, JsonOpts)
                   ?? new OpcUaServerSettings();
        }
        catch
        {
            return new OpcUaServerSettings();
        }
    }

    /// <summary>
    /// Agent 전용 설정을 읽는다. 최초 설치로 파일이 아직 없을 때만 Agent 기본값(Enabled=true)을
    /// 사용한다. 파일이 손상된 경우에는 일반 로더의 안전 기본값(Enabled=false)으로 떨어진다.
    /// </summary>
    public static OpcUaServerSettings LoadAgentOrDefault(string path)
    {
        if (File.Exists(path)) return LoadOrDefault(path);
        return new OpcUaServerSettings
        {
            Enabled = true,
            ApplicationName = AgentApplicationName,
            ApplicationUri = AgentApplicationUri,
            AllowAnonymous = false,
            AllowUnsecuredEndpoint = false,
            AutoAcceptUntrustedCertificates = false,
        };
    }

    public bool TrySave(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
