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

            Assert.Equal(1.0, vm.Simulation.SimSpeed);
            Assert.All(items, item => Assert.IsType<double>(item.Tag));
            Assert.Equal(1.0, Assert.IsType<double>(items[1].Tag));
            Assert.Equal(1.0, Assert.IsType<double>(speedCombo.SelectedValue));
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
