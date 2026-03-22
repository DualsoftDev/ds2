using DSPilot.Engine;
using DSPilot.Engine.Core;

namespace DSPilot.Services;

/// <summary>
/// PLC 태그 상태 추적 서비스 (F# TagStateTracker 래퍼)
/// </summary>
public class PlcTagStateTrackerService
{
    private readonly ILogger<PlcTagStateTrackerService> _logger;
    private readonly TagStateTrackerMutable _tracker;

    public PlcTagStateTrackerService(ILogger<PlcTagStateTrackerService> logger)
    {
        _logger = logger;
        _tracker = new TagStateTrackerMutable();
    }

    /// <summary>
    /// 태그 값 업데이트 및 엣지 상태 반환 (F# TagStateTracker 사용)
    /// </summary>
    public TagEdgeState UpdateTagValue(string tagName, string newValue)
    {
        var state = _tracker.UpdateTagValue(tagName, newValue);

        // 첫 업데이트인 경우
        if (state.PreviousValue == "0" && state.CurrentValue == newValue)
        {
            _logger.LogDebug("Tag '{TagName}' initialized: {Value}", tagName, newValue);
        }
        else if (state.EdgeType == DSPilot.Engine.Core.EdgeType.RisingEdge)
        {
            _logger.LogDebug("Tag '{TagName}': Rising edge detected (0 → 1)", tagName);
        }
        else if (state.EdgeType == DSPilot.Engine.Core.EdgeType.FallingEdge)
        {
            _logger.LogDebug("Tag '{TagName}': Falling edge detected (1 → 0)", tagName);
        }

        return state;
    }

    /// <summary>
    /// 태그 상태 조회
    /// </summary>
    public TagEdgeState? GetState(string tagName)
    {
        return _tracker.GetState(tagName)?.Value;
    }

    /// <summary>
    /// 모든 태그 상태 초기화
    /// </summary>
    public void Reset()
    {
        _tracker.Reset();
        _logger.LogInformation("All tag states cleared");
    }

    /// <summary>
    /// 추적 중인 태그 개수
    /// </summary>
    public int TrackedTagCount => _tracker.TrackedTagCount;
}
