using System;
using System.Reflection;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class PropertyPanelMultiSelectionTests
{
    [Fact]
    public void Multi_selected_works_apply_period_to_all_selected_items()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flow = Queries.flowsOf(systemId, store).Head;
            var work1Id = store.AddWork("Work1", flow.Id);
            var work2Id = store.AddWork("Work2", flow.Id);

            var work1Node = new EntityNode(work1Id, EntityKind.Work, $"{flow.Name}.Work1");
            var work2Node = new EntityNode(work2Id, EntityKind.Work, $"{flow.Name}.Work2");
            vm.Canvas.CanvasNodes.Add(work1Node);
            vm.Canvas.CanvasNodes.Add(work2Node);

            vm.Selection.SelectNodeFromCanvas(work1Node, ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectNodeFromCanvas(work2Node, ctrlPressed: true, shiftPressed: false);

            Assert.True(vm.PropertyPanel.IsMultiSelection);
            Assert.True(vm.PropertyPanel.IsWorkSelected);

            vm.PropertyPanel.WorkPeriodMs = 2500;
            vm.PropertyPanel.ApplyWorkPeriodCommand.Execute(null);

            Assert.Equal(2500.0, store.Works[work1Id].SimulationProperties!.Value.Duration!.Value.TotalMilliseconds);
            Assert.Equal(2500.0, store.Works[work2Id].SimulationProperties!.Value.Duration!.Value.TotalMilliseconds);
        });
    }

    [Fact]
    public void Multi_selected_works_apply_token_role_flag_to_all_selected_items()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flow = Queries.flowsOf(systemId, store).Head;
            var work1Id = store.AddWork("Work1", flow.Id);
            var work2Id = store.AddWork("Work2", flow.Id);

            store.UpdateWorkTokenRole(work2Id, TokenRole.Ignore);

            var work1Node = new EntityNode(work1Id, EntityKind.Work, $"{flow.Name}.Work1");
            var work2Node = new EntityNode(work2Id, EntityKind.Work, $"{flow.Name}.Work2");
            vm.Canvas.CanvasNodes.Add(work1Node);
            vm.Canvas.CanvasNodes.Add(work2Node);

            vm.Selection.SelectNodeFromCanvas(work1Node, ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectNodeFromCanvas(work2Node, ctrlPressed: true, shiftPressed: false);

            Assert.True(vm.PropertyPanel.IsWorkSelected);
            Assert.Null(vm.PropertyPanel.IsTokenIgnore);

            vm.PropertyPanel.IsTokenSource = true;

            Assert.Equal(TokenRole.Source, store.Works[work1Id].TokenRole);
            Assert.Equal(TokenRole.Source | TokenRole.Ignore, store.Works[work2Id].TokenRole);
        });
    }

    [Fact]
    public void Setting_IsTokenIgnore_to_null_clears_flag_instead_of_being_ignored()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flow = Queries.flowsOf(systemId, store).Head;
            var workId = store.AddWork("Work1", flow.Id);

            store.UpdateWorkTokenRole(workId, TokenRole.Ignore);

            var workNode = new EntityNode(workId, EntityKind.Work, $"{flow.Name}.Work1");
            vm.Canvas.CanvasNodes.Add(workNode);
            vm.Selection.SelectNodeFromCanvas(workNode, ctrlPressed: false, shiftPressed: false);

            Assert.Equal(true, vm.PropertyPanel.IsTokenIgnore);

            // Three-state checkbox cycles true → null; null should be treated as "uncheck"
            vm.PropertyPanel.IsTokenIgnore = null;

            Assert.Equal(TokenRole.None, store.Works[workId].TokenRole);
            Assert.Equal(false, vm.PropertyPanel.IsTokenIgnore);
        });
    }

    [Fact]
    public void Multi_selected_calls_apply_timeout_to_all_selected_items()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flow = Queries.flowsOf(systemId, store).Head;
            var work1Id = store.AddWork("Work1", flow.Id);
            var work2Id = store.AddWork("Work2", flow.Id);

            store.AddCallsWithDevice(projectId, work1Id, ["Dev.Api1"], true, null);
            store.AddCallsWithDevice(projectId, work2Id, ["Dev.Api2"], true, null);

            var call1 = Queries.callsOf(work1Id, store).Head;
            var call2 = Queries.callsOf(work2Id, store).Head;

            var call1Node = new EntityNode(call1.Id, EntityKind.Call, call1.Name);
            var call2Node = new EntityNode(call2.Id, EntityKind.Call, call2.Name);
            vm.Canvas.CanvasNodes.Add(call1Node);
            vm.Canvas.CanvasNodes.Add(call2Node);

            vm.Selection.SelectNodeFromCanvas(call1Node, ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectNodeFromCanvas(call2Node, ctrlPressed: true, shiftPressed: false);

            Assert.True(vm.PropertyPanel.IsMultiSelection);
            Assert.True(vm.PropertyPanel.IsCallSelected);

            vm.PropertyPanel.CallTimeoutMs = 1800;
            vm.PropertyPanel.ApplyCallTimeoutCommand.Execute(null);

            Assert.Equal(1800.0, store.Calls[call1.Id].SimulationProperties!.Value.Timeout!.Value.TotalMilliseconds);
            Assert.Equal(1800.0, store.Calls[call2.Id].SimulationProperties!.Value.Timeout!.Value.TotalMilliseconds);
        });
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }
}
