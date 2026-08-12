using log4net.Core;
using Promaker.ViewModels.Logging;
using Xunit;

namespace Promaker.Tests;

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

            var ok = StaTestRunner.WaitUntil(1000, () => state.Entries.Count > before);
            Assert.True(ok, "Simulation log must be recorded in the integrated application log.");

            var entry = state.Entries[state.Entries.Count - 1];
            Assert.Equal("Simulation", entry.Logger);
            Assert.Equal("Going", entry.Category);
            Assert.Equal(Level.Info, entry.Level);
        });
    }
}
