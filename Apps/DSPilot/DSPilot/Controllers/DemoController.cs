using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Demo(데모 모드) 백도어 API.
///
/// Blazor /pw(PowerTools.razor) 의 계산기 백도어("2747=" 입력 → Demo.ToggleBypass())를
/// 정적 standalone 페이지(/app/pw.html)가 fetch 로 쓸 수 있게 얇게 래핑한다.
/// DemoModeService 는 싱글톤이라 Blazor 와 동일 인스턴스를 공유 — 토글 상태가 그대로 반영된다.
///
/// 중요: 이 컨트롤러는 데모가 차단된(IsBlocked) 상태에서도 동작해야 하는 백도어이므로
/// 전역 데모 게이트 미들웨어의 allowlist(IsAllowedDuringDemoBlock) 에 '/api/demo' 경로가
/// 추가되어 있어야 한다(미추가 시 차단 후 토글 불가 → 영구 잠김). Blazor /pw 와 동일한 면제.
/// 신규 데이터 로직 없음(직렬화 경계).
/// </summary>
[ApiController]
[Route("api/demo")]
public class DemoController : ControllerBase
{
    private readonly DemoModeService _demo;

    public DemoController(DemoModeService demo)
    {
        _demo = demo;
    }

    /// <summary>현재 데모 모드 상태 스냅샷.</summary>
    [HttpGet("status")]
    public ActionResult<DemoStatusDto> GetStatus() => BuildStatus();

    /// <summary>
    /// 바이패스 토글 (PowerTools 계산기 "2747=" 백도어와 동일).
    /// 켜짐 → 데모 제한 무시(FULL), 다시 호출 → 데모 모드 복귀. 갱신된 상태를 반환한다.
    /// </summary>
    [HttpPost("toggle")]
    public ActionResult<DemoStatusDto> Toggle()
    {
        _demo.ToggleBypass();
        return BuildStatus();
    }

    private DemoStatusDto BuildStatus() => new(
        _demo.IsBypassed,
        _demo.IsExpired,
        _demo.IsBlocked,
        (long)_demo.Elapsed.TotalSeconds,
        (long)_demo.Remaining.TotalSeconds);
}

// ── DTO (camelCase 자동: isBypassed, isExpired, isBlocked, elapsedSeconds, remainingSeconds) ──
public record DemoStatusDto(
    bool IsBypassed,
    bool IsExpired,
    bool IsBlocked,
    long ElapsedSeconds,
    long RemainingSeconds);
