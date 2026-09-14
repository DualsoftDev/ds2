using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Backend.Plc;
using Ds2.Core.StandardSubmodels;
using PromakerShared = Promaker.Shared;

namespace Promaker.ViewModels;

/// <summary>연결 방식 콤보 항목 — Value 는 문자열 계약(<see cref="PromakerShared.PlcTransports"/>), Label 은 표시용.</summary>
public sealed record PlcTransportOption(string Value, string Label);

/// <summary>
/// System 속성 패널 "PLC 연결" 섹션의 접속 매체 부분 — 연결 방식(Ethernet TCP/UDP · USB) 선택과
/// USB 장치 조회. 매체별 검증·저장 규칙은 F#(AidXgtEndpointSettings)이 갖고, 여기서는 화면 상태만 다룬다.
/// </summary>
public partial class PropertyPanelState
{
    /// <summary>접속 매체 라벨 — "tcp" | "udp" | "usb".</summary>
    [ObservableProperty] private string _plcTransport = PromakerShared.PlcTransports.Tcp;

    /// <summary>USB 장치 선택 키(목록번호 · serial · bus:addr · product 부분일치). "" = 첫 매칭 장치.</summary>
    [ObservableProperty] private string _plcUsbDeviceSelector = string.Empty;

    /// <summary>'검색' 결과 — 이 PC 에 붙은 LS PLC USB 장치. Edge 위임 수집이면 Edge 단말의 장치와 다를 수 있다.</summary>
    [ObservableProperty] private IReadOnlyList<LsUsbDeviceEntry> _plcUsbDevices = Array.Empty<LsUsbDeviceEntry>();

    /// <summary>목록에서 고른 장치 — 고르면 선택 키를 그 장치의 serial(없으면 bus:addr)로 채운다.</summary>
    [ObservableProperty] private LsUsbDeviceEntry? _plcSelectedUsbDevice;

    /// <summary>검색 결과/실패 안내 한 줄.</summary>
    [ObservableProperty] private string _plcUsbDeviceHint = string.Empty;

    public bool IsPlcUsb => PlcEndpointLabel.isUsb(PlcTransport);
    public bool IsPlcEthernet => !IsPlcUsb;
    /// <summary>"내장 이더넷" 체크박스는 LS 이더넷에서만 의미가 있다(USB 로더 포트에는 없는 개념).</summary>
    public bool IsPlcLsEthernet => IsPlcVendorLs && IsPlcEthernet;
    public bool HasPlcUsbDevices => PlcUsbDevices.Count > 0;

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

    partial void OnPlcUsbDevicesChanged(IReadOnlyList<LsUsbDeviceEntry> value) =>
        OnPropertyChanged(nameof(HasPlcUsbDevices));

    partial void OnPlcSelectedUsbDeviceChanged(LsUsbDeviceEntry? value)
    {
        if (value is not null)
            PlcUsbDeviceSelector = value.Selector;
    }

    /// <summary>벤더가 바뀌면 고를 수 있는 연결 방식이 달라진다 — 현재 값이 목록에 없으면 TCP 로 되돌린다.</summary>
    private void RefreshPlcTransportChoices()
    {
        OnPropertyChanged(nameof(PlcTransportChoices));
        OnPropertyChanged(nameof(IsPlcLsEthernet));
        if (!PromakerShared.PlcVendorProfile.TransportsFor((PromakerShared.PlcVendorChoice)PlcVendor).Contains(PlcTransport))
            PlcTransport = PromakerShared.PlcTransports.Tcp;
    }

    /// <summary>이 PC 에 붙은 LS PLC USB 장치 조회. libusb-1.0 이 없거나 장치 접근이 막히면 사유를 안내로 보여준다.</summary>
    [RelayCommand]
    private void RefreshPlcUsbDevices()
    {
        try
        {
            var devices = LsUsbDevices.list();
            PlcUsbDevices = devices;
            PlcSelectedUsbDevice = null;
            PlcUsbDeviceHint = devices.Length == 0
                ? "연결된 LS PLC USB 장치가 없습니다 — 케이블·전원을 확인하세요. 비워 두면 수집 시점의 첫 장치에 붙습니다."
                : $"장치 {devices.Length}개 — 목록에서 고르거나 키를 직접 입력하세요.";
        }
        catch (Exception ex)
        {
            PlcUsbDevices = Array.Empty<LsUsbDeviceEntry>();
            PlcUsbDeviceHint = $"장치 조회 실패: {ex.Message} — libusb-1.0.dll 이 실행 폴더에 있어야 하며, XG5000 온라인 접속 중이면 포트가 점유됩니다.";
        }
    }
}
