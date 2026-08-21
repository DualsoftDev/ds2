using System.Collections.Generic;
using System.Linq;
using Ds2.Backend.Plc;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.IO;

namespace Promaker.ViewModels;

/// <summary>PLC 설정 다이얼로그용 System별 endpoint 항목.
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
    /// 다중 System 프로젝트에서 System별 PLC 접속을 편집하는 PlcSettingsDialog 의 입력.</summary>
    public IReadOnlyList<PlcSystemEndpointEntry> ListPlcSystemEndpoints()
    {
        var store = _storeProvider();
        var project = store.Projects.Values.FirstOrDefault();
        if (project is null)
            return System.Array.Empty<PlcSystemEndpointEntry>();

        var entries = new List<PlcSystemEndpointEntry>();
        foreach (var sys in Queries.activeSystemsOf(project.Id, store))
        {
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

    /// <summary>다이얼로그에서 편집한 System별 PLC 접속을 그 System 의 AID XGT endpoint 에 저장.</summary>
    public bool SavePlcEndpointForSystem(
        System.Guid systemId, PlcVendorChoice vendor, Promaker.Shared.PlcVendorProfile profile)
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
        poco.WasPersisted = true;
        return Promaker.Shared.AidXgtEndpointSynchronizer.EnsureToStore(
            store, systemId, poco, EnumeratePlcAddressesForSystem(systemId));
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
