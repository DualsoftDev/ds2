// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// 데모 모드: 서비스 기동 후 일정 시간이 지나면 모든 페이지 접근을 차단한다.
/// 백도어(`/pw` 계산기에 2747=)로 바이패스 마커 파일을 켜면
/// 제한 시간을 무시하고 접속 가능하며, 동일 입력으로 다시 끄면 데모 모드로 복귀한다.
/// 바이패스 상태는 파일(.demo-bypass)로 영속화되어 재기동 후에도 유지된다.
/// </summary>
public sealed class DemoModeService
{
    public static readonly TimeSpan DemoDuration = TimeSpan.FromHours(3);

    private readonly string _bypassMarkerPath;
    private readonly ILogger<DemoModeService> _logger;

    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public event Action? StateChanged;

    public DemoModeService(IWebHostEnvironment env, ILogger<DemoModeService> logger)
    {
        _bypassMarkerPath = Path.Combine(env.ContentRootPath, ".demo-bypass");
        _logger = logger;
        _logger.LogInformation(
            "[Demo] DemoModeService started. duration={Duration}, bypass={Bypass}",
            DemoDuration, IsBypassed);
    }

    public bool IsBypassed => File.Exists(_bypassMarkerPath);

    public TimeSpan Elapsed => DateTime.UtcNow - StartedAtUtc;

    public TimeSpan Remaining
    {
        get
        {
            var r = DemoDuration - Elapsed;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
    }

    public bool IsExpired => Elapsed >= DemoDuration;

    public bool IsBlocked => IsExpired && !IsBypassed;

    public void ToggleBypass()
    {
        try
        {
            if (IsBypassed)
            {
                File.Delete(_bypassMarkerPath);
                _logger.LogWarning("[Demo] bypass disabled — demo mode active");
            }
            else
            {
                File.WriteAllText(_bypassMarkerPath, DateTime.UtcNow.ToString("O"));
                _logger.LogWarning("[Demo] bypass enabled — full access");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Demo] bypass marker write/delete failed: {Path}", _bypassMarkerPath);
        }

        StateChanged?.Invoke();
    }
}
