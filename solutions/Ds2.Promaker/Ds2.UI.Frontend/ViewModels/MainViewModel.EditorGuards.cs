using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;

namespace Ds2.UI.Frontend.ViewModels;

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
            DialogHelpers.Warn($"{operation} failed: {ex.Message}");
    }

    private bool TryEditorAction(
        string operation,
        Action action,
        string? statusOverride = null,
        bool warnDialog = false)
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
        string operation,
        Func<T> action,
        out T result,
        T fallback = default!,
        string? statusOverride = null,
        bool warnDialog = false)
    {
        try
        {
            result = action();
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
        string operation,
        Func<T> action,
        [NotNullWhen(true)] out T? result,
        string? statusOverride = null,
        bool warnDialog = false)
        where T : class
    {
        if (!TryEditorFunc(
                operation,
                action,
                out T raw,
                fallback: null!,
                statusOverride: statusOverride,
                warnDialog: warnDialog))
        {
            result = null;
            return false;
        }

        if (raw is null)
        {
            result = null;
            return false;
        }

        result = raw;
        return true;
    }

    private bool TryGetSelectedNode(string entityType, [NotNullWhen(true)] out EntityNode? node)
    {
        node = RequireSelectedAs(entityType);
        return node is not null;
    }

    public bool TryMoveEntitiesFromCanvas(IReadOnlyList<MoveEntityRequest> requests) =>
        TryEditorAction("MoveEntities", () => _editor.MoveEntities(requests),
            statusOverride: "[ERROR] Failed to move selected nodes.");

    public bool TryReconnectArrowFromCanvas(Guid arrowId, bool replaceSource, Guid newEndpointId)
    {
        if (!TryEditorFunc(
                "ReconnectArrow",
                () => _editor.ReconnectArrow(arrowId, replaceSource, newEndpointId),
                out bool changed,
                fallback: false,
                statusOverride: "[ERROR] Failed to reconnect arrow."))
            return false;

        return changed;
    }

    public bool TryConnectNodesFromCanvas(Guid sourceId, Guid targetId, ArrowType arrowType)
    {
        if (!TryEditorFunc(
                "ConnectSelectionInOrder",
                () => _editor.ConnectSelectionInOrder([sourceId, targetId], arrowType),
                out int createdCount,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

        return createdCount > 0;
    }
}
