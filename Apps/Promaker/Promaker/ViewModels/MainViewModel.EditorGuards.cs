using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ds2.UI.Core;
using Promaker.Dialogs;
using Microsoft.FSharp.Core;

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
            DialogHelpers.Warn($"{operation} failed: {ex.Message}");
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

    /// ObservableCollection 전체 교체 (Clear + AddRange)
    private static void ReplaceAll<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

    private static FSharpOption<T>? ToOption<T>(T? value) where T : struct =>
        value.HasValue ? FSharpOption<T>.Some(value.Value) : null;

    private static FSharpOption<T>? ToOption<T>(T? value) where T : class =>
        value is not null ? FSharpOption<T>.Some(value) : null;

    private bool TryGetSelectedNode(string entityType, [NotNullWhen(true)] out EntityNode? node)
    {
        node = RequireSelectedAs(entityType);
        return node is not null;
    }

    public bool TryMoveEntitiesFromCanvas(IReadOnlyList<UiMoveEntityRequest> requests) =>
        TryEditorAction(() => _store.MoveEntitiesUi(requests),
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

    public bool TryConnectNodesFromCanvas(Guid sourceId, Guid targetId, UiArrowType arrowType)
    {
        if (!TryEditorFunc(
                () => _store.ConnectSelectionInOrderUi(new Guid[] { sourceId, targetId }, arrowType),
                out int createdCount,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

        return createdCount > 0;
    }
}
