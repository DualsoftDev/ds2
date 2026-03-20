using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainToolbarVisualTests
{
    [Fact]
    public void MainToolbar_tools_group_and_settings_button_use_gear_style_icons()
    {
        StaTestRunner.Run(() =>
        {
            var toolbar = new MainToolbar();

            var toolsButton = Assert.IsType<Button>(toolbar.FindName("ToolsRibbonGroupButton"));
            var toolsContent = Assert.IsType<WrapPanel>(toolbar.FindName("ToolsRibbonContentPanel"));
            var reportToggle = Assert.IsType<ToggleButton>(toolbar.FindName("ReportToggleBtn"));
            var csvToggle = Assert.IsType<ToggleButton>(toolbar.FindName("ToolToggleBtn"));

            var toolsGroupStack = Assert.IsType<StackPanel>(toolsButton.Content);
            var toolsGroupIcon = Assert.IsType<Path>(toolsGroupStack.Children[0]);

            var settingsButton = toolsContent.Children.OfType<Button>().Last();
            var settingsStack = Assert.IsType<StackPanel>(settingsButton.Content);
            var settingsIcon = Assert.IsType<Path>(settingsStack.Children[0]);

            Assert.NotNull(reportToggle);
            Assert.NotNull(csvToggle);
            Assert.Equal(18d, toolsGroupIcon.Width);
            Assert.Equal(18d, settingsIcon.Width);
            Assert.Equal(70d, settingsButton.MinHeight);
        });
    }

    [Fact]
    public void MainToolbar_simulation_work_buttons_are_grouped_under_work_selector()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ShowSimulationRibbonGroupCommand.Execute(null);

            var toolbar = new MainToolbar
            {
                DataContext = vm
            };

            toolbar.Measure(new Size(1200, 200));
            toolbar.Arrange(new Rect(0, 0, 1200, 200));
            toolbar.UpdateLayout();

            var workCombo = Assert.IsType<ComboBox>(toolbar.FindName("SimWorkComboBox"));
            var startButton = Assert.IsType<Button>(toolbar.FindName("ForceWorkStartButton"));
            var resetButton = Assert.IsType<Button>(toolbar.FindName("ForceWorkResetButton"));

            var workStack = Assert.IsType<StackPanel>(workCombo.Parent);
            var buttonsGrid = Assert.IsType<UniformGrid>(startButton.Parent);

            Assert.Same(buttonsGrid, resetButton.Parent);
            Assert.Same(workStack, buttonsGrid.Parent);
        });
    }

    [Fact]
    public void MainToolbar_simulation_speed_combobox_defaults_to_one_x()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ShowSimulationRibbonGroupCommand.Execute(null);

            var toolbar = new MainToolbar
            {
                DataContext = vm
            };

            toolbar.Measure(new Size(1200, 200));
            toolbar.Arrange(new Rect(0, 0, 1200, 200));
            toolbar.UpdateLayout();

            var speedCombo = Assert.IsType<ComboBox>(toolbar.FindName("SimSpeedComboBox"));
            var items = speedCombo.Items.OfType<ComboBoxItem>().ToArray();

            Assert.Equal(1.0, vm.Simulation.SimSpeed);
            Assert.All(items, item => Assert.IsType<double>(item.Tag));
            Assert.Equal(1.0, Assert.IsType<double>(items[1].Tag));
            Assert.Equal(1.0, Assert.IsType<double>(speedCombo.SelectedValue));
        });
    }

    [Fact]
    public void MainToolbar_simulation_start_and_reset_buttons_use_colored_foregrounds()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ShowSimulationRibbonGroupCommand.Execute(null);

            var toolbar = new MainToolbar
            {
                DataContext = vm
            };

            toolbar.Measure(new Size(1200, 200));
            toolbar.Arrange(new Rect(0, 0, 1200, 200));
            toolbar.UpdateLayout();

            var simulationPanel = Assert.IsType<WrapPanel>(toolbar.FindName("SimulationRibbonContentPanel"));
            var topButtons = simulationPanel.Children.OfType<Button>().Take(3).ToArray();

            var startButton = topButtons[0];
            var resetButton = topButtons[2];

            Assert.NotEqual(Brushes.White, startButton.Foreground);
            Assert.NotEqual(Brushes.White, resetButton.Foreground);
        });
    }

    [Fact]
    public void MainToolbar_theme_button_uses_path_icon_instead_of_text_glyph()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ShowToolsRibbonGroupCommand.Execute(null);

            var toolbar = new MainToolbar
            {
                DataContext = vm
            };

            toolbar.Measure(new Size(1200, 200));
            toolbar.Arrange(new Rect(0, 0, 1200, 200));
            toolbar.UpdateLayout();

            var toolsContent = Assert.IsType<WrapPanel>(toolbar.FindName("ToolsRibbonContentPanel"));
            var themeButton = toolsContent.Children.OfType<Button>().First(button => button.Command == vm.ToggleThemeCommand);
            var themeStack = Assert.IsType<StackPanel>(themeButton.Content);

            Assert.IsType<Path>(themeStack.Children[0]);
            Assert.Equal(vm.ThemeButtonText, Assert.IsType<TextBlock>(themeStack.Children[1]).Text);
        });
    }

    [Fact]
    public void MainToolbar_force_work_buttons_follow_toolbar_disabled_visuals()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ShowSimulationRibbonGroupCommand.Execute(null);

            var toolbar = new MainToolbar
            {
                DataContext = vm
            };

            toolbar.Measure(new Size(1200, 200));
            toolbar.Arrange(new Rect(0, 0, 1200, 200));
            toolbar.UpdateLayout();

            var startButton = Assert.IsType<Button>(toolbar.FindName("ForceWorkStartButton"));
            var resetButton = Assert.IsType<Button>(toolbar.FindName("ForceWorkResetButton"));

            Assert.False(startButton.IsEnabled);
            Assert.False(resetButton.IsEnabled);
            Assert.NotEqual(Brushes.White, startButton.Foreground);
            Assert.NotEqual(Brushes.White, resetButton.Foreground);
        });
    }
}
