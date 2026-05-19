using System.Collections.Generic;
using System.Linq;
using Ds2.Backend.Plc;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.IO;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>현재 IO 매핑이 비어있지 않은지 — RuntimeMode 전이 시 I/O 미설정 경고에 사용.</summary>
    private bool HasIOConfigured()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        return iomap.Mappings.Length > 0;
    }

    /// <summary>현재 IO 매핑에서 dedup 된 PLC 주소 개수 — PLC 설정 다이얼로그 안내용.</summary>
    public int CountAutoImportablePlcAddresses()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var k in iomap.OutAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        foreach (var k in iomap.InAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        return set.Count;
    }

    /// <summary>현재 IO 매핑 + UI 의 PlcSettings 로 PlcGatewayConfig 를 빌드.
    /// PLAY 시점 (Hub.TryStart) 에서 호출. 검증 실패 시 errors 채워 null 반환.
    /// UserTag 주소도 함께 PLC 스캔 대상으로 포함 — 그래야 DSPilot 의 UserTag 알림이
    /// 동작 (Hub 에 그 주소 변화가 흘러야 plcTagLog 에 기록됨).</summary>
    public PlcGatewayConfig? BuildPlcGatewayConfig(out List<string> errors)
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        var userTagAddresses = store.GetAllUserTagsForProject()
            .Select(r => r.TagAddress);
        return PlcSettings.BuildGatewayConfig(iomap, out errors, userTagAddresses);
    }
}
