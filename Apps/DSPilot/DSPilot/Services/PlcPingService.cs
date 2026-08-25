// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSPilot.Infrastructure;

namespace DSPilot.Services;

/// <summary>한 PLC 엔드포인트 직접 핑 결과. UI 가 어댑터 상태로 렌더링.</summary>
public sealed record PlcPingResult(
    string Name, string Vendor, string Ip, int Port, bool Connected, string? Error, DateTime AtUtc);

/// <summary>
/// PLC 어댑터 상태의 폴백 소스. 평소엔 Promaker.Agent 가 Hub 로 PLC 연결 상태를 push 하고
/// <see cref="PlcConnectionStatusTracker"/> 가 캐시하지만, Agent/Hub 가 끊겨 보고가 없을 때
/// (헤더 "실시간 상태"의 PLC 어댑터 행) DSPilot 이 <b>직접</b> 대상 PLC 에 핑을 던져 상태를 보여준다.
///
/// 대상 IP/Port 는 1순위로 로드된 모델의 AID 서브모델(<see cref="DsProjectService.GetPlcEndpoints"/> —
/// 멀티 PLC 정본, 시스템별 엔드포인트)에서, 모델이 없으면 2순위로 공유
/// <see cref="SharedPaths.PlcConnectionFilePath"/>(PlcConnection.json — 단일 활성 연결)에서 읽는다.
/// 핑은 ICMP 가 아니라 PLC 제어 포트로의 <b>TCP connect</b> — 게이트웨이가 실제로
/// 여는 포트와 같은 의미라("호스트가 떠 있나"보다) "PLC 와 통신 가능한가"를 정확히 반영한다.
///
/// /api/nav/summary 는 브라우저마다 4초 주기로 폴링되므로, 결과를 짧은 TTL 로 캐시하고 single-flight
/// (<see cref="_gate"/>)로 동시 핑을 1회로 합쳐 PLC·요청 스레드 부하를 막는다.
/// 멀티 PLC 는 병렬로 핑한다 — 직렬이면 끊긴 PLC 수 × timeout 만큼 응답이 밀린다.
/// </summary>
public sealed class PlcPingService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<PlcPingService> _logger;
    private readonly DsProjectService _project;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<PlcPingResult> _cache = Array.Empty<PlcPingResult>();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;

    public PlcPingService(ILogger<PlcPingService> logger, DsProjectService project)
    {
        _logger = logger;
        _project = project;
    }

    /// <summary>설정된 대상 PLC 를 직접 핑한 결과(TTL 캐시). 대상 미설정이면 빈 목록.</summary>
    public async Task<IReadOnlyList<PlcPingResult>> ProbeAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _cacheExpiresUtc) return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            // double-check — 대기 중 다른 호출이 이미 갱신했을 수 있음.
            if (DateTime.UtcNow < _cacheExpiresUtc) return _cache;

            var endpoints = ReadEndpoints();
            var results = await Task.WhenAll(endpoints.Select(ep => ProbeOneAsync(ep, ct)));

            _cache = results;
            _cacheExpiresUtc = DateTime.UtcNow.Add(CacheTtl);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PlcPingResult> ProbeOneAsync(PlcEndpoint ep, CancellationToken ct)
    {
        // 상태 표시용이라 connect timeout 을 짧게 캡(요청 응답성 보호). 설정 timeout 이 더 짧으면 그걸 존중.
        var timeoutMs = Math.Clamp(ep.TimeoutMs, 300, 2000);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ep.Ip, ep.Port, cts.Token);
            return new PlcPingResult(ep.Name, ep.Vendor, ep.Ip, ep.Port, client.Connected, null, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 우리 timeout 으로 취소된 경우(외부 ct 취소가 아니라) → 응답 없음.
            return new PlcPingResult(ep.Name, ep.Vendor, ep.Ip, ep.Port, false, $"timeout ({timeoutMs}ms)", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new PlcPingResult(ep.Name, ep.Vendor, ep.Ip, ep.Port, false, ex.Message, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 핑 대상 PLC 엔드포인트 목록.
    /// 1순위: 로드된 모델의 AID(시스템별 멀티 PLC — 접속정보 정본).
    /// 2순위: PlcConnection.json(단일 활성 연결 — 모델 미로드/AID 없는 구 모델 폴백).
    /// </summary>
    private List<PlcEndpoint> ReadEndpoints()
    {
        var fromModel = ReadModelEndpoints();
        if (fromModel.Count > 0) return fromModel;
        return ReadLegacySingleEndpoint();
    }

    private List<PlcEndpoint> ReadModelEndpoints()
    {
        try
        {
            return _project.GetPlcEndpoints()
                .Select(e => new PlcEndpoint(
                    e.SystemName, e.Vendor, e.Ip.Trim(), e.Port,
                    e.TimeoutMs > 0 ? e.TimeoutMs : 3000))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PlcPing] 모델 AID 엔드포인트 읽기 실패 — PlcConnection.json 폴백");
            return new List<PlcEndpoint>();
        }
    }

    private List<PlcEndpoint> ReadLegacySingleEndpoint()
    {
        var path = SharedPaths.PlcConnectionFilePath;
        try
        {
            if (!File.Exists(path)) return new List<PlcEndpoint>();
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PlcConnectionFileDto>(json, JsonOpts);
            if (dto is null || string.IsNullOrWhiteSpace(dto.IpAddress) || dto.Port is <= 0 or > 65535)
                return new List<PlcEndpoint>();

            return new List<PlcEndpoint>
            {
                new(
                    string.IsNullOrWhiteSpace(dto.Name) ? "PLC" : dto.Name.Trim(),
                    dto.Vendor ?? "",
                    dto.IpAddress.Trim(),
                    dto.Port,
                    dto.TimeoutMs > 0 ? dto.TimeoutMs : 3000),
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PlcPing] PlcConnection.json 읽기 실패 — 폴백 핑 대상 없음");
            return new List<PlcEndpoint>();
        }
    }

    private sealed record PlcEndpoint(string Name, string Vendor, string Ip, int Port, int TimeoutMs);

    /// <summary>PlcConnection.json(camelCase) 중 핑에 필요한 필드만 — Promaker.Shared 의존 없이 읽기.</summary>
    private sealed class PlcConnectionFileDto
    {
        public string? Name { get; set; }
        public string? Vendor { get; set; }
        public string? IpAddress { get; set; }
        public int Port { get; set; }
        public int TimeoutMs { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}
