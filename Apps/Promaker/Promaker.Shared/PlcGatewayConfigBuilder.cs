using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Backend.Plc;
using Ds2.Runtime.IO;
using Microsoft.FSharp.Core;

namespace Promaker.Shared;

/// <summary>
/// <see cref="PlcConnectionSettings"/> + <see cref="SignalIOMap"/> → <see cref="PlcGatewayConfig"/>.
/// Promaker WPF 의 PLAY 분기와 Promaker.Agent 의 부트스트랩이 동일하게 사용.
/// </summary>
public static class PlcGatewayConfigBuilder
{
    /// <summary>설정 + IO 매핑 + (옵션) 추가 주소 → F# PlcGatewayConfig.
    /// 검증 실패 시 errors 에 사유 누적 후 null 반환.</summary>
    public static PlcGatewayConfig? TryBuild(
        PlcConnectionSettings settings,
        SignalIOMap ioMap,
        out List<string> errors,
        IEnumerable<string>? extraAddresses = null)
    {
        errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.IpAddress)) errors.Add("IP 주소를 입력하세요.");
        if (settings.Port <= 0 || settings.Port > 65535) errors.Add("포트는 1–65535 범위여야 합니다.");
        if (settings.TimeoutMs <= 0) errors.Add("Timeout(ms) 은 양수여야 합니다.");
        if (settings.ScanIntervalMs <= 0) errors.Add("Scan interval(ms) 은 양수여야 합니다.");

        var fsVendor = ParseVendor(settings.Vendor);

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addr in ioMap.OutAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(addr)) addresses.Add(addr);
        foreach (var addr in ioMap.InAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(addr)) addresses.Add(addr);

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

        var transport = settings.IsUdp ? PlcTransport.Udp : PlcTransport.Tcp;
        var connection = new PlcConnectionConfig(
            settings.Name,
            FSharpOption<Guid>.None,
            fsVendor,
            settings.IpAddress.Trim(),
            settings.Port,
            settings.LocalEthernet,
            settings.NetworkNumber,
            settings.StationNumber,
            transport,
            settings.TimeoutMs,
            FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(settings.ScanIntervalMs)),
            Microsoft.FSharp.Collections.ListModule.OfSeq(tags));

        return new PlcGatewayConfig(
            Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { connection }));
    }

    private static PlcVendor ParseVendor(string vendor)
    {
        if (Enum.TryParse<PlcVendorChoice>(vendor, ignoreCase: true, out var v))
            return v switch
            {
                PlcVendorChoice.LsXgi => PlcVendor.LsXgi,
                PlcVendorChoice.LsXgk => PlcVendor.LsXgk,
                PlcVendorChoice.LsXgb => PlcVendor.LsXgb,
                PlcVendorChoice.Mitsubishi => PlcVendor.Mitsubishi,
                _ => PlcVendor.LsXgi
            };
        return PlcVendor.LsXgi;
    }
}
