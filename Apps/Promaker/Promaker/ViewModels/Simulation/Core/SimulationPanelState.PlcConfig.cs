using System.Collections.Generic;
using System.Linq;
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

    /// <summary>active System 목록과 각 System 의 AID endpoint 를 편집·표시용으로 투영.
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
                    Transport = Promaker.Shared.PlcTransports.Normalize(conn.Transport),
                    UsbDeviceSelector = conn.UsbDeviceSelector,
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

    /// <summary>System 속성 패널(PLC 연결 섹션)에서 편집한 접속을 그 System 의 AID endpoint 에 저장.
    /// AID 는 AASX 로 저장되는 모델 데이터이므로 성공 시 dirty 마킹. 단일 System 프로젝트면 전역
    /// PlcSettings(런타임 세팅 푸터·PlcConnection.json)도 endpoint 값으로 동기해 표시/레거시 경로 정합 유지.</summary>
    public bool SavePlcEndpointForSystem(
        System.Guid systemId, PlcVendorChoice vendor, Promaker.Shared.PlcVendorProfile profile,
        string? sxIoMapPath, IEnumerable<string>? sxWritableAreas)
    {
        var store = _storeProvider();
        var poco = PlcSettings.ToPoco();
        poco.Vendor = vendor.ToString();
        poco.ApplyProfile(profile);
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
                // 런타임 세팅 푸터 표시가 endpoint 와 어긋나지 않게 한다.
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

    /// <summary>AID 로 표현할 수 없는 벤더(Mitsubishi)와 단일 System SX 동기용 — 접속을 전역 PLC 연결
    /// (PlcConnection.json) 에 저장한다.
    ///
    /// 모델 데이터가 아니므로 dirty 마킹하지 않는다 — 저장할 프로젝트 변경이 없다.</summary>
    public bool SavePlcConnectionGlobally(
        PlcVendorChoice vendor,
        Promaker.Shared.PlcVendorProfile profile,
        string? sxIoMapPath,
        IEnumerable<string>? sxWritableAreas)
    {
        PlcSettings.Vendor = vendor;
        PlcSettings.ApplyProfile(profile);
        PlcSettings.SxIoMapPath = sxIoMapPath ?? string.Empty;
        PlcSettings.SxWritableAreas = sxWritableAreas?.ToList() ?? new List<string>();

        // Save() 가 활성 벤더 프로파일을 CaptureActiveProfile 로 갱신하고 파일에 쓴다.
        // 성공하면 WasPersisted 가 true 가 되고, 그때부터 AID 박제 판정이 이 PC 를 신뢰한다.
        PlcSettings.Save();
        return PlcSettings.WasPersisted;
    }
}
