using System.Collections.Generic;
using System.Linq;
using Ds2.Backend.Plc;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.IO;

namespace Promaker.ViewModels;

/// <summary>System별 AID endpoint 요약 항목 — 런타임 세팅(실행 대상·푸터)과 저장 시 endpoint 재보장
/// (Save.StampPlcConnection)이 사용. 접속 편집 UI 는 System 속성 패널의 PLC 연결 섹션.
/// AID 에 endpoint 가 아직 없으면 HasEndpoint=false + 기본 프로파일(이름=System명)로 시작한다.</summary>
public sealed record PlcSystemEndpointEntry(
    System.Guid SystemId,
    string SystemName,
    PlcVendorChoice Vendor,
    Promaker.Shared.PlcVendorProfile Profile,
    bool HasEndpoint,
    int AddressCount);

public partial class SimulationPanelState
{
    /// <summary>실행 대상 System(PLC) — null 이면 프로젝트(라인) 전체 실행.
    /// System 단위 실행이면 엔진 인덱스·IO맵·PLC 스캔이 그 System(+인과 폐포)으로 한정된다.
    /// 세션 선택값 (비영속) — 런타임 설정 다이얼로그에서 지정.</summary>
    public System.Guid? RuntimeTargetSystemId { get; set; }

    /// <summary>현재 IO 매핑이 비어있지 않은지 — RuntimeMode 전이 시 I/O 미설정 경고에 사용.</summary>
    private bool HasIOConfigured()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        return iomap.Mappings.Length > 0;
    }

    /// <summary>현재 IO 매핑에서 dedup 된 PLC 주소 개수 — PLC 설정 다이얼로그 안내용.</summary>
    public int CountAutoImportablePlcAddresses() => EnumeratePlcAddresses().Count;

    /// <summary>현재 IO 매핑(OUT/IN) + UserTag 의 dedup 된 PLC 주소 집합.
    /// AID XGT 바인딩 생성(AidXgtEndpointSynchronizer.EnsureToStore)의 InteractionMetadata 원천.</summary>
    public IReadOnlyCollection<string> EnumeratePlcAddresses()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var k in iomap.OutAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        foreach (var k in iomap.InAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        foreach (var r in store.GetAllUserTagsForProject())
            if (!string.IsNullOrWhiteSpace(r.TagAddress)) set.Add(r.TagAddress);
        return set;
    }

    /// <summary>active System 목록과 각 System 의 AID XGT endpoint 를 다이얼로그 편집용으로 투영.
    /// 런타임 세팅(실행 대상 콤보·푸터 요약)과 저장 시 endpoint 재보장이 사용.</summary>
    public IReadOnlyList<PlcSystemEndpointEntry> ListPlcSystemEndpoints()
    {
        var store = _storeProvider();
        var project = store.Projects.Values.FirstOrDefault();
        if (project is null)
            return System.Array.Empty<PlcSystemEndpointEntry>();

        var entries = new List<PlcSystemEndpointEntry>();
        foreach (var sys in Queries.activeSystemsOf(project.Id, store))
        {
            // SX endpoint 를 먼저 본다 — 한 System 이 둘을 동시에 갖지 않도록 저장 경로가
            // 상대 바인딩을 지우지만, 읽는 쪽도 순서를 정해 두어야 결과가 흔들리지 않는다.
            var sxConn = Promaker.Shared.AidMicrexSxEndpointSynchronizer.TryReadFromStore(store, sys.Id);
            if (sxConn is not null)
            {
                var sxProfile = new Promaker.Shared.PlcVendorProfile
                {
                    Name = sys.Name,
                    IpAddress = sxConn.IpAddress,
                    Port = sxConn.Port,
                    TimeoutMs = sxConn.TimeoutMs > 0 ? sxConn.TimeoutMs : 3000,
                    ScanIntervalMs = sxConn.ScanIntervalMs > 0 ? sxConn.ScanIntervalMs : 100,
                };
                entries.Add(new PlcSystemEndpointEntry(
                    sys.Id, sys.Name, PlcVendorChoice.MicrexSx, sxProfile, HasEndpoint: true,
                    AddressCount: EnumeratePlcAddressesForSystem(sys.Id).Count));
                continue;
            }

            var conn = Promaker.Shared.AidXgtEndpointSynchronizer.TryReadFromStore(store, sys.Id);
            if (conn is not null
                && System.Enum.TryParse<PlcVendorChoice>(conn.Vendor, ignoreCase: true, out var vendor))
            {
                var profile = new Promaker.Shared.PlcVendorProfile
                {
                    Name = sys.Name,
                    IpAddress = conn.IpAddress,
                    Port = conn.Port,
                    TimeoutMs = conn.TimeoutMs > 0 ? conn.TimeoutMs : 3000,
                    ScanIntervalMs = conn.ScanIntervalMs > 0 ? conn.ScanIntervalMs : 100,
                    LocalEthernet = conn.LocalEthernet,
                    NetworkNumber = conn.NetworkNumber,
                    StationNumber = conn.StationNumber,
                    IsUdp = conn.IsUdp,
                };
                entries.Add(new PlcSystemEndpointEntry(
                    sys.Id, sys.Name, vendor, profile, HasEndpoint: true,
                    AddressCount: EnumeratePlcAddressesForSystem(sys.Id).Count));
            }
            else
            {
                // endpoint 미보유 System — 현재 화면 벤더의 기본 프로파일로 시작 (IP 는 사용자가 채움).
                var fallbackVendor = PlcSettings.Vendor;
                var profile = Promaker.Shared.PlcVendorProfile.Defaults(
                    (Promaker.Shared.PlcVendorChoice)fallbackVendor);
                profile.Name = sys.Name;
                entries.Add(new PlcSystemEndpointEntry(
                    sys.Id, sys.Name, fallbackVendor, profile, HasEndpoint: false,
                    AddressCount: EnumeratePlcAddressesForSystem(sys.Id).Count));
            }
        }
        return entries;
    }

    /// <summary>지정 System 소속 PLC 주소 집합 — Flow→Work→Call 체인의 ApiCall Out/In + 그 System 의 UserTag.
    /// System별 AID 바인딩에는 자기 주소만 담아야 한다 (전 모델 주소를 넣으면 남의 PLC 태그가 섞임).</summary>
    public IReadOnlyCollection<string> EnumeratePlcAddressesForSystem(System.Guid systemId)
    {
        var store = _storeProvider();
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var address in Queries.plcAddressesOfSystem(systemId, store))
            set.Add(address);
        foreach (var r in store.GetAllUserTagsForProject())
            if (r.SystemId == systemId && !string.IsNullOrWhiteSpace(r.TagAddress))
                set.Add(r.TagAddress);
        return set;
    }

    /// <summary>System 속성 패널(PLC 연결 섹션)에서 편집한 접속을 그 System 의 AID XGT endpoint 에 저장.
    /// AID 는 AASX 로 저장되는 모델 데이터이므로 성공 시 dirty 마킹. 단일 System 프로젝트면 전역
    /// PlcSettings(런타임 세팅 푸터·PlcConnection.json)도 endpoint 값으로 동기해 표시/레거시 경로 정합 유지.</summary>
    public bool SavePlcEndpointForSystem(
        System.Guid systemId, PlcVendorChoice vendor, Promaker.Shared.PlcVendorProfile profile,
        string? sxIoMapPath, IEnumerable<string>? sxWritableAreas)
    {
        var store = _storeProvider();
        var poco = PlcSettings.ToPoco();
        poco.Name = profile.Name;
        poco.Vendor = vendor.ToString();
        poco.IpAddress = profile.IpAddress;
        poco.Port = profile.Port;
        poco.IsUdp = profile.IsUdp;
        poco.LocalEthernet = profile.LocalEthernet;
        poco.NetworkNumber = profile.NetworkNumber;
        poco.StationNumber = profile.StationNumber;
        poco.TimeoutMs = profile.TimeoutMs;
        poco.ScanIntervalMs = profile.ScanIntervalMs;
        poco.SxIoMapPath = sxIoMapPath ?? string.Empty;
        poco.SxWritableAreas = sxWritableAreas?.ToList() ?? new List<string>();
        poco.WasPersisted = true;

        var addresses = EnumeratePlcAddressesForSystem(systemId);

        // 벤더에 따라 어느 AID 바인딩에 실리는지가 갈린다. 상대 바인딩은 각 동기화기가 지운다 —
        // 남겨 두면 Agent 가 AID 에서 연결을 하나 더 만들어 "1대만 설정했는데 2대" 가 된다.
        bool ok;
        if (vendor == PlcVendorChoice.MicrexSx)
        {
            ok = Promaker.Shared.AidMicrexSxEndpointSynchronizer.EnsureToStore(
                store, systemId, poco, addresses);
        }
        else if (Promaker.Shared.PlcVendorProfile.IsAidXgtVendor(
                     (Promaker.Shared.PlcVendorChoice)vendor))
        {
            ok = Promaker.Shared.AidXgtEndpointSynchronizer.EnsureToStore(
                store, systemId, poco, addresses);
            if (ok) Promaker.Shared.AidMicrexSxEndpointSynchronizer.RemoveFromStore(store, systemId);
        }
        else
        {
            // Mitsubishi — AID 에 표현이 없다. 전역 연결에만 저장하므로 Agent 경로는 여전히
            // 이 벤더를 수집하지 못한다. SX 처럼 인터페이스를 신설해야 해소된다.
            return SavePlcConnectionGlobally(vendor, profile, sxIoMapPath, sxWritableAreas);
        }
        if (!ok) return false;

        var project = store.Projects.Values.FirstOrDefault();
        if (project is not null && project.ActiveSystemIds.Count == 1)
        {
            if (vendor == PlcVendorChoice.MicrexSx)
            {
                // SX 는 AidXgtConnectionInfo 로 읽히지 않는다. 전역 연결도 같은 값으로 맞춰
                // Promaker 인앱 게이트웨이(BuildPlcGatewayConfig)와 푸터 표시가 어긋나지 않게 한다.
                SavePlcConnectionGlobally(vendor, profile, sxIoMapPath, sxWritableAreas);
            }
            else
            {
                var conn = Promaker.Shared.AidXgtEndpointSynchronizer.TryReadFromStore(store, systemId);
                if (conn is not null)
                {
                    PlcSettings.ApplyConnection(conn);
                    PlcSettings.Save();
                }
            }
        }

        MarkDirty?.Invoke();
        return true;
    }

    /// <summary>AID InterfaceXGT 로 표현할 수 없는 벤더(MICREX-SX)의 접속을 전역 PLC 연결
    /// (PlcConnection.json) 에 저장한다.
    ///
    /// InterfaceXGT endpoint 의 CpuModel 은 Xgi|Xgk|Xgb 뿐이라 SX 는 그쪽에 실릴 수 없다
    /// (AidXgtEndpointSettings.tryCpuModel 이 비-LS 를 거부한다). 그런데 Promaker 가 실제로
    /// 스캔에 쓰는 값은 전역 PlcSettings 다 — <see cref="BuildPlcGatewayConfig"/> 가
    /// PlcSettings.BuildGatewayConfig 로 위임한다. 그래서 AASX 박제만 건너뛰고 런타임이 읽는
    /// 곳에 저장하면 SX 도 그대로 동작한다.
    ///
    /// 모델 데이터가 아니므로 dirty 마킹하지 않는다 — 저장할 프로젝트 변경이 없다.</summary>
    public bool SavePlcConnectionGlobally(
        PlcVendorChoice vendor,
        Promaker.Shared.PlcVendorProfile profile,
        string? sxIoMapPath,
        IEnumerable<string>? sxWritableAreas)
    {
        PlcSettings.Vendor = vendor;
        PlcSettings.Name = profile.Name;
        PlcSettings.IpAddress = profile.IpAddress;
        PlcSettings.Port = profile.Port;
        PlcSettings.TimeoutMs = profile.TimeoutMs;
        PlcSettings.ScanIntervalMs = profile.ScanIntervalMs;
        PlcSettings.LocalEthernet = profile.LocalEthernet;
        PlcSettings.NetworkNumber = profile.NetworkNumber;
        PlcSettings.StationNumber = profile.StationNumber;
        PlcSettings.IsUdp = profile.IsUdp;
        PlcSettings.SxIoMapPath = sxIoMapPath ?? string.Empty;
        PlcSettings.SxWritableAreas = sxWritableAreas?.ToList() ?? new List<string>();

        // Save() 가 활성 벤더 프로파일을 CaptureActiveProfile 로 갱신하고 파일에 쓴다.
        // 성공하면 WasPersisted 가 true 가 되고, 그때부터 AID 박제 판정이 이 PC 를 신뢰한다.
        PlcSettings.Save();
        return PlcSettings.WasPersisted;
    }

    /// <summary>현재 IO 매핑 + UI 의 PlcSettings 로 PlcGatewayConfig 를 빌드.
    /// PLAY 시점 (Hub.TryStart) 에서 호출. 검증 실패 시 errors 채워 null 반환.
    /// UserTag 주소도 함께 PLC 스캔 대상으로 포함 — 그래야 DSPilot 의 UserTag 알림이
    /// 동작 (Hub 에 그 주소 변화가 흘러야 plcTagLog 에 기록됨).</summary>
    public PlcGatewayConfig? BuildPlcGatewayConfig(out List<string> errors)
    {
        var store = _storeProvider();

        // System 단위 실행 — IO맵과 UserTag 를 대상 System(인과 폐포)으로 한정.
        // 다른 PLC(System)의 주소가 이 연결의 스캔 대상에 섞이지 않는다.
        if (RuntimeTargetSystemId is { } targetId)
        {
            var closure = Queries.systemClosureOf(targetId, store);
            var callIds = new HashSet<System.Guid>();
            foreach (var sysId in closure)
                foreach (var flow in Queries.flowsOf(sysId, store))
                    foreach (var work in Queries.worksOf(flow.Id, store))
                        foreach (var call in Queries.callsOf(work.Id, store))
                            callIds.Add(call.Id);
            var scopedIomap = SignalIOMapModule.buildFiltered(
                store,
                Microsoft.FSharp.Core.FSharpOption<Microsoft.FSharp.Collections.FSharpSet<System.Guid>>.Some(
                    Microsoft.FSharp.Collections.SetModule.OfSeq(callIds)));
            var scopedUserTags = store.GetAllUserTagsForProject()
                .Where(r => closure.Contains(r.SystemId))
                .Select(r => r.TagAddress);
            return PlcSettings.BuildGatewayConfig(scopedIomap, out errors, scopedUserTags);
        }

        var iomap = SignalIOMapModule.build(store);
        var userTagAddresses = store.GetAllUserTagsForProject()
            .Select(r => r.TagAddress);
        return PlcSettings.BuildGatewayConfig(iomap, out errors, userTagAddresses);
    }
}
