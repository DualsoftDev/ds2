using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.StandardSubmodels;
using Ds2.Core.Store;
using Ds2.Editor;
using PromakerShared = Promaker.Shared;

namespace Promaker.ViewModels;

/// <summary>연결 방식 콤보 항목 — Value 는 문자열 계약(<see cref="PromakerShared.PlcTransports"/>), Label 은 표시용.</summary>
public sealed record PlcTransportOption(string Value, string Label);

/// <summary>
/// System 속성 패널 "PLC 연결" 섹션의 접속 매체 부분 — 연결 방식(Ethernet TCP/UDP · USB) 선택.
/// 매체별 검증·저장 규칙은 F#(AidXgtEndpointSettings)이 갖고, 여기서는 화면 상태만 다룬다.
///
/// USB 는 고를 수만 있고 <b>장치를 지정하지 않는다</b> — 수집 시점의 첫 장치에 붙는다. 장치 검색·선택
/// UI 를 뒀다가 걷어낸 이유:
///   * 검색은 Promaker 가 도는 PC 의 libusb 를 열거한다. 수집을 Edge 단말(Pi)에 위임하면 열거 대상이
///     아예 다른 기계라, 고른 장치 키가 현장에서 맞을 근거가 없다.
///   * LS USB 는 VID/PID 가 XGI·XGK·XGB 공통이고 serial 은 best-effort(빈 값 가능)라 개체를 가리키는
///     안정적인 키가 없다. 남은 키인 목록번호·bus:addr 은 재삽입·재부팅에 뒤바뀐다.
/// 그래서 "장치를 고른다"가 성립하려면 수집 호스트가 자기 USB 인벤토리를 Hub 로 보고하는 경로가 먼저
/// 필요하다. 그 전까지는 <b>USB 접속을 프로젝트(수집기 1대)당 System 1개만</b> 두고 선택을 없앤다 —
/// 둘 이상이면 모두 "첫 장치"에 붙어 두 번째가 claim BUSY 로 조용히 죽는다. 이 규칙은 저장(ApplySystemPlc)에서
/// 막고, 패널의 안내 줄(<see cref="PlcUsbNote"/>)이 저장 전에 먼저 알려 준다.
/// </summary>
public partial class PropertyPanelState
{
    /// <summary>접속 매체 라벨 — "tcp" | "udp" | "usb".</summary>
    [ObservableProperty] private string _plcTransport = PromakerShared.PlcTransports.Tcp;

    /// <summary>USB 장치 선택 키. 화면에서 입력하는 값이 아니라 <b>AID 에 이미 있던 값을 그대로 되돌려
    /// 보내기 위한 통로</b>다 — 손으로 적은 AASX 의 키가 다른 항목을 저장할 때 조용히 지워지면 안 된다.
    /// Promaker 가 새로 만드는 USB endpoint 는 항상 ""(첫 장치).</summary>
    [ObservableProperty] private string _plcUsbDeviceSelector = string.Empty;

    /// <summary>USB 를 고르면 IP/Port 자리에 대신 보이는 한 줄 — 정책(프로젝트당 1대)과 전제(수집 PC 직결,
    /// XG5000 포트 독점). 다른 System 이 이미 USB 면 그 이름을 앞세운 경고로 바뀐다. 툴팁에만 두면
    /// 저장하고 나서야 알게 되는 내용이라 패널 본문에 둔다.</summary>
    [ObservableProperty] private string _plcUsbNote = string.Empty;

    /// <summary>다른 System 이 이미 USB 를 점유 — 안내 줄을 경고색으로. 저장 버튼은 열어 둔다(사용자가 이
    /// System 을 Ethernet 으로 바꿔 저장할 수 있어야 하므로) — 막는 건 ApplySystemPlc 가 한다.</summary>
    [ObservableProperty] private bool _plcUsbConflict;

    /// <summary>USB 로 바꾸기 직전의 이더넷 입력. USB endpoint 는 AID base 에 host 가 실리지 않아 저장 후
    /// 다시 읽으면 IP 가 빈칸이 된다 — 같은 패널 세션 안에서 Ethernet 으로 되돌리면 복원한다.</summary>
    private (string Ip, int Port)? _plcEthernetStash;

    public bool IsPlcUsb => PlcEndpointLabel.isUsb(PlcTransport);
    public bool IsPlcEthernet => !IsPlcUsb;
    /// <summary>"내장 이더넷" 체크박스는 LS 이더넷에서만 의미가 있다(USB 로더 포트에는 없는 개념).</summary>
    public bool IsPlcLsEthernet => IsPlcVendorLs && IsPlcEthernet;

    /// <summary>현재 벤더가 고를 수 있는 연결 방식. 목록은 Promaker.Shared 가 정한다(UDP 는 Mitsubishi, USB 는 LS).</summary>
    public IReadOnlyList<PlcTransportOption> PlcTransportChoices =>
        PromakerShared.PlcVendorProfile.TransportsFor((PromakerShared.PlcVendorChoice)PlcVendor)
            .Select(t => new PlcTransportOption(t, TransportLabelOf(t)))
            .ToList();

    private static string TransportLabelOf(string transport) =>
        transport == PromakerShared.PlcTransports.Usb ? "USB (로더 포트)"
        : transport == PromakerShared.PlcTransports.Udp ? "Ethernet (UDP)"
        : "Ethernet (TCP)";

    partial void OnPlcTransportChanged(string value)
    {
        OnPropertyChanged(nameof(IsPlcUsb));
        OnPropertyChanged(nameof(IsPlcEthernet));
        OnPropertyChanged(nameof(IsPlcLsEthernet));

        // 사용자 조작일 때만(로드 중엔 저장된 값이 기준) — Ethernet→USB 는 IP/Port 를 보관, USB→Ethernet 은
        // IP 가 비어 있으면 보관해 둔 값으로 되살린다.
        if (!_suppressPlcDirty)
        {
            var ip = (PlcIpAddress ?? "").Trim();
            if (PlcEndpointLabel.isUsb(value))
            {
                if (ip.Length > 0) _plcEthernetStash = (ip, PlcPort);
            }
            else if (ip.Length == 0 && _plcEthernetStash is { } stash)
            {
                PlcIpAddress = stash.Ip;
                PlcPort = stash.Port;
            }
        }

        RefreshPlcUsbNote();
        UpdatePlcDirty();
    }

    partial void OnPlcUsbDeviceSelectorChanged(string value) => UpdatePlcDirty();

    /// <summary>벤더가 바뀌면 고를 수 있는 연결 방식이 달라진다 — 현재 값이 목록에 없으면 TCP 로 되돌린다.
    /// USB 에서 되돌아간 경우는 상태줄로 알린다: 콤보 값이 소리 없이 바뀌면 사용자는 USB 로 저장된 줄 안다.</summary>
    private void RefreshPlcTransportChoices()
    {
        OnPropertyChanged(nameof(PlcTransportChoices));
        OnPropertyChanged(nameof(IsPlcLsEthernet));
        if (PromakerShared.PlcVendorProfile.TransportsFor((PromakerShared.PlcVendorChoice)PlcVendor).Contains(PlcTransport))
            return;

        var wasUsb = IsPlcUsb;
        PlcTransport = PromakerShared.PlcTransports.Tcp;
        if (wasUsb && !_suppressPlcDirty)
            _host.SetStatusText(
                $"{PlcVendor} 는 USB 수집을 지원하지 않아 연결 방식을 Ethernet(TCP) 로 되돌렸습니다 — IP/Port 를 확인하세요.");
    }

    /// <summary>현재 선택 System 을 제외한 활성 System 중 USB endpoint 를 가진 것의 이름. 없으면 null.
    /// 기준은 store 에 저장된 endpoint 다 — 다른 System 패널에서 USB 로 바꿔 놓고 아직 저장하지 않은 값은 보지 않는다.</summary>
    private string? OtherUsbSystemName()
    {
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return null;
        return _host.Simulation.ListPlcSystemEndpoints()
            .FirstOrDefault(e => e.SystemId != systemNode.Id && e.HasEndpoint && e.Profile.IsUsb)
            ?.SystemName;
    }

    /// <summary>USB 안내 줄 갱신 — 연결 방식이 바뀔 때와 패널 로드 끝에 부른다.</summary>
    private void RefreshPlcUsbNote()
    {
        if (!IsPlcUsb)
        {
            PlcUsbNote = string.Empty;
            PlcUsbConflict = false;
            return;
        }

        var other = OtherUsbSystemName();
        PlcUsbConflict = other is not null;
        PlcUsbNote = other is not null
            ? $"'{other}' 이(가) 이미 USB 접속입니다 — USB 는 프로젝트(수집기 1대)당 System 1개만 둘 수 있습니다. 한쪽을 Ethernet 으로 바꾸세요."
            : "수집하는 PC(Agent 직접) 또는 Edge 단말(위임)에 꽂힌 첫 LS USB 장치에 붙습니다 · 프로젝트당 USB 1대 · 수집 중에는 XG5000 온라인 접속을 함께 쓸 수 없습니다.";
    }
}
