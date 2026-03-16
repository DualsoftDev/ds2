using System.Windows;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class DiscardChangesFlowTests
{
    [Fact]
    public void ShouldProceed_returns_false_for_cancel_without_saving()
    {
        var saveCalled = false;

        var proceed = DiscardChangesFlow.ShouldProceed(
            MessageBoxResult.Cancel,
            () =>
            {
                saveCalled = true;
                return true;
            });

        Assert.False(proceed);
        Assert.False(saveCalled);
    }

    [Fact]
    public void ShouldProceed_returns_true_for_no_without_saving()
    {
        var saveCalled = false;

        var proceed = DiscardChangesFlow.ShouldProceed(
            MessageBoxResult.No,
            () =>
            {
                saveCalled = true;
                return false;
            });

        Assert.True(proceed);
        Assert.False(saveCalled);
    }

    [Fact]
    public void ShouldProceed_returns_save_result_for_yes()
    {
        var saveCalled = false;

        var proceed = DiscardChangesFlow.ShouldProceed(
            MessageBoxResult.Yes,
            () =>
            {
                saveCalled = true;
                return false;
            });

        Assert.False(proceed);
        Assert.True(saveCalled);
    }
}
