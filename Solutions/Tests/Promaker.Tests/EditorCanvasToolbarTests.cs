using System;
using System.Windows;
using System.Windows.Controls;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class EditorCanvasToolbarTests
{
    [Fact]
    public void EditorCanvas_quick_add_toolbar_follows_active_canvas_context()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var canvas = new EditorCanvas
            {
                DataContext = vm,
                Pane = vm.Canvas
            };

            canvas.Measure(new Size(1200, 800));
            canvas.Arrange(new Rect(0, 0, 1200, 800));
            canvas.UpdateLayout();

            var flowButton = Assert.IsType<Button>(canvas.FindName("FlowQuickAddButton"));
            var contextualButton = Assert.IsType<Button>(canvas.FindName("ContextualQuickAddButton"));
            var contextualText = Assert.IsType<TextBlock>(canvas.FindName("ContextualQuickAddText"));

            Assert.False(flowButton.IsEnabled);
            Assert.False(contextualButton.IsEnabled);
            Assert.Equal("W/C", contextualText.Text);

            vm.NewProjectCommand.Execute(null);
            canvas.UpdateLayout();

            Assert.True(flowButton.IsEnabled);
            Assert.False(contextualButton.IsEnabled);
            Assert.Equal("W/C", contextualText.Text);

            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.Flow, "FlowA"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];
            canvas.UpdateLayout();

            Assert.True(contextualButton.IsEnabled);
            Assert.Equal("W", contextualText.Text);

            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.Work, "WorkA"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[1];
            canvas.UpdateLayout();

            Assert.True(contextualButton.IsEnabled);
            Assert.Equal("C", contextualText.Text);
        });
    }
}
