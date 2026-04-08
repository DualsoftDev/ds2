using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainToolbarVisualTests
{
    [Fact]
    public void Simulation_toolbar_buttons_follow_public_command_state()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var toolbar = CreateToolbar(vm);

            var startButton = FindRequiredDescendant<Button>(toolbar, "SimulationStartStopButton");
            var pauseButton = FindRequiredDescendant<Button>(toolbar, "SimulationPauseStepButton");
            var tokenButton = FindRequiredDescendant<Button>(toolbar, "OpenTokenSpecButton");

            Assert.False(startButton.IsEnabled);
            Assert.False(pauseButton.IsEnabled);
            Assert.False(tokenButton.IsEnabled);

            vm.NewProjectCommand.Execute(null);
            toolbar.UpdateLayout();

            Assert.True(startButton.IsEnabled);
            Assert.False(pauseButton.IsEnabled);
            Assert.True(tokenButton.IsEnabled);
            Assert.Same(vm.Simulation.StartSimulationCommand, startButton.Command);

            vm.Simulation.IsSimulating = true;
            vm.Simulation.IsSimPaused = false;
            toolbar.UpdateLayout();

            Assert.Same(vm.Simulation.StopSimulationCommand, startButton.Command);
            Assert.Same(vm.Simulation.PauseSimulationCommand, pauseButton.Command);
            Assert.True(pauseButton.IsEnabled);

            vm.Simulation.IsSimPaused = true;
            toolbar.UpdateLayout();

            Assert.Same(vm.Simulation.StepSimulationCommand, pauseButton.Command);
        });
    }

    [Fact]
    public void Connect_toolbar_icon_reflects_selected_arrow_type()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var toolbar = CreateToolbar(vm);
            var startIcon = FindRequiredDescendant<Canvas>(toolbar, "ConnectStartIcon");
            var startResetIcon = FindRequiredDescendant<Canvas>(toolbar, "ConnectStartResetIcon");
            var resetResetIcon = FindRequiredDescendant<Canvas>(toolbar, "ConnectResetResetIcon");
            var groupIcon = FindRequiredDescendant<Canvas>(toolbar, "ConnectGroupIcon");

            Assert.Equal(Visibility.Visible, startIcon.Visibility);

            vm.SelectedConnectArrowType = ArrowType.StartReset;
            toolbar.UpdateLayout();
            Assert.Equal(Visibility.Visible, startResetIcon.Visibility);
            Assert.Equal(Visibility.Collapsed, startIcon.Visibility);

            vm.SelectedConnectArrowType = ArrowType.ResetReset;
            toolbar.UpdateLayout();
            Assert.Equal(Visibility.Visible, resetResetIcon.Visibility);
            Assert.Equal(Visibility.Collapsed, startResetIcon.Visibility);

            vm.SelectedConnectArrowType = ArrowType.Group;
            toolbar.UpdateLayout();
            Assert.Equal(Visibility.Visible, groupIcon.Visibility);
            Assert.Equal(Visibility.Collapsed, resetResetIcon.Visibility);
        });
    }

    private static MainToolbar CreateToolbar(MainViewModel vm)
    {
        var toolbar = new MainToolbar
        {
            DataContext = vm
        };

        toolbar.Measure(new Size(1800, 180));
        toolbar.Arrange(new Rect(0, 0, 1800, 180));
        toolbar.UpdateLayout();
        return toolbar;
    }

    private static T FindRequiredDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (TryFindDescendant(root, name) is T match)
            return match;

        throw new InvalidOperationException($"Could not find descendant '{name}' of type {typeof(T).Name}.");
    }

    private static FrameworkElement? TryFindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
            return element;

        foreach (var child in GetVisualChildren(root))
        {
            var match = TryFindDescendant(child, name);
            if (match is not null)
                return match;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var match = TryFindDescendant(child, name);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static DependencyObject[] GetVisualChildren(DependencyObject root)
    {
        if (root is not Visual && root is not Visual3D)
            return [];

        var count = VisualTreeHelper.GetChildrenCount(root);
        var children = new DependencyObject[count];
        for (var i = 0; i < count; i++)
            children[i] = VisualTreeHelper.GetChild(root, i);
        return children;
    }
}
