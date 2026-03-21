using System;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class EditorCanvasConnectTests
{
    [Fact]
    public void ConnectShortcut_uses_only_f3()
    {
        Assert.True(EditorCanvas.IsConnectShortcutKey(System.Windows.Input.Key.F3));
        Assert.False(EditorCanvas.IsConnectShortcutKey(System.Windows.Input.Key.F4));
    }

    [Fact]
    public void ResolveConnectSourceEntityType_returns_work_for_selected_work()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var work = new EntityNode(Guid.NewGuid(), EntityKind.Work, "WorkA");

            vm.Canvas.CanvasNodes.Add(work);
            vm.SelectedNode = work;

            var resolved = EditorCanvas.TryResolveConnectSourceEntityType(vm, out var entityType);

            Assert.True(resolved);
            Assert.Equal(EntityKind.Work, entityType);
        });
    }

    [Fact]
    public void ResolveConnectSourceEntityType_returns_call_for_selected_call()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var call = new EntityNode(Guid.NewGuid(), EntityKind.Call, "CallA");

            vm.Canvas.CanvasNodes.Add(call);
            vm.SelectedNode = call;

            var resolved = EditorCanvas.TryResolveConnectSourceEntityType(vm, out var entityType);

            Assert.True(resolved);
            Assert.Equal(EntityKind.Call, entityType);
        });
    }

    [Fact]
    public void ResolveConnectSourceEntityType_prefers_ordered_selection_type()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var work1 = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Work1");
            var work2 = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Work2");
            var call = new EntityNode(Guid.NewGuid(), EntityKind.Call, "CallA");

            vm.Canvas.CanvasNodes.Add(work1);
            vm.Canvas.CanvasNodes.Add(work2);
            vm.Canvas.CanvasNodes.Add(call);

            vm.Selection.SelectNodeFromCanvas(work1, ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectNodeFromCanvas(work2, ctrlPressed: true, shiftPressed: false);
            vm.SelectedNode = call;

            var resolved = EditorCanvas.TryResolveConnectSourceEntityType(vm, out var entityType);

            Assert.True(resolved);
            Assert.Equal(EntityKind.Work, entityType);
        });
    }
}
