using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void HandleUiOperationException(
        string operation,
        Exception ex,
        string? statusOverride = null,
        bool warnDialog = false)
    {
        Log.Error($"UI operation failed: {operation}", ex);
        StatusText = statusOverride ?? $"[ERROR] {operation} failed. See log.";

        if (warnDialog)
            _dialogService.ShowWarning($"{operation} failed: {ex.Message}");
    }

    private bool TryEditorAction(
        Action action,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(action))] string operation = "")
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
    }

    private bool TryEditorFunc<T>(
        Func<T> func,
        out T result,
        T fallback = default!,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(func))] string operation = "")
    {
        try
        {
            result = func();
            return true;
        }
        catch (Exception ex)
        {
            result = fallback;
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
    }

    private bool TryEditorRef<T>(
        Func<T> func,
        [NotNullWhen(true)] out T? result,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(func))] string operation = "")
        where T : class
    {
        if (TryEditorFunc(func, out var raw, fallback: default!, statusOverride, warnDialog, operation) && raw is not null)
        {
            result = raw;
            return true;
        }

        result = null;
        return false;
    }


    private bool TryGetSelectedNode(EntityKind entityType, [NotNullWhen(true)] out EntityNode? node)
    {
        node = SelectedNode is { } selected && selected.EntityType == entityType
            ? selected
            : null;
        return node is not null;
    }

    public bool TryMoveEntitiesFromCanvas(IReadOnlyList<MoveEntityRequest> requests) =>
        TryEditorAction(() => _store.MoveEntities(requests),
            statusOverride: "[ERROR] Failed to move selected nodes.");

    public bool TryReconnectArrowFromCanvas(Guid arrowId, bool replaceSource, Guid newEndpointId)
    {
        if (!TryEditorFunc(
                () => _store.ReconnectArrow(arrowId, replaceSource, newEndpointId),
                out bool changed,
                fallback: false,
                statusOverride: "[ERROR] Failed to reconnect arrow."))
            return false;

        return changed;
    }

    public bool TryUpdateArrowType(Guid arrowId, ArrowType newArrowType) =>
        TryEditorFunc(
            () => _store.UpdateArrowType(arrowId, newArrowType),
            out bool _,
            fallback: false,
            statusOverride: "[ERROR] Failed to change arrow type.");

    public bool TryConnectNodesFromCanvas(Guid sourceId, Guid targetId, ArrowType arrowType)
    {
        if (!TryEditorFunc(
                () => _store.ConnectSelectionInOrder(new Guid[] { sourceId, targetId }, arrowType),
                out int createdCount,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

        return createdCount > 0;
    }
}
