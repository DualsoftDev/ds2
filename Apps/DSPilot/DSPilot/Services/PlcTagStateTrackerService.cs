using DSPilot.Models.Dsp;

namespace DSPilot.Services;

/// <summary>
/// PLC 태그 상태 추적 서비스 - 엣지 감지를 위한 이전 값 저장
/// </summary>
public class PlcTagStateTrackerService
{
    private readonly ILogger<PlcTagStateTrackerService> _logger;

    // TagName → TagEdgeState 매핑
    private readonly Dictionary<string, TagEdgeState> _tagStates = new();

    public PlcTagStateTrackerService(ILogger<PlcTagStateTrackerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 태그 값 업데이트 및 엣지 상태 반환
    /// </summary>
    public TagEdgeState UpdateTagValue(string tagName, string newValue)
    {
        if (!_tagStates.TryGetValue(tagName, out var state))
        {
            // 첫 번째 업데이트
            state = new TagEdgeState
            {
                TagName = tagName,
                PreviousValue = "0",  // 초기값
                CurrentValue = newValue,
                LastUpdateTime = DateTime.Now
            };
            _tagStates[tagName] = state;

            _logger.LogDebug("Tag '{TagName}' initialized: {Value}", tagName, newValue);
        }
        else
        {
            // 기존 값 업데이트
            state.PreviousValue = state.CurrentValue;
            state.CurrentValue = newValue;
            state.LastUpdateTime = DateTime.Now;

            // 엣지 감지 로깅
            if (state.IsRisingEdge())
            {
                _logger.LogDebug("Tag '{TagName}': Rising edge detected (0 → 1)", tagName);
            }
            else if (state.IsFallingEdge())
            {
                _logger.LogDebug("Tag '{TagName}': Falling edge detected (1 → 0)", tagName);
            }
        }

        return state;
    }

    /// <summary>
    /// 태그 상태 조회
    /// </summary>
    public TagEdgeState? GetState(string tagName)
    {
        return _tagStates.TryGetValue(tagName, out var state) ? state : null;
    }

    /// <summary>
    /// 모든 태그 상태 초기화
    /// </summary>
    public void Reset()
    {
        _tagStates.Clear();
        _logger.LogInformation("All tag states cleared");
    }

    /// <summary>
    /// 추적 중인 태그 개수
    /// </summary>
    public int TrackedTagCount => _tagStates.Count;
}
