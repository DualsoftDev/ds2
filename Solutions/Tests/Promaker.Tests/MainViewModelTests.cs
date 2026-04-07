using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.Services;
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
    public void ExportCsv_creates_file_for_new_project()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var path = Path.Combine(Path.GetTempPath(), $"promaker-export-{Guid.NewGuid():N}.csv");
            try
            {
                var export = typeof(MainViewModel).GetMethod(
                    "ExportCsvToPath",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                var result = (bool)export.Invoke(vm, [path])!;

                Assert.True(result);
                Assert.True(File.Exists(path));

                var content = File.ReadAllText(path);
                Assert.Contains("Flow", content, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    [Fact]
    public void ShowProjectSettings_updates_project_name_from_dialog()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            SetDialogService(vm, new StubDialogService(dialog =>
            {
                var projectDialog = Assert.IsType<ProjectPropertiesDialog>(dialog);
                SetAutoProperty(projectDialog, "ResultProjectName", "ConfiguredProject");
                SetAutoProperty(projectDialog, "ResultAuthor", "TestAuthor");
                SetAutoProperty(projectDialog, "ResultDateTime", DateTimeOffset.Now);
                SetAutoProperty(projectDialog, "ResultVersion", "1.0.0");
                SetAutoProperty(projectDialog, "ResultIriPrefix", "https://dualsoft.com/");
                SetAutoProperty(projectDialog, "ResultSplitDeviceAasx", false);
                SetAutoProperty(projectDialog, "ResultPresetSystemTypes", Array.Empty<string>());

                return true;
            }));

            vm.ShowProjectSettingsCommand.Execute(null);

            var project = Queries.allProjects(GetStore(vm)).Head;
            Assert.Equal("ConfiguredProject", project.Name);
        });
    }

    private static void SetDialogService(MainViewModel vm, IDialogService dialogService)
    {
        typeof(MainViewModel)
            .GetField("_dialogService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, dialogService);
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private static void SetAutoProperty<T>(object target, string propertyName, T value)
    {
        var field = target.GetType()
            .GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, value);
    }

    private sealed class StubDialogService(Func<Window, bool?> showDialog) : IDialogService
    {
        private readonly Func<Window, bool?> _showDialog = showDialog;

        public string? PromptName(string title, string defaultName) => defaultName;
        public bool Confirm(string message, string title) => true;
        public void ShowWarning(string message) { }
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => _showDialog(dialog) == true ? dialog.DataContext as T : null;
        public bool? ShowDialog(Window dialog) => _showDialog(dialog);
    }
}
