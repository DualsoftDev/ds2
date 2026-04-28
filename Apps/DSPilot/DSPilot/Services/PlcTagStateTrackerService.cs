using Ds2.Core;

namespace DSPilot.Services;

/// <summary>
/// 태그 엣지 상태 (immutable).
/// </summary>
public sealed record TagEdgeState(
    string TagName,
    string PreviousValue,
    string CurrentValue,
    DateTime LastUpdateTime,
    EdgeType EdgeType);

/// <summary>
/// PLC 태그 상태 추적 서비스 (C# 자체 구현).
/// Previous / Current 문자열 비교로 rising/falling 엣지 검출.
/// </summary>
public class PlcTagStateTrackerService
{
    private readonly ILogger<PlcTagStateTrackerService> _logger;
    private readonly Dictionary<string, TagEdgeState> _state = new();
    private readonly object _sync = new();

    public PlcTagStateTrackerService(ILogger<PlcTagStateTrackerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 태그 값 업데이트 및 엣지 상태 반환.
    /// 최초 업데이트는 NoChange로 초기화.
    /// </summary>
    public TagEdgeState UpdateTagValue(string tagName, string newValue)
    {
        TagEdgeState next;
        lock (_sync)
        {
            var now = DateTime.Now;
            if (!_state.TryGetValue(tagName, out var prev))
            {
                next = new TagEdgeState(tagName, "0", newValue, now, EdgeType.NoChange);
                _state[tagName] = next;
                _logger.LogDebug("Tag '{TagName}' initialized: {Value}", tagName, newValue);
                return next;
            }

            var edge = (prev.CurrentValue, newValue) switch
            {
                ("0", "1") => EdgeType.RisingEdge,
                ("1", "0") => EdgeType.FallingEdge,
                _ => EdgeType.NoChange,
            };
            next = new TagEdgeState(tagName, prev.CurrentValue, newValue, now, edge);
            _state[tagName] = next;
        }

        if (next.EdgeType == EdgeType.RisingEdge)
            _logger.LogDebug("Tag '{TagName}': Rising edge detected (0 → 1)", tagName);
        else if (next.EdgeType == EdgeType.FallingEdge)
            _logger.LogDebug("Tag '{TagName}': Falling edge detected (1 → 0)", tagName);

        return next;
    }

    public TagEdgeState? GetState(string tagName)
    {
        lock (_sync)
        {
            return _state.TryGetValue(tagName, out var state) ? state : null;
        }
    }

    public void Reset()
    {
        lock (_sync) _state.Clear();
        _logger.LogInformation("All tag states cleared");
    }

    public int TrackedTagCount
    {
        get { lock (_sync) return _state.Count; }
    }
}
