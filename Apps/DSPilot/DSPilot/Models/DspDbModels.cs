namespace DSPilot.Models;

public class FlowState
{
    public int Id { get; set; }
    public string FlowName { get; set; } = "";
    public int? MT { get; set; }
    public int? WT { get; set; }
    public int? CT { get; set; }
    public string State { get; set; } = "";
    public string? MovingStartName { get; set; }
    public string? MovingEndName { get; set; }
}

public class CallState
{
    public int Id { get; set; }

    /// <summary>
    /// Call 고유 식별자 (AASX Call.Id)
    /// </summary>
    public Guid CallId { get; set; }

    public string CallName { get; set; } = "";
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string State { get; set; } = "Ready";
    public double ProgressRate { get; set; }
    public int GoingCount { get; set; }
    public double? AverageGoingTime { get; set; }
    public string? Device { get; set; }
    public string? ErrorText { get; set; }
}

public class DspDbSnapshot
{
    public IReadOnlyList<FlowState> Flows { get; init; } = [];
    public IReadOnlyList<CallState> Calls { get; init; } = [];
    public IReadOnlyDictionary<string, List<CallState>> CallsByFlow { get; init; }
        = new Dictionary<string, List<CallState>>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
