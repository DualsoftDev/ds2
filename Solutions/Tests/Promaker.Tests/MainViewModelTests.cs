using System;
using System.Collections.Generic;
using System.Reflection;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void PrepareForLoadedStore_clears_canvas_selection_and_simulation_state()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var nodeId = Guid.NewGuid();
            var arrowId = Guid.NewGuid();

            var node = new EntityNode(nodeId, EntityKind.Work, "Work1");
            var arrow = new ArrowNode(arrowId, nodeId, Guid.NewGuid(), ArrowType.Start);

            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.System, "System"));
            vm.Canvas.CanvasNodes.Add(node);
            vm.Canvas.CanvasArrows.Add(arrow);

            vm.Selection.SelectNodeFromCanvas(node, ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectArrowFromCanvas(arrow, ctrlPressed: false);

            vm.Simulation.HasReportData = true;
            vm.Simulation.SimNodes.Add(new SimNodeRow
            {
                NodeGuid = nodeId,
                Name = "Work1",
                NodeType = "Work",
                SystemName = "SystemA",
                State = Status4.Going
            });
            vm.Simulation.SimEventLog.Add("log");
            vm.Simulation.SimWorkItems.Add(new SimWorkItem(nodeId, "Work1"));
            vm.Simulation.SelectedSimWork = vm.Simulation.SimWorkItems[0];
            vm.Simulation.GanttChart.AddEntry(nodeId, "Work1", EntityKind.Work);

            var clipboard = (List<SelectionKey>)typeof(MainViewModel)
                .GetField("_clipboardSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm)!;
            clipboard.Add(new SelectionKey(Guid.NewGuid(), EntityKind.Work));

            vm.SelectedNode = node;
            vm.SelectedArrow = arrow;

            vm.PrepareForLoadedStore();

            Assert.Null(vm.SelectedNode);
            Assert.Null(vm.SelectedArrow);
            Assert.Empty(vm.Canvas.OpenTabs);
            Assert.Empty(vm.Canvas.CanvasNodes);
            Assert.Empty(vm.Canvas.CanvasArrows);
            Assert.Empty(vm.Selection.OrderedNodeSelection);
            Assert.Empty(vm.Selection.OrderedArrowSelection);
            Assert.Empty(clipboard);
            Assert.False(vm.Simulation.HasReportData);
            Assert.Empty(vm.Simulation.SimNodes);
            Assert.Empty(vm.Simulation.SimEventLog);
            Assert.Empty(vm.Simulation.SimWorkItems);
            Assert.Empty(vm.Simulation.GanttChart.Entries);
        });
    }

    [Fact]
    public void Canvas_quick_create_label_and_enablement_follow_active_workspace()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();

            Assert.Equal("Work / Call", vm.Canvas.ContextualQuickCreateLabel);
            Assert.False(vm.Canvas.QuickAddFlowCommand.CanExecute(null));
            Assert.False(vm.Canvas.QuickAddContextualNodeCommand.CanExecute(null));

            vm.NewProjectCommand.Execute(null);

            Assert.True(vm.Canvas.QuickAddFlowCommand.CanExecute(null));
            Assert.False(vm.Canvas.QuickAddContextualNodeCommand.CanExecute(null));

            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.Flow, "FlowA"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];

            Assert.Equal("Work", vm.Canvas.ContextualQuickCreateLabel);
            Assert.True(vm.Canvas.QuickAddContextualNodeCommand.CanExecute(null));

            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.Work, "WorkA"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[1];

            Assert.Equal("Call", vm.Canvas.ContextualQuickCreateLabel);
            Assert.True(vm.Canvas.QuickAddContextualNodeCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Toolbar_group_commands_switch_selected_ribbon_group()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();

            Assert.Equal(RibbonGroup.Project, vm.SelectedRibbonGroup);

            vm.ShowEditRibbonGroupCommand.Execute(null);
            Assert.Equal(RibbonGroup.Edit, vm.SelectedRibbonGroup);

            vm.ShowSimulationRibbonGroupCommand.Execute(null);
            Assert.Equal(RibbonGroup.Simulation, vm.SelectedRibbonGroup);

            vm.ShowToolsRibbonGroupCommand.Execute(null);
            Assert.Equal(RibbonGroup.Tools, vm.SelectedRibbonGroup);
        });
    }

}
