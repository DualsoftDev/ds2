using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainToolbarVisualTests
{
    [Fact]
    public void MainToolbar_simulation_speed_combobox_defaults_to_one_x()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var toolbar = CreateToolbar(vm);

            var speedCombo = FindRequiredDescendant<ComboBox>(toolbar, "SimSpeedComboBox");
            var items = speedCombo.Items.OfType<ComboBoxItem>().ToArray();
            var labels = items.Select(item => item.Content?.ToString()).ToArray();

            Assert.Equal(1.0, vm.Simulation.SimSpeed);
            Assert.Equal(6, items.Length);
            Assert.Equal("0.5x", labels[0]);
            Assert.Equal("1x", labels[1]);
            Assert.Equal("2x", labels[2]);
            Assert.Equal("5x", labels[3]);
            Assert.Equal("10x", labels[4]);
            Assert.False(string.IsNullOrWhiteSpace(labels[5]));
        });
    }

    [Fact]
    public void MainToolbar_simulation_controls_bind_to_main_view_model_and_simulation_state()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var toolbar = CreateToolbar(vm);

            var startButton = FindRequiredDescendant<Button>(toolbar, "SimulationStartStopButton");
            var pauseButton = FindRequiredDescendant<Button>(toolbar, "SimulationPauseStepButton");
            var tokenButton = FindRequiredDescendant<Button>(toolbar, "OpenTokenSpecButton");
            var speedCombo = FindRequiredDescendant<ComboBox>(toolbar, "SimSpeedComboBox");

            Assert.False(vm.HasProject);
            Assert.False(startButton.IsEnabled);
            Assert.False(pauseButton.IsEnabled);
            Assert.False(tokenButton.IsEnabled);

            vm.NewProjectCommand.Execute(null);
            toolbar.UpdateLayout();

            Assert.True(vm.HasProject);
            Assert.Same(vm.Simulation.StartSimulationCommand, startButton.Command);
            Assert.Same(vm.OpenTokenSpecDialogCommand, tokenButton.Command);
            Assert.True(startButton.IsEnabled);
            Assert.False(pauseButton.IsEnabled);
            Assert.True(tokenButton.IsEnabled);
            Assert.Equal("1x", ((ComboBoxItem)speedCombo.SelectedItem).Content?.ToString());
            Assert.True(speedCombo.ActualHeight >= 20);

            startButton.Command.Execute(null);
            toolbar.UpdateLayout();

            Assert.True(vm.Simulation.IsSimulating);
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

        foreach (var child in GetLogicalChildren(root))
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

    private static DependencyObject[] GetLogicalChildren(DependencyObject root)
    {
        return LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray();
    }
}
