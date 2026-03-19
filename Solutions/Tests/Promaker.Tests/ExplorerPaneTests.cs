using Promaker.Controls;
using Xunit;

namespace Promaker.Tests;

public sealed class ExplorerPaneTests
{
    [Fact]
    public void Explorer_layout_keeps_control_above_history()
    {
        StaTestRunner.Run(() =>
        {
            var pane = new ExplorerPane();

            Assert.Equal(1, pane.UpperPanelsHostRow);
            Assert.Equal(3, pane.HistoryPanelRow);
            Assert.True(pane.UpperPanelsHostRow < pane.HistoryPanelRow);
        });
    }
}
