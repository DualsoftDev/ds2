namespace DSPilot.Services;

/// <summary>
/// EV2/PLC 이벤트를 메모리에 유지하고 실시간 신호 타임라인용 구간 상태를 계산한다.
/// 차트는 이 버퍼만 참조하며 DB를 직접 읽지 않는다.
/// </summary>
public sealed class SignalTimelineBufferService
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly Dictionary<string, TagTimelineBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnUpdated;

    public void PublishBatch(IEnumerable<SignalTimelineSample> samples)
    {
        var updated = false;
        var now = DateTime.Now;

        lock (_lock)
        {
            foreach (var sample in samples)
            {
                if (string.IsNullOrWhiteSpace(sample.Address))
                    continue;

                updated |= PublishSampleLocked(sample);
            }

            TrimLocked(now);
        }

        if (updated)
        {
            OnUpdated?.Invoke();
        }
    }

    public Dictionary<string, bool[]> BuildSegmentStates(
        IEnumerable<string> addresses,
        DateTime windowEnd,
        int timeRangeSeconds,
        int timeSegments)
    {
        var distinctAddresses = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        if (distinctAddresses.Count == 0 || timeRangeSeconds <= 0 || timeSegments <= 0)
            return result;

        var windowStart = windowEnd.AddSeconds(-timeRangeSeconds);
        var segmentDurationSeconds = (double)timeRangeSeconds / timeSegments;

        lock (_lock)
        {
            foreach (var address in distinctAddresses)
            {
                if (!_buffers.TryGetValue(address, out var buffer) || buffer.Transitions.Count == 0)
                {
                    result[address] = new bool[timeSegments];
                    continue;
                }

                var transitions = buffer.Transitions;
                var segmentStates = new bool[timeSegments];
                var transitionIndex = 0;
                var currentState = false;

                while (transitionIndex < transitions.Count && transitions[transitionIndex].Timestamp <= windowStart)
                {
                    currentState = transitions[transitionIndex].State;
                    transitionIndex++;
                }

                for (var segmentIndex = 0; segmentIndex < timeSegments; segmentIndex++)
                {
                    var segmentEnd = windowStart.AddSeconds((segmentIndex + 1) * segmentDurationSeconds);
                    var segmentWasOn = currentState;

                    while (transitionIndex < transitions.Count && transitions[transitionIndex].Timestamp <= segmentEnd)
                    {
                        if (currentState)
                        {
                            segmentWasOn = true;
                        }

                        currentState = transitions[transitionIndex].State;
                        if (currentState)
                        {
                            segmentWasOn = true;
                        }

                        transitionIndex++;
                    }

                    segmentStates[segmentIndex] = segmentWasOn;
                }

                result[address] = segmentStates;
            }
        }

        return result;
    }

    public SignalTimelineWindow BuildTimelineWindow(
        IEnumerable<string> addresses,
        int timeRangeSeconds)
    {
        var distinctAddresses = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var emptyEnd = DateTime.Now;
        if (distinctAddresses.Count == 0 || timeRangeSeconds <= 0)
        {
            return new SignalTimelineWindow(
                emptyEnd.AddSeconds(-Math.Max(timeRangeSeconds, 1)),
                emptyEnd,
                new Dictionary<string, List<SignalActiveRange>>(StringComparer.OrdinalIgnoreCase));
        }

        lock (_lock)
        {
            DateTime? latestTimestamp = null;

            foreach (var address in distinctAddresses)
            {
                if (_buffers.TryGetValue(address, out var buffer) && buffer.Transitions.Count > 0)
                {
                    var candidate = buffer.Transitions[^1].Timestamp;
                    latestTimestamp = latestTimestamp is null || candidate > latestTimestamp
                        ? candidate
                        : latestTimestamp;
                }
            }

            var windowEnd = latestTimestamp ?? emptyEnd;
            var windowStart = windowEnd.AddSeconds(-timeRangeSeconds);
            var rangesByAddress = new Dictionary<string, List<SignalActiveRange>>(StringComparer.OrdinalIgnoreCase);

            foreach (var address in distinctAddresses)
            {
                if (!_buffers.TryGetValue(address, out var buffer) || buffer.Transitions.Count == 0)
                {
                    rangesByAddress[address] = new List<SignalActiveRange>();
                    continue;
                }

                rangesByAddress[address] = BuildActiveRangesLocked(
                    buffer.Transitions,
                    windowStart,
                    windowEnd);
            }

            return new SignalTimelineWindow(windowStart, windowEnd, rangesByAddress);
        }
    }

    private bool PublishSampleLocked(SignalTimelineSample sample)
    {
        var normalizedTimestamp = NormalizeTimestamp(sample.Timestamp);
        var normalizedState = NormalizeValue(sample.Value);

        if (!_buffers.TryGetValue(sample.Address, out var buffer))
        {
            buffer = new TagTimelineBuffer();
            _buffers[sample.Address] = buffer;
        }

        if (buffer.Transitions.Count == 0)
        {
            buffer.Transitions.Add(new SignalStateTransition(normalizedTimestamp, normalizedState));
            return true;
        }

        var lastTransition = buffer.Transitions[^1];
        if (lastTransition.State == normalizedState)
        {
            return false;
        }

        var timestamp = normalizedTimestamp < lastTransition.Timestamp
            ? lastTransition.Timestamp
            : normalizedTimestamp;

        buffer.Transitions.Add(new SignalStateTransition(timestamp, normalizedState));
        return true;
    }

    private static List<SignalActiveRange> BuildActiveRangesLocked(
        List<SignalStateTransition> transitions,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var ranges = new List<SignalActiveRange>();
        if (transitions.Count == 0 || windowEnd <= windowStart)
            return ranges;

        var transitionIndex = 0;
        var currentState = false;
        DateTime? activeRangeStart = null;

        while (transitionIndex < transitions.Count && transitions[transitionIndex].Timestamp <= windowStart)
        {
            currentState = transitions[transitionIndex].State;
            transitionIndex++;
        }

        if (currentState)
        {
            activeRangeStart = windowStart;
        }

        while (transitionIndex < transitions.Count && transitions[transitionIndex].Timestamp <= windowEnd)
        {
            var transition = transitions[transitionIndex];

            if (transition.State)
            {
                if (!currentState)
                {
                    activeRangeStart = transition.Timestamp < windowStart
                        ? windowStart
                        : transition.Timestamp;
                }
            }
            else if (currentState && activeRangeStart is DateTime start)
            {
                var end = transition.Timestamp > windowEnd
                    ? windowEnd
                    : transition.Timestamp;

                if (end > start)
                {
                    ranges.Add(new SignalActiveRange(start, end));
                }

                activeRangeStart = null;
            }

            currentState = transition.State;
            transitionIndex++;
        }

        if (currentState && activeRangeStart is DateTime tailStart)
        {
            var clippedTailStart = tailStart < windowStart ? windowStart : tailStart;
            if (windowEnd > clippedTailStart)
            {
                ranges.Add(new SignalActiveRange(clippedTailStart, windowEnd));
            }
        }

        return ranges;
    }

    private void TrimLocked(DateTime referenceUtcNow)
    {
        var cutoff = referenceUtcNow - Retention;
        var emptyKeys = new List<string>();

        foreach (var (address, buffer) in _buffers)
        {
            while (buffer.Transitions.Count > 1 && buffer.Transitions[1].Timestamp < cutoff)
            {
                buffer.Transitions.RemoveAt(0);
            }

            if (buffer.Transitions.Count == 0)
            {
                emptyKeys.Add(address);
            }
        }

        foreach (var key in emptyKeys)
        {
            _buffers.Remove(key);
        }
    }

    private static bool NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToLowerInvariant();

        if (normalized is "1" or "true" or "on")
            return true;

        if (normalized is "0" or "false" or "off")
            return false;

        if (normalized.Contains("true", StringComparison.Ordinal))
            return true;

        if (normalized.Contains("false", StringComparison.Ordinal))
            return false;

        if (double.TryParse(normalized, out var numeric))
        {
            return Math.Abs(numeric) > double.Epsilon;
        }

        return false;
    }

    private static DateTime NormalizeTimestamp(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp.ToLocalTime(),
            _ => timestamp
        };
    }

    private sealed class TagTimelineBuffer
    {
        public List<SignalStateTransition> Transitions { get; } = new();
    }
}

public readonly record struct SignalTimelineSample(string Address, string? Value, DateTime Timestamp);

public readonly record struct SignalActiveRange(DateTime StartTime, DateTime EndTime);

public sealed record SignalTimelineWindow(
    DateTime WindowStart,
    DateTime WindowEnd,
    Dictionary<string, List<SignalActiveRange>> RangesByAddress);

internal readonly record struct SignalStateTransition(DateTime Timestamp, bool State);
