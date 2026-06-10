using log4net.Core;
using Promaker.ViewModels.Logging;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// SimEventLog → AppLogState 통합 후 Simulation 카테고리 routing 검증.
/// </summary>
public class AppLogStateSimulationCategoryTests
{
    [Fact]
    public void Enqueue_with_logger_and_category_records_entry()
    {
        StaTestRunner.Run(() =>
        {
            var state = AppLogState.Instance;
            var before = state.Entries.Count;

            state.Enqueue(Level.Info, "Simulation", "[00:00:01.000] hello", category: "Going");

            // 16ms coalesce — UI dispatcher flush 까지 pump.
            var ok = StaTestRunner.WaitUntil(1000, () => state.Entries.Count > before);
            Assert.True(ok, "Simulation log 가 통합 AppLog 에 기록되어야 합니다.");

            var entry = state.Entries[state.Entries.Count - 1];
            Assert.Equal("Simulation", entry.Logger);
            Assert.Equal("Going", entry.Category);
            Assert.Equal(Level.Info, entry.Level);
        });
    }

    [Fact]
    public void AppLogEntry_appender_path_has_null_category()
    {
        // appender 가 만드는 일반 entry 는 Category=null (도메인 색 미적용).
        var entry = new AppLogEntry(seq: 1, timestamp: System.DateTime.Now,
                                    level: Level.Info, logger: "Promaker.X", message: "msg");
        Assert.Null(entry.Category);
    }
}
