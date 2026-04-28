namespace DSPilot.Services.Statistics;

public readonly record struct CallRuntimeStatistics(
    int GoingTime,
    double Average,
    double StdDev,
    int SessionCount,
    int BaseCount,
    int TotalCount);

/// <summary>
/// Per-Call going-time tracker with sliding window (max 100 samples).
/// Pure C# replacement for DSPilot.Engine.RuntimeStatisticsTrackerMutable.
/// </summary>
public sealed class CallStatisticsTracker
{
    private const int MaxSamples = 100;

    private sealed class Entry
    {
        public DateTime? StartTime;
        public readonly List<int> History = new(MaxSamples);
        public int SessionCount;
        public int BaseCount;
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _sync = new();

    public void RecordStart(string callName, int baseCount)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(callName, out var entry))
            {
                entry = new Entry { BaseCount = baseCount };
                _entries[callName] = entry;
            }
            entry.StartTime = DateTime.Now;
        }
    }

    public CallRuntimeStatistics? RecordFinish(string callName)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(callName, out var entry) || entry.StartTime is null)
                return null;

            var finishTime = DateTime.Now;
            var goingTime = (int)(finishTime - entry.StartTime.Value).TotalMilliseconds;
            entry.StartTime = null;

            entry.History.Insert(0, goingTime);
            if (entry.History.Count > MaxSamples)
                entry.History.RemoveAt(entry.History.Count - 1);

            var average = ComputeAverage(entry.History);
            var stdDev = ComputeStdDev(entry.History, average);
            entry.SessionCount++;

            return new CallRuntimeStatistics(
                goingTime,
                average,
                stdDev,
                entry.SessionCount,
                entry.BaseCount,
                entry.BaseCount + entry.SessionCount);
        }
    }

    public void ResetAllSessions()
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
            {
                entry.StartTime = null;
                entry.History.Clear();
                entry.SessionCount = 0;
            }
        }
    }

    public int TrackedCallCount
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    private static double ComputeAverage(List<int> samples)
    {
        if (samples.Count == 0) return 0;
        long sum = 0;
        foreach (var v in samples) sum += v;
        return (double)sum / samples.Count;
    }

    private static double ComputeStdDev(List<int> samples, double average)
    {
        if (samples.Count == 0) return 0;
        double sumSq = 0;
        foreach (var v in samples)
        {
            var d = v - average;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / samples.Count);
    }
}
