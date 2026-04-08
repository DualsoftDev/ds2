using System;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SplitCanvasContainerTests
{
    [Fact]
    public void Changing_data_context_unwires_old_secondary_pane_callbacks()
    {
        StaTestRunner.Run(() =>
        {
            var vm1 = new MainViewModel();
            var vm2 = new MainViewModel();

            var keepTab = new CanvasTab(Guid.NewGuid(), TabKind.System, "System");
            var moveTab = new CanvasTab(Guid.NewGuid(), TabKind.Work, "Work");
            vm1.CanvasManager.PrimaryPane.AddTab(keepTab);
            vm1.CanvasManager.PrimaryPane.AddTab(moveTab);
            vm1.CanvasManager.SplitTab(moveTab, SplitSide.Right);

            var oldSecondaryPane = vm1.CanvasManager.SecondaryPane!;
            var container = new SplitCanvasContainer();
            container.DataContext = vm1;

            Assert.NotNull(oldSecondaryPane.CenterOnNodeRequested);
            Assert.NotNull(oldSecondaryPane.FitToViewZoomOutRequested);
            Assert.NotNull(oldSecondaryPane.ApplyZoomCenteredRequested);
            Assert.NotNull(oldSecondaryPane.GetViewportCenterRequested);

            container.DataContext = vm2;

            Assert.Null(oldSecondaryPane.CenterOnNodeRequested);
            Assert.Null(oldSecondaryPane.FitToViewZoomOutRequested);
            Assert.Null(oldSecondaryPane.ApplyZoomCenteredRequested);
            Assert.Null(oldSecondaryPane.GetViewportCenterRequested);
        });
    }
}
