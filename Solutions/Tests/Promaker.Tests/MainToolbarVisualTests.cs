using System.Windows;
using System.Windows.Controls;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainToolbarVisualTests
{
    [Fact]
    public void MainToolbar_wraps_simulation_and_splits_theme_and_settings_with_separators()
    {
        StaTestRunner.Run(() =>
        {
            var toolbar = new MainToolbar
            {
                DataContext = new MainViewModel()
            };

            toolbar.Measure(new Size(1800, 180));
            toolbar.Arrange(new Rect(0, 0, 1800, 180));
            toolbar.UpdateLayout();

            var simulationSection = Assert.IsType<Grid>(toolbar.FindName("SimulationSection"));
            var simulationSeparator = Assert.IsType<Border>(toolbar.FindName("SimulationSettingsSeparator"));
            var settingsSection = Assert.IsType<Grid>(toolbar.FindName("SettingsSection"));
            var settingsPanel = Assert.IsType<Grid>(toolbar.FindName("SettingsRibbonContentPanel"));
            var themeSeparator = Assert.IsType<Border>(toolbar.FindName("ThemeSettingsSeparator"));
            var themeButton = Assert.IsType<Button>(toolbar.FindName("ThemeButton"));
            var settingsButton = Assert.IsType<Button>(toolbar.FindName("SettingsButton"));

            Assert.Equal(8, Grid.GetColumn(simulationSection));
            Assert.Equal(9, Grid.GetColumn(simulationSeparator));
            Assert.Equal(11, Grid.GetColumn(settingsSection));

            Assert.Equal(0, Grid.GetColumn(themeButton));
            Assert.Equal(1, Grid.GetColumn(themeSeparator));
            Assert.Equal(2, Grid.GetColumn(settingsButton));
            Assert.Equal(3, settingsPanel.ColumnDefinitions.Count);
        });
    }
}
