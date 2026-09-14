using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core.StandardSubmodels;
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
/// 필요하다. 그 전까지는 USB 접속을 프로젝트에 하나만 두고 선택을 없앤다.
/// </summary>
public partial class PropertyPanelState
{
    /// <summary>접속 매체 라벨 — "tcp" | "udp" | "usb".</summary>
    [ObservableProperty] private string _plcTransport = PromakerShared.PlcTransports.Tcp;

    /// <summary>USB 장치 선택 키. 화면에서 입력하는 값이 아니라 <b>AID 에 이미 있던 값을 그대로 되돌려
    /// 보내기 위한 통로</b>다 — 손으로 적은 AASX 의 키가 다른 항목을 저장할 때 조용히 지워지면 안 된다.
    /// Promaker 가 새로 만드는 USB endpoint 는 항상 ""(첫 장치).</summary>
    [ObservableProperty] private string _plcUsbDeviceSelector = string.Empty;

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
        UpdatePlcDirty();
    }

    partial void OnPlcUsbDeviceSelectorChanged(string value) => UpdatePlcDirty();

    /// <summary>벤더가 바뀌면 고를 수 있는 연결 방식이 달라진다 — 현재 값이 목록에 없으면 TCP 로 되돌린다.</summary>
    private void RefreshPlcTransportChoices()
    {
        OnPropertyChanged(nameof(PlcTransportChoices));
        OnPropertyChanged(nameof(IsPlcLsEthernet));
        if (!PromakerShared.PlcVendorProfile.TransportsFor((PromakerShared.PlcVendorChoice)PlcVendor).Contains(PlcTransport))
            PlcTransport = PromakerShared.PlcTransports.Tcp;
    }
}
