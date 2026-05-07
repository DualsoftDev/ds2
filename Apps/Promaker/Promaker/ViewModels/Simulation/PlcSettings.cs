using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Backend.Plc;
using Ds2.Runtime.IO;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public enum PlcVendorChoice
{
    LsXgi,
    LsXgk,
    Mitsubishi
}

/// <summary>
/// PLC 연결 정보 모델. 태그 매핑은 별도로 입력하지 않고 — AASX/IOList 에서 빌드된
/// <see cref="SignalIOMap"/> 의 OUT/IN 주소를 그대로 PLC 게이트웨이의 스캔/쓰기 라우팅에 사용한다.
/// </summary>
public partial class PlcSettings : ObservableObject
{
    [ObservableProperty] private PlcVendorChoice _vendor = PlcVendorChoice.LsXgi;
    [ObservableProperty] private string _name = "PLC#1";
    [ObservableProperty] private string _ipAddress = "192.168.0.10";
    [ObservableProperty] private int _port = 2004;        // LS 기본 2004, MX 기본 5007 — Vendor 변경 시 자동 갱신
    [ObservableProperty] private int _timeoutMs = 3000;
    [ObservableProperty] private int _scanIntervalMs = 100;
    [ObservableProperty] private bool _localEthernet = true;     // LS only
    [ObservableProperty] private byte _networkNumber = 0;        // MX only
    [ObservableProperty] private byte _stationNumber = 0xFF;     // MX only

    partial void OnVendorChanged(PlcVendorChoice value)
    {
        // 벤더 전환 시 기본 포트 자동 적용 (이전 값이 다른 벤더 기본값일 때만 덮어써 의도치 않은 손상 방지).
        if (Port == 2004 || Port == 5007)
            Port = value == PlcVendorChoice.Mitsubishi ? 5007 : 2004;
    }

    /// <summary>
    /// SignalIOMap 의 OUT/IN 주소를 그대로 PLC 태그 리스트로 자동 채워 F# PlcGatewayConfig 빌드.
    /// 검증 실패 시 errors 에 사유 누적 후 null 반환. ioMap.Mappings 가 비어 있어도 connection 은 만들지만
    /// 태그 0 개라 실 효과는 없음 — 사용자에게 경고 추가.
    /// </summary>
    public PlcGatewayConfig? BuildGatewayConfig(SignalIOMap ioMap, out List<string> errors)
    {
        errors = new();
        if (string.IsNullOrWhiteSpace(IpAddress)) errors.Add("IP 주소를 입력하세요.");
        if (Port <= 0 || Port > 65535) errors.Add("포트는 1–65535 범위여야 합니다.");
        if (TimeoutMs <= 0) errors.Add("Timeout(ms) 은 양수여야 합니다.");
        if (ScanIntervalMs <= 0) errors.Add("Scan interval(ms) 은 양수여야 합니다.");

        var fsVendor = Vendor switch
        {
            PlcVendorChoice.LsXgi => Ds2.Backend.Plc.PlcVendor.LsXgi,
            PlcVendorChoice.LsXgk => Ds2.Backend.Plc.PlcVendor.LsXgk,
            PlcVendorChoice.Mitsubishi => Ds2.Backend.Plc.PlcVendor.Mitsubishi,
            _ => Ds2.Backend.Plc.PlcVendor.LsXgi
        };

        // SignalIOMap → 주소 dedup → 데이터 타입 추론 → PlcTagDef
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addr in ioMap.OutAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(addr)) addresses.Add(addr);
        foreach (var addr in ioMap.InAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(addr)) addresses.Add(addr);

        if (addresses.Count == 0)
            errors.Add("AASX IO 매핑에서 OUT/IN 주소가 발견되지 않았습니다. ApiCall 의 OutTag/InTag 주소를 먼저 설정하세요.");

        if (errors.Count > 0) return null;

        var tags = addresses
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .Select(a => new PlcTagDef(
                a.Trim(),
                a.Trim(),
                PlcAddressInfer.dataType(fsVendor, a)))
            .ToList();

        var connection = new PlcConnectionConfig(
            Name,
            fsVendor,
            IpAddress.Trim(),
            Port,
            LocalEthernet,
            NetworkNumber,
            StationNumber,
            TimeoutMs,
            FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(ScanIntervalMs)),
            Microsoft.FSharp.Collections.ListModule.OfSeq(tags));

        return new PlcGatewayConfig(
            Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { connection }));
    }
}
