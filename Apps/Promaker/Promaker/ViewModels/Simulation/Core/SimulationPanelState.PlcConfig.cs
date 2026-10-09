using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.StandardSubmodels;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>System별 AID endpoint 요약 항목 — 저장 시 endpoint 재보장(Save.StampPlcConnection)과
/// USB 중복 검사가 사용. 접속 편집 UI 는 System 속성 패널의 PLC 연결 섹션.
/// AID 에 endpoint 가 아직 없으면 HasEndpoint=false + 기본 프로파일(이름=System명)로 시작한다.</summary>
public sealed record PlcSystemEndpointEntry(
    Guid SystemId,
    string SystemName,
    PlcVendorChoice Vendor,
    PlcVendorProfile Profile,
    bool HasEndpoint,
    int AddressCount);

/// <summary>PLC 접속은 AASX 안 AID endpoint 가 유일한 저장소다 — Promaker 는 PLC 에 붙지 않고
/// 모델 데이터로서 접속을 편집만 한다. 읽기·쓰기 규칙은 Core 의 PlcEndpointStore 가 갖는다(DSPilot 과 공유).</summary>
public partial class SimulationPanelState
{
    /// <summary>active System 목록과 각 System 의 AID endpoint 를 편집·표시용으로 투영.</summary>
    public IReadOnlyList<PlcSystemEndpointEntry> ListPlcSystemEndpoints()
    {
        var store = _storeProvider();
        var project = store.Projects.Values.FirstOrDefault();
        if (project is null)
            return Array.Empty<PlcSystemEndpointEntry>();

        var entries = new List<PlcSystemEndpointEntry>();
        foreach (var sys in Queries.activeSystemsOf(project.Id, store))
        {
            // SX endpoint 를 먼저 본다 — 한 System 이 둘을 동시에 갖지 않도록 저장 경로가
            // 상대 바인딩을 지우지만, 읽는 쪽도 순서를 정해 두어야 결과가 흔들리지 않는다.
            var sxConn = store.TryReadMicrexSxEndpoint(sys.Id);
            if (sxConn is not null)
            {
                var sxProfile = new PlcVendorProfile
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

            var conn = store.TryReadXgtEndpoint(sys.Id);
            if (conn is not null
                && Enum.TryParse<PlcVendorChoice>(conn.Vendor, ignoreCase: true, out var vendor))
            {
                var profile = new PlcVendorProfile
                {
                    Name = sys.Name,
                    IpAddress = conn.IpAddress,
                    Port = conn.Port,
                    TimeoutMs = conn.TimeoutMs > 0 ? conn.TimeoutMs : 3000,
                    ScanIntervalMs = conn.ScanIntervalMs > 0 ? conn.ScanIntervalMs : 100,
                    LocalEthernet = conn.LocalEthernet,
                    NetworkNumber = conn.NetworkNumber,
                    StationNumber = conn.StationNumber,
                    Transport = PlcTransports.Normalize(conn.Transport),
                    UsbDeviceSelector = conn.UsbDeviceSelector,
                };
                entries.Add(new PlcSystemEndpointEntry(
                    sys.Id, sys.Name, vendor, profile, HasEndpoint: true,
                    AddressCount: EnumeratePlcAddressesForSystem(sys.Id).Count));
            }
            else
            {
                // endpoint 미보유 System — LS XGI 기본 프로파일로 시작 (IP 는 사용자가 채움).
                var profile = PlcVendorProfile.Defaults(PlcVendorChoice.LsXgi);
                profile.Name = sys.Name;
                entries.Add(new PlcSystemEndpointEntry(
                    sys.Id, sys.Name, PlcVendorChoice.LsXgi, profile, HasEndpoint: false,
                    AddressCount: EnumeratePlcAddressesForSystem(sys.Id).Count));
            }
        }
        return entries;
    }

    /// <summary>지정 System 소속 PLC 주소 집합 — Flow→Work→Call 체인의 ApiCall Out/In + 그 System 의 UserTag.
    /// System별 AID 바인딩에는 자기 주소만 담아야 한다 (전 모델 주소를 넣으면 남의 PLC 태그가 섞임).</summary>
    public IReadOnlyCollection<string> EnumeratePlcAddressesForSystem(Guid systemId)
    {
        var store = _storeProvider();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in Queries.plcAddressesOfSystem(systemId, store))
            set.Add(address);
        foreach (var r in store.GetAllUserTagsForProject())
            if (r.SystemId == systemId && !string.IsNullOrWhiteSpace(r.TagAddress))
                set.Add(r.TagAddress);
        return set;
    }

    /// <summary>System 속성 패널(PLC 연결 섹션)에서 편집한 접속을 그 System 의 AID endpoint 에 저장.
    /// AID 는 AASX 로 저장되는 모델 데이터이므로 성공 시 dirty 마킹.
    /// Mitsubishi 는 AID 표현이 없어 저장할 곳이 없다 — Core 가 false 를 돌려준다.</summary>
    public bool SavePlcEndpointForSystem(
        Guid systemId, PlcVendorChoice vendor, PlcVendorProfile profile,
        string? sxIoMapPath, IEnumerable<string>? sxWritableAreas)
    {
        var store = _storeProvider();
        var addresses = EnumeratePlcAddressesForSystem(systemId);

        // 벤더에 따라 어느 AID 바인딩에 실리는지가 갈린다. 상대 바인딩은 Core 가 함께 지운다 —
        // 남겨 두면 수집기가 AID 에서 연결을 하나 더 만들어 "1대만 설정했는데 2대" 가 된다.
        var ok = vendor == PlcVendorChoice.MicrexSx
            ? store.EnsureMicrexSxEndpoint(
                systemId, profile, sxIoMapPath ?? string.Empty, sxWritableAreas ?? Array.Empty<string>(), addresses)
            : store.EnsureXgtEndpoint(systemId, vendor, profile, addresses);
        if (!ok) return false;

        MarkDirty?.Invoke();
        return true;
    }
}
