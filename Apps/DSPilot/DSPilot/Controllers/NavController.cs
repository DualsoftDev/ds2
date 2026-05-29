using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Nav API — 정적 셸(/app/shell.js)이 Blazor 사이드바(NavMenu.razor)와
/// 동일한 per-system flow 트리 + PLC 디버그 노출 여부를 그릴 수 있도록 데이터만 내려준다.
/// 신규 로직 없음: NavMenu.razor 가 OnInitialized 에서 하던 호출을 그대로 얇게 래핑.
///   - ShowPlcDebug = AppSettingsService.LoadSettings().Ui.ShowPlcDebug
///   - systems = DsProjectService.GetActiveSystems() → 각 system 의 GetFlows(system.Id)
///     (NavMenu 와 동일하게 flow 가 1개 이상인 시스템만 포함)
/// camelCase 자동(MVC 기본값): { showPlcDebug, systems:[{ name, flows:[string] }] }.
/// </summary>
[ApiController]
[Route("api/nav")]
public class NavController : ControllerBase
{
    private readonly DsProjectService _project;
    private readonly AppSettingsService _settings;

    public NavController(DsProjectService project, AppSettingsService settings)
    {
        _project = project;
        _settings = settings;
    }

    [HttpGet]
    public ActionResult<NavDto> Get()
    {
        var showPlcDebug = _settings.LoadSettings().Ui.ShowPlcDebug;

        var systems = new List<NavSystemDto>();
        if (_project.IsLoaded)
        {
            foreach (var system in _project.GetActiveSystems())
            {
                var flows = _project.GetFlows(system.Id);
                // NavMenu.razor 와 동일: flow 가 있는 시스템만 노출.
                if (flows.Count > 0)
                {
                    systems.Add(new NavSystemDto(
                        system.Name,
                        flows.Select(f => f.Name).ToList()));
                }
            }
        }

        return new NavDto(showPlcDebug, systems);
    }
}

// ── DTOs (camelCase 자동: showPlcDebug, systems, name, flows) ──

public record NavDto(bool ShowPlcDebug, List<NavSystemDto> Systems);

public record NavSystemDto(string Name, List<string> Flows);
