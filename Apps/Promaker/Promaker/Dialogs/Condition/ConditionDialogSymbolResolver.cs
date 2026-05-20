using System;
using System.Collections.Generic;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

internal static class ConditionDialogSymbolResolver
{
    internal static Dictionary<string, Guid> BuildDisplayNameToApiCallId(
        DsStore store,
        Guid ownerId,
        EntityKind ownerKind,
        ConditionType conditionType)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        AddConditionApiCalls(store, map, ownerId, ownerKind, conditionType, exactTypeOnly: true);
        AddConditionApiCalls(store, map, ownerId, ownerKind, conditionType, exactTypeOnly: false);

        foreach (var apiCall in store.ApiCalls.Values)
            AddIfDisplayNameResolved(store, map, apiCall);

        return map;
    }

    internal static IReadOnlyList<string> BuildRegisteredDisplayNames(
        DsStore store,
        Guid ownerId,
        EntityKind ownerKind,
        ConditionType conditionType)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        AddConditionApiCalls(store, map, ownerId, ownerKind, conditionType, exactTypeOnly: true);
        AddConditionApiCalls(store, map, ownerId, ownerKind, conditionType, exactTypeOnly: false);
        return new List<string>(map.Keys);
    }

    private static void AddConditionApiCalls(
        DsStore store,
        Dictionary<string, Guid> map,
        Guid ownerId,
        EntityKind ownerKind,
        ConditionType conditionType,
        bool exactTypeOnly)
    {
        foreach (var condition in GetOwnerConditions(store, ownerId, ownerKind))
        {
            if (exactTypeOnly && (!FSharpOption<ConditionType>.get_IsSome(condition.Type) || condition.Type.Value != conditionType))
                continue;

            AddConditionTreeApiCalls(store, map, condition);
        }
    }

    private static IEnumerable<Condition> GetOwnerConditions(DsStore store, Guid ownerId, EntityKind ownerKind)
    {
        if (ownerKind == EntityKind.Work)
        {
            var resolvedId = Queries.resolveOriginalWorkId(ownerId, store);
            if (store.Works.TryGetValue(resolvedId, out var work))
                return work.Conditions;
            return Array.Empty<Condition>();
        }

        var resolvedCallId = Queries.resolveOriginalCallId(ownerId, store);
        if (store.Calls.TryGetValue(resolvedCallId, out var call))
            return call.Conditions;
        return Array.Empty<Condition>();
    }

    private static void AddConditionTreeApiCalls(DsStore store, Dictionary<string, Guid> map, Condition condition)
    {
        foreach (var apiCall in condition.ApiCalls)
            AddIfDisplayNameResolved(store, map, apiCall);

        foreach (var child in condition.Children)
            AddConditionTreeApiCalls(store, map, child);
    }

    private static void AddIfDisplayNameResolved(DsStore store, Dictionary<string, Guid> map, ApiCall apiCall)
    {
        var displayName = TryGetDisplayName(store, apiCall);
        if (displayName is not null && !map.ContainsKey(displayName))
            map.Add(displayName, apiCall.Id);
    }

    private static string? TryGetDisplayName(DsStore store, ApiCall apiCall)
    {
        if (!FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
            return null;

        var apiDefId = apiCall.ApiDefId.Value;
        if (!store.ApiDefs.TryGetValue(apiDefId, out var apiDef))
            return null;

        if (!store.Systems.TryGetValue(apiDef.ParentId, out var system))
            return null;

        return $"{system.Name}.{apiDef.Name}";
    }
}
