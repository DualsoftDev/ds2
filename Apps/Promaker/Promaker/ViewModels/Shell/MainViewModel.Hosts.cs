using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Ds2.Store;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    public abstract class HostBase
    {
        protected readonly MainViewModel Owner;

        protected HostBase(MainViewModel owner)
        {
            Owner = owner;
        }

        public DsStore Store => Owner._store;

        public bool TryAction(Action action, string? statusOverride = null) =>
            Owner.TryEditorAction(action, statusOverride: statusOverride);

        public bool TryFunc<T>(Func<T> func, out T value, T fallback, string? statusOverride = null) =>
            Owner.TryEditorFunc(func, out value, fallback, statusOverride: statusOverride);

        public bool TryRef<T>(Func<T> func, [NotNullWhen(true)] out T? value, string? statusOverride = null)
            where T : class =>
            Owner.TryEditorRef(func, out value, statusOverride: statusOverride);

        public void RequestRebuildAll(Action? afterRebuild = null) => Owner.RequestRebuildAll(afterRebuild);

        public void SetStatusText(string text) => Owner.StatusText = text;
    }

    public sealed class CanvasHost : HostBase
    {
        public CanvasHost(MainViewModel owner)
            : base(owner)
        {
        }

        public SelectionState Selection => Owner.Selection;
        public EntityNode? SelectedNode => Owner.SelectedNode;
        public ObservableCollection<EntityNode> ControlTreeRoots => Owner.ControlTreeRoots;
        public ObservableCollection<EntityNode> DeviceTreeRoots => Owner.DeviceTreeRoots;
        public bool HasProject => Owner.HasProject;

        public void ExpandNodeAndAncestors(Guid nodeId) => Owner.Selection.ExpandNodeAndAncestors(nodeId);
        public void NotifyCommandStatesChanged() => Owner.RefreshEditorCommandStates();

        public void SelectNodeFromCanvas(EntityNode node, bool ctrlPressed, bool shiftPressed)
        {
            Owner.Selection.SelectNodeFromCanvas(node, ctrlPressed, shiftPressed);
            Owner.Simulation.ClearWarning(node.Id);
        }
    }

    public sealed class PropertyPanelHost : HostBase
    {
        public PropertyPanelHost(MainViewModel owner)
            : base(owner)
        {
        }

        public EntityNode? SelectedNode => Owner.SelectedNode;

        public void RenameSelected(string newName) => Owner.RenameSelectedCommand.Execute(newName);

        public void OpenParentCanvasAndFocusNode(Guid entityId, EntityKind entityKind) =>
            Owner.Canvas.OpenParentCanvasAndFocusNode(entityId, entityKind);

        public bool ShowOwnedDialog(Window dialog)
        {
            if (Application.Current.MainWindow is { } owner)
                dialog.Owner = owner;

            return dialog.ShowDialog() == true;
        }
    }

    public sealed class SelectionHost : HostBase
    {
        public SelectionHost(MainViewModel owner)
            : base(owner)
        {
        }

        public ObservableCollection<EntityNode> ControlTreeRoots => Owner.ControlTreeRoots;
        public ObservableCollection<EntityNode> DeviceTreeRoots => Owner.DeviceTreeRoots;
        public ObservableCollection<EntityNode> CanvasNodes => Owner.CanvasManager.ActivePane.CanvasNodes;
        public ObservableCollection<ArrowNode> CanvasArrows => Owner.CanvasManager.ActivePane.CanvasArrows;

        public EntityNode? SelectedNode
        {
            get => Owner.SelectedNode;
            set => Owner.SelectedNode = value;
        }

        public ArrowNode? SelectedArrow
        {
            get => Owner.SelectedArrow;
            set => Owner.SelectedArrow = value;
        }

        public void NotifyCommandStatesChanged() => Owner.RefreshEditorCommandStates();
    }
}
