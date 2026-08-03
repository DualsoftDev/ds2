using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.Shared;

/// <summary>PLC 접속 정보가 어디서 왔는지. 진단의 단일 어휘 — 로그·UI 가 이 값을 그대로 표시한다.</summary>
public enum PlcConnectionSource
{
    /// <summary>로컬 PlcConnection.json (기존 동작).</summary>
    Sidecar,

    /// <summary>프로젝트 파일(AASX/.sdf)에 저장된 접속 정보.</summary>
    Aasx,

    /// <summary>cloudinit /etc/agent/plc-connections.json — 현장 실측 discovery.</summary>
    Cloudinit,
}

/// <summary>
/// AASX(=store)에서 읽어낸 접속 정보. 벤더/IP/포트가 모두 유효할 때만 생성된다.
///
/// <para><see cref="ProfileVersion"/> 이 0 이면 벤더/IP/포트만 신뢰할 수 있다 — 나머지 필드는
/// 기록되지 않은 구버전 파일의 생성자 기본값이므로 적용하면 안 된다. 1 이상이면 전 필드가 유효하다.</para>
/// </summary>
public sealed record AasxPlcConnection(
    PlcVendorChoice Vendor,
    string IpAddress,
    int Port,
    bool IsUdp,
    int NetworkNumber,
    int StationNumber,
    bool LocalEthernet,
    int TimeoutMs,
    int ProfileVersion)
{
    /// <summary>전송방식·국번까지 프로젝트 값을 신뢰할 수 있는가.</summary>
    public bool HasVendorParams => ProfileVersion >= 1;

    public override string ToString()
    {
        var head = $"{Vendor} {IpAddress}:{Port}";
        if (!HasVendorParams) return head;
        return Vendor == PlcVendorChoice.Mitsubishi
            ? $"{head} ({(IsUdp ? "UDP" : "TCP")}, 국번 {NetworkNumber}/{StationNumber})"
            : $"{head} ({(LocalEthernet ? "로컬 이더넷" : "원격 이더넷")})";
    }
}

/// <summary>접속 해석 결과. 호출자는 <see cref="Settings"/> 를 그대로 게이트웨이 빌드에 쓰고,
/// <see cref="Source"/>/<see cref="Label"/> 을 로그·UI 에 남긴다.</summary>
public sealed class PlcConnectionResolution
{
    public required PlcConnectionSettings Settings { get; init; }
    public required PlcConnectionSource Source { get; init; }

    /// <summary>사람이 읽는 한 줄 요약 — 예: "AASX 내장 (LsXgk 192.168.9.100:2004)".</summary>
    public required string Label { get; init; }

    /// <summary>기각된 출처 등 진단 메시지. Promaker.Shared 는 로깅 의존성을 갖지 않으므로
    /// 호출자(Agent/WPF)가 자기 로거로 남긴다.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// PLC 접속 정보의 출처 우선순위를 결정하는 <b>단일 지점</b>.
///
/// <para>우선순위: <c>cloudinit</c> → <c>AASX 내장</c> → <c>로컬 PlcConnection.json</c>.
/// cloudinit 이 최상위인 것은 기존 동작 보존 — 분리 아키텍처에서 현장 실측 discovery 가
/// 인스턴스에 내려주는 값이 실제 배선을 반영하기 때문이다.</para>
///
/// <para><b>파일을 쓰지 않는다.</b> AASX 값은 in-memory override 로만 적용된다.
/// Agent 의 설정 지문(ComputeConfigFingerprint)이 PlcConnection.json 직렬화 전체를 포함하므로,
/// 여기서 sidecar 를 갱신하면 지문이 흔들려 BackendHost 재시작이 연쇄한다.
/// sidecar 파일 쓰기는 사용자 조작(Promaker) 경로에서만 일어나야 한다.</para>
///
/// <para><b>미설정 판정</b>: <see cref="ControlSystemProperties"/> 의 PLC 3필드는 생성자 기본값이
/// 이미 배포된 모든 .aasx/.sdf 에 박혀 있다. 세 값이 <i>정확히</i> 기본값 조합이면 "미설정"으로 보고
/// AASX 출처를 건너뛴다 — 구 파일이 현장 sidecar 를 덮어쓰는 사고 방지.
/// 기본 포트 5000 은 실제 설정 경로(<see cref="PlcVendorProfile.Defaults"/>: LS 2004 / MX 5007)가
/// 만들어내지 않는 값이라 판별력이 충분하다.</para>
/// </summary>
public static class PlcConnectionResolver
{
    // ── ControlSystemProperties 의 생성자 기본값 (Ds2.Core/SequenceSubmodels/02_Control.fs) ──
    // 이 조합이 곧 "사용자가 접속을 지정하지 않았음" 의 신호다. Core 의 기본값을 바꾸면
    // 여기도 함께 바꿔야 한다 (바꾸지 않는 것이 원칙 — .sdf 골든/yaml emit 규칙이 함께 흔들린다).
    public const string UnsetIpAddress = "192.168.0.1";
    public const string UnsetVendor = "Mitsubishi";
    public const int UnsetPort = 5000;

    /// <summary>이 빌드가 기록하는 접속 정보 형식 버전 (ControlSystemProperties.PlcProfileVersion).
    /// 1 = 벤더/IP/포트 + 전송방식/국번/LocalEthernet/Timeout 전 필드.
    /// 0(부재) 은 벤더/IP/포트만 기록된 구버전 파일로, 나머지는 로컬 설정을 유지한다.</summary>
    public const int CurrentProfileVersion = 1;

    /// <summary>cloudinit 계약 경로 — 쓰는 쪽(cloudinit.py PLC_CONNECTIONS_PATH)과 일치.</summary>
    public const string CloudinitPlcConnectionsPath = "/etc/agent/plc-connections.json";

    /// <summary>
    /// 접속 정보를 우선순위대로 해석한다. <paramref name="store"/> 가 null 이면 AASX 단계를 건너뛴다.
    /// </summary>
    /// <param name="store">AASX/.sdf 를 로드한 store. 접속 정보는 첫 Project × 첫 ActiveSystem 에서 읽는다.</param>
    /// <param name="sidecarPath">PlcConnection.json 경로. 비어 있으면 공유 기본 경로.</param>
    public static PlcConnectionResolution Resolve(DsStore? store, string? sidecarPath)
    {
        var warnings = new List<string>();
        var path = string.IsNullOrWhiteSpace(sidecarPath) ? SharedPaths.PlcConnectionFilePath : sidecarPath!;
        var settings = PlcConnectionSettings.LoadOrDefault(path);

        // 1) AASX 내장 — sidecar 위에 덮어쓴다 (cloudinit 이 있으면 다시 덮인다).
        var applied = false;
        if (settings.PreferAasxPlcConnection)
        {
            var fromAasx = TryReadFromStore(store, warnings);
            if (fromAasx is not null)
            {
                ApplyToSettings(settings, fromAasx);
                applied = true;
            }
        }
        else if (store is not null)
        {
            warnings.Add("PreferAasxPlcConnection=false — AASX 내장 접속 정보를 사용하지 않습니다.");
        }

        // 2) cloudinit — 있으면 최우선.
        if (TryApplyCloudinit(settings, warnings))
        {
            return new PlcConnectionResolution
            {
                Settings = settings,
                Source = PlcConnectionSource.Cloudinit,
                Label = $"cloudinit ({settings.Vendor} {settings.IpAddress}:{settings.Port})",
                Warnings = warnings,
            };
        }

        return new PlcConnectionResolution
        {
            Settings = settings,
            Source = applied ? PlcConnectionSource.Aasx : PlcConnectionSource.Sidecar,
            Label = applied
                ? $"AASX 내장 ({settings.Vendor} {settings.IpAddress}:{settings.Port})"
                : $"로컬 설정 ({settings.Vendor} {settings.IpAddress}:{settings.Port})",
            Warnings = warnings,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AASX (store) 읽기 / 쓰기
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>store 의 ControlSystemProperties 에서 접속 정보를 읽는다.
    /// 미설정(생성자 기본값 조합)이거나 값이 유효하지 않으면 null.</summary>
    public static AasxPlcConnection? TryReadFromStore(DsStore? store) => TryReadFromStore(store, null);

    private static AasxPlcConnection? TryReadFromStore(DsStore? store, List<string>? warnings)
    {
        if (store is null) return null;

        var cpOpt = Queries.tryGetPrimaryControlProps(store);
        if (cpOpt is null || !Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(cpOpt))
            return null;

        var cp = cpOpt.Value;
        var ip = (cp.PlcIpAddress ?? "").Trim();
        var vendorText = (cp.PlcVendor ?? "").Trim();
        var port = cp.PlcPort;

        // 미설정 — 구 파일 또는 접속을 지정하지 않은 프로젝트. 조용히 건너뛴다(정상 경로).
        if (IsUnset(ip, vendorText, port))
            return null;

        if (!IsValidIpv4(ip))
        {
            warnings?.Add($"AASX 내장 IP '{ip}' 를 해석할 수 없어 무시합니다 — 로컬 설정을 사용합니다.");
            return null;
        }

        if (port is <= 0 or > 65535)
        {
            warnings?.Add($"AASX 내장 포트 {port} 가 범위를 벗어나 무시합니다 — 로컬 설정을 사용합니다.");
            return null;
        }

        // 벤더 파싱 실패는 폴백하지 않는다. 미쓰비시 현장이 LS 프로토콜로 붙어 원인 불명 통신 실패가 나느니
        // AASX 출처 전체를 기각하고 로컬 설정을 쓰는 편이 안전하다.
        if (!Enum.TryParse<PlcVendorChoice>(vendorText, ignoreCase: true, out var vendor))
        {
            warnings?.Add(
                $"AASX 내장 벤더 '{vendorText}' 를 인식할 수 없어 접속 정보 전체를 무시합니다 " +
                $"(지원: {string.Join(", ", Enum.GetNames(typeof(PlcVendorChoice)))}) — 로컬 설정을 사용합니다.");
            return null;
        }

        // 버전 0 = 벤더/IP/포트만 기록된 구버전 파일. 나머지 필드는 생성자 기본값이므로 읽지 않는다.
        // 여기서 읽어버리면 미쓰비시 UDP 현장이 TCP 기본값으로 조용히 덮인다.
        if (cp.PlcProfileVersion < 1)
            return new AasxPlcConnection(vendor, ip, port, false, 0, 255, true, 0, 0);

        // 국번은 프로토콜 레벨에서 byte — 범위를 벗어난 값은 부분 적용하지 않고 전체를 기각한다
        // (포트 검증과 동일 원칙: 조용한 폴백 금지).
        if (cp.PlcNetworkNumber is < 0 or > 255 || cp.PlcStationNumber is < 0 or > 255)
        {
            warnings?.Add(
                $"AASX 내장 국번이 범위를 벗어나 접속 정보 전체를 무시합니다 " +
                $"(네트워크={cp.PlcNetworkNumber}, 국번={cp.PlcStationNumber}, 유효 0~255) — 로컬 설정을 사용합니다.");
            return null;
        }

        // CommunicationTimeout(TimeSpan) → TimeoutMs. 0 이하면 "기록 안 됨" 으로 보고 로컬 값을 유지한다.
        var timeoutMs = (int)Math.Round(cp.CommunicationTimeout.TotalMilliseconds);
        if (timeoutMs <= 0) timeoutMs = 0;

        return new AasxPlcConnection(
            vendor, ip, port,
            cp.PlcIsUdp, cp.PlcNetworkNumber, cp.PlcStationNumber, cp.PlcLocalEthernet,
            timeoutMs, cp.PlcProfileVersion);
    }

    /// <summary>현재 접속 설정을 store 의 ControlSystemProperties 에 기록한다 (AASX/.sdf 저장 직전 호출).
    /// Project/ActiveSystem 이 없으면 기록할 곳이 없으므로 false.
    ///
    /// <para><b>이 PC 에 저장된 적 없는 설정은 기록하지 않는다</b>(<see cref="PlcConnectionSettings.WasPersisted"/>).
    /// PlcConnection.json 이 아직 없다는 것은 PLC 설정을 한 번도 확정한 적이 없다는 뜻이고, 그 상태에서
    /// 저장하면 아무도 고른 적 없는 생성자 기본값 192.168.0.10 이 프로젝트에 박혀 파일을 타고 다음 사람에게
    /// 전파된다 — 이 기능이 없애려던 문제와 같은 부류다. 부수 효과로, 접속을 지정하지 않은 프로젝트에
    /// <c>SequenceControl</c> 서브모델이 새로 생기는 일도 사라진다
    /// (<see cref="Queries.getOrCreatePrimaryControlProps"/> 를 아예 호출하지 않으므로).</para>
    ///
    /// <para>값 비교(기본값과 같으면 건너뛰기)로 이 판별을 하면 안 된다 — 실제로 192.168.0.10:2004 을 쓰는
    /// 현장의 설정이 "손댄 적 없음" 으로 오인되어 기록이 누락된다. 저장 이력만이 의도의 신호다.</para>
    /// </summary>
    public static bool StampToStore(DsStore? store, PlcConnectionSettings settings)
    {
        if (store is null || settings is null) return false;
        if (!settings.WasPersisted) return false;

        var cpOpt = Queries.getOrCreatePrimaryControlProps(store);
        if (cpOpt is null || !Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(cpOpt))
            return false;

        var cp = cpOpt.Value;
        cp.PlcVendor = settings.Vendor;
        cp.PlcIpAddress = (settings.IpAddress ?? "").Trim();
        cp.PlcPort = settings.Port;

        // 벤더별 접속 파라미터 — byte → int 경계 변환 (AASX 리플렉션이 byte 를 지원하지 않음).
        cp.PlcIsUdp = settings.IsUdp;
        cp.PlcNetworkNumber = settings.NetworkNumber;
        cp.PlcStationNumber = settings.StationNumber;
        cp.PlcLocalEthernet = settings.LocalEthernet;
        if (settings.TimeoutMs > 0)
            cp.CommunicationTimeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);

        cp.PlcProfileVersion = CurrentProfileVersion;
        return true;
    }

    /// <summary>
    /// <b>Promaker 가 기록한</b> 접속 정보만 미설정 상태로 되돌린다. "AASX 에 접속 정보 포함" 을 끄고
    /// 저장할 때, 앞서 우리가 실어둔 값을 회수하기 위한 것이다.
    ///
    /// <para><see cref="ControlSystemProperties.PlcProfileVersion"/> 이 0 이면 손편집(AasxEditor)이나
    /// 외부 도구가 넣은 값이므로 <b>건드리지 않는다</b> — 저장 동작이 남의 데이터를 조용히 파괴하지 않도록.</para>
    /// </summary>
    /// <returns>실제로 지웠으면 true. 지울 것이 없거나 우리 것이 아니면 false.</returns>
    public static bool ClearStampedConnection(DsStore? store)
    {
        if (store is null) return false;

        var cpOpt = Queries.tryGetPrimaryControlProps(store);
        if (cpOpt is null || !Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(cpOpt))
            return false;

        var cp = cpOpt.Value;
        if (cp.PlcProfileVersion < CurrentProfileVersion)
            return false;   // 우리가 쓴 값이 아니다 — 보존.

        cp.PlcVendor = UnsetVendor;
        cp.PlcIpAddress = UnsetIpAddress;
        cp.PlcPort = UnsetPort;
        cp.PlcProfileVersion = 0;   // 벤더 파라미터도 무효화 — 3필드 게이트만으로 판정되게 되돌린다.
        return true;
    }

    /// <summary>점 4개로 구분된 IPv4 인지. <see cref="IPAddress.TryParse"/> 만으로는 부족하다 —
    /// "192.168.0" 같은 레거시 3-파트 표기를 192.168.0.0 으로 받아들여 엉뚱한 대상에 접속하게 된다.
    /// PLC 접속 대상은 항상 완전한 IPv4 이므로 옥텟 4개를 강제한다.</summary>
    private static bool IsValidIpv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        return IPAddress.TryParse(ip, out var parsed)
               && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>생성자 기본값 조합 = "사용자가 지정하지 않음".</summary>
    public static bool IsUnset(string? ip, string? vendor, int port) =>
        string.IsNullOrWhiteSpace(ip)
        || (string.Equals(ip!.Trim(), UnsetIpAddress, StringComparison.Ordinal)
            && string.Equals((vendor ?? "").Trim(), UnsetVendor, StringComparison.OrdinalIgnoreCase)
            && port == UnsetPort);

    /// <summary>해석된 AASX 접속 정보를 설정에 반영. 운영 튜닝값(스캔주기/자동정합/간트윈도우)은
    /// 건드리지 않는다 — Agent 가 라이브로 조정해 PlcConnection.json 에 영속화하는 값이라
    /// 여기서 덮으면 재시작마다 사용자 조정이 원복된다.</summary>
    public static void ApplyToSettings(PlcConnectionSettings settings, AasxPlcConnection conn)
    {
        // 벤더가 바뀌면 그 벤더의 프로파일을 먼저 플랫 필드로 복원 — 국번/전송방식(UDP)/LocalEthernet 처럼
        // AASX 가 싣지 않는 벤더별 값이 이전 벤더 것으로 남는 것을 막는다.
        // 스캔 주기만은 프로파일 복원에서 제외 — 위 이유와 동일.
        if (!string.Equals(settings.Vendor, conn.Vendor.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var keepScanIntervalMs = settings.ScanIntervalMs;
            settings.ApplyProfileToFlat(conn.Vendor);
            settings.ScanIntervalMs = keepScanIntervalMs;
        }

        settings.Vendor = conn.Vendor.ToString();
        settings.IpAddress = conn.IpAddress;
        settings.Port = conn.Port;

        // 버전 0(구 파일)이면 전송방식·국번은 기록되지 않은 값이므로 로컬 설정을 그대로 둔다.
        if (conn.HasVendorParams)
        {
            settings.IsUdp = conn.IsUdp;
            settings.NetworkNumber = (byte)conn.NetworkNumber;
            settings.StationNumber = (byte)conn.StationNumber;
            settings.LocalEthernet = conn.LocalEthernet;
            if (conn.TimeoutMs > 0) settings.TimeoutMs = conn.TimeoutMs;
        }

        settings.EnsureProfiles();
    }

    /// <summary>설정이 AASX 접속 정보와 이미 같은지 — 재적용/통지가 필요한지 판단용.
    /// 버전 0(구 파일)은 벤더/IP/포트만 비교한다 — 나머지는 애초에 적용 대상이 아니다.</summary>
    public static bool Matches(PlcConnectionSettings settings, AasxPlcConnection conn)
    {
        var head = string.Equals(settings.Vendor, conn.Vendor.ToString(), StringComparison.OrdinalIgnoreCase)
                   && string.Equals((settings.IpAddress ?? "").Trim(), conn.IpAddress, StringComparison.Ordinal)
                   && settings.Port == conn.Port;
        if (!head || !conn.HasVendorParams) return head;

        return settings.IsUdp == conn.IsUdp
               && settings.NetworkNumber == conn.NetworkNumber
               && settings.StationNumber == conn.StationNumber
               && settings.LocalEthernet == conn.LocalEthernet
               && (conn.TimeoutMs <= 0 || settings.TimeoutMs == conn.TimeoutMs);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // cloudinit — MonitoringSupervisor 에서 이관 (로직 무변경)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>cloudinit 이 내려준 접속으로 override. 파일 없으면(로컬/올인원/Windows) no-op → false.
    /// 계약: {"version":1,"plcs":[{"name","ip","type","enabled"}]}. discovery 는 ip/type 만 주므로
    /// port/station 은 벤더 기본으로 유추. 현재 빌더가 단일 connection 이라 첫 enabled PLC 만 반영.</summary>
    private static bool TryApplyCloudinit(PlcConnectionSettings settings, List<string> warnings)
    {
        try
        {
            if (!File.Exists(CloudinitPlcConnectionsPath))
                return false; // 로컬/올인원 — cloudinit 접속 없음, 기존 설정 유지.

            var json = File.ReadAllText(CloudinitPlcConnectionsPath);
            var doc = JsonSerializer.Deserialize<PlcConnectionsFile>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var first = doc?.Plcs?.FirstOrDefault(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Ip));
            if (first is null)
            {
                warnings.Add($"plc-connections.json 에 enabled PLC 없음 ({CloudinitPlcConnectionsPath}) — 기존 설정 유지.");
                return false;
            }

            var (vendor, port) = MapPlcType(first.Type);
            settings.Vendor = vendor;
            settings.Port = port;
            settings.IpAddress = first.Ip!.Trim();
            if (!string.IsNullOrWhiteSpace(first.Name)) settings.Name = first.Name!;
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"plc-connections.json 로드 실패 ({CloudinitPlcConnectionsPath}) — 기존 설정 유지: {ex.Message}");
            return false;
        }
    }

    /// <summary>discovery type("LS"/"MX"/…) → (Vendor enum 이름, 기본 port). discovery 는 ip/type 만 주므로
    /// port/station 세부는 벤더 기본으로 유추. LS 계열 XGK/XGI 구분 정보가 type 에 없어 LsXgk 기본.</summary>
    private static (string vendor, int port) MapPlcType(string? type) =>
        (type ?? "").Trim().ToUpperInvariant() switch
        {
            "MX" or "MITSUBISHI" or "MELSEC" => (nameof(PlcVendorChoice.Mitsubishi), 5007),
            "XGI" or "LSXGI" => (nameof(PlcVendorChoice.LsXgi), 2004),
            _ => (nameof(PlcVendorChoice.LsXgk), 2004), // "LS"(discovery 기본) 포함
        };

    private sealed record PlcConnectionsFile
    {
        public int Version { get; init; }
        public PlcConnItem[]? Plcs { get; init; }
    }

    private sealed record PlcConnItem
    {
        public string? Name { get; init; }
        public string? Ip { get; init; }
        public string? Type { get; init; }
        public bool Enabled { get; init; } = true;
    }
}
