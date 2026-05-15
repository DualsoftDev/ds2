using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Backend.Plc;
using Ds2.Runtime.IO;
using Microsoft.FSharp.Core;
using Promaker.Services;

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

    /// <summary>Mitsubishi 전송 방식 — true=UDP, false=TCP. LS 에서는 무시 (LS 는 항상 TCP).
    /// 미쓰비시 MC 프로토콜은 PLC 측 Ethernet 모듈 파라미터(GX Works)에서 TCP/UDP 를 정해두면
    /// 클라이언트가 그 모드로 붙어야 함 — 모니터링 통신용으로 UDP 를 쓰는 현장이 흔하다.</summary>
    [ObservableProperty] private bool _isUdp = false;

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
    /// <para>
    /// <paramref name="extraAddresses"/>: UserTag 처럼 IOMap 에는 없지만 모니터링이 필요한 주소.
    /// PLC 스캔 + Hub 브로드캐스트 대상에 포함되며, IOMap 주소와 중복되면 자동 dedup.
    /// </para>
    /// </summary>
    public PlcGatewayConfig? BuildGatewayConfig(
        SignalIOMap ioMap,
        out List<string> errors,
        IEnumerable<string>? extraAddresses = null)
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

        // UserTag 등 추가 주소 — IOMap 에 없으면 PLC 스캔 안 돼 알림이 안 잡힘.
        // dedup 은 HashSet 이 알아서 처리, OUT/IN 과 같은 주소면 그쪽 데이터타입 추론 한 번이면 충분.
        if (extraAddresses is not null)
        {
            foreach (var addr in extraAddresses)
                if (!string.IsNullOrWhiteSpace(addr)) addresses.Add(addr.Trim());
        }

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

        var transport = IsUdp ? PlcTransport.Udp : PlcTransport.Tcp;
        var connection = new PlcConnectionConfig(
            Name,
            fsVendor,
            IpAddress.Trim(),
            Port,
            LocalEthernet,
            NetworkNumber,
            StationNumber,
            transport,
            TimeoutMs,
            FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(ScanIntervalMs)),
            Microsoft.FSharp.Collections.ListModule.OfSeq(tags));

        return new PlcGatewayConfig(
            Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { connection }));
    }

    // ── 영속화 (Load/Save) ──────────────────────────────────────────────
    // 사용자가 다이얼로그에서 마지막으로 입력한 벤더/IP/포트/Timeout/Scan 등을
    // %AppData%\Dualsoft\Promaker\Settings\PlcConnection.json 에 저장. 다음 실행 시 자동 로드.
    // (태그는 IO 매핑에서 자동 import 되므로 저장 안 함.)

    private sealed class PersistedShape
    {
        public string Vendor { get; set; } = nameof(PlcVendorChoice.LsXgi);
        public string Name { get; set; } = "PLC#1";
        public string IpAddress { get; set; } = "192.168.0.10";
        public int Port { get; set; } = 2004;
        public int TimeoutMs { get; set; } = 3000;
        public int ScanIntervalMs { get; set; } = 100;
        public bool LocalEthernet { get; set; } = true;
        public byte NetworkNumber { get; set; } = 0;
        public byte StationNumber { get; set; } = 0xFF;
        public bool IsUdp { get; set; } = false;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>저장된 설정을 읽어 새 PlcSettings 인스턴스 생성. 파일 없으면 default.</summary>
    public static PlcSettings LoadOrDefault()
    {
        var s = new PlcSettings();
        try
        {
            var path = SettingsPaths.PlcConnection;
            if (!File.Exists(path)) return s;
            var text = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PersistedShape>(text, _jsonOpts);
            if (data is null) return s;

            if (Enum.TryParse<PlcVendorChoice>(data.Vendor, ignoreCase: true, out var v))
                s.Vendor = v;
            s.Name = data.Name ?? s.Name;
            s.IpAddress = data.IpAddress ?? s.IpAddress;
            if (data.Port > 0) s.Port = data.Port;
            if (data.TimeoutMs > 0) s.TimeoutMs = data.TimeoutMs;
            if (data.ScanIntervalMs > 0) s.ScanIntervalMs = data.ScanIntervalMs;
            s.LocalEthernet = data.LocalEthernet;
            s.NetworkNumber = data.NetworkNumber;
            s.StationNumber = data.StationNumber;
            s.IsUdp = data.IsUdp;
        }
        catch
        {
            // 손상된 설정 파일은 default 로 fallback — silent
        }
        return s;
    }

    /// <summary>현재 값을 JSON 으로 저장. 실패해도 throw 없이 조용히 반환 (사용자 흐름 막지 않음).</summary>
    public void Save()
    {
        try
        {
            var path = SettingsPaths.PlcConnection;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var data = new PersistedShape
            {
                Vendor = Vendor.ToString(),
                Name = Name,
                IpAddress = IpAddress,
                Port = Port,
                TimeoutMs = TimeoutMs,
                ScanIntervalMs = ScanIntervalMs,
                LocalEthernet = LocalEthernet,
                NetworkNumber = NetworkNumber,
                StationNumber = StationNumber,
                IsUdp = IsUdp,
            };
            var text = JsonSerializer.Serialize(data, _jsonOpts);
            File.WriteAllText(path, text);
        }
        catch { /* best-effort */ }
    }
}
