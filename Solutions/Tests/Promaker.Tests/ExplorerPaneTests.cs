using System;
using System.Windows;
using Promaker.Controls;
using Xunit;

namespace Promaker.Tests;

public sealed class ExplorerPaneTests
{
    [Fact]
    public void Explorer_layout_has_header_and_tree_tabs()
    {
        StaTestRunner.Run(() =>
        {
            EnsureAppResources();
            var pane = new ExplorerPane();

            Assert.Equal(1, pane.UpperPanelsHostRow);
        });
    }

    private static void EnsureAppResources()
    {
        if (Application.Current == null)
            _ = new Application();

        if (Application.Current!.Resources.MergedDictionaries.Count > 0)
            return;

        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Promaker;component/Themes/Theme.Dark.xaml", UriKind.Relative)
        });
    }
}
