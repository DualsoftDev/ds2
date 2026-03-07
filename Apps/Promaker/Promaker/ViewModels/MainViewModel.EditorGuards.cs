using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Ds2.UI.Core;
using Promaker.Dialogs;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private static string ExtractMethodName(LambdaExpression expression)
    {
        var body = expression.Body;
        while (body is MethodCallExpression call)
        {
            if (call.Object is { Type: var t } && t == typeof(DsStore))
                return call.Method.Name;
            if (call.Arguments.Count > 0 && call.Arguments[0].Type == typeof(DsStore))
                return call.Method.Name;
            if (call.Object is MethodCallExpression inner)
                body = inner;
            else if (call.Arguments.Count > 0 && call.Arguments[0] is MethodCallExpression argInner)
                body = argInner;
            else
                break;
        }
        return "Unknown";
    }

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
        Expression<Action> expression,
        string? statusOverride = null,
        bool warnDialog = false)
    {
        var operation = ExtractMethodName(expression);
        try
        {
            expression.Compile()();
            return true;
        }
        catch (Exception ex)
        {
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
    }

    private bool TryEditorFunc<T>(
        Expression<Func<T>> expression,
        out T result,
        T fallback = default!,
        string? statusOverride = null,
        bool warnDialog = false)
    {
        var operation = ExtractMethodName(expression);
        try
        {
            result = expression.Compile()();
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
        Expression<Func<T>> expression,
        [NotNullWhen(true)] out T? result,
        string? statusOverride = null,
        bool warnDialog = false)
        where T : class
    {
        var operation = ExtractMethodName(expression);
        try
        {
            var raw = expression.Compile()();
            if (raw is null)
            {
                result = null;
                return false;
            }
            result = raw;
            return true;
        }
        catch (Exception ex)
        {
            result = null;
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
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
