using System;
using Ds2.UI.Core;

namespace Ds2.UI.Frontend;

public static class EntityTypes
{
    public static readonly string Project = EntityTypeNames.Project;
    public static readonly string System = EntityTypeNames.System;
    public static readonly string Flow = EntityTypeNames.Flow;
    public static readonly string Work = EntityTypeNames.Work;
    public static readonly string Call = EntityTypeNames.Call;
    public static readonly string ApiDef = EntityTypeNames.ApiDef;
    public static readonly string Button = EntityTypeNames.Button;
    public static readonly string Lamp = EntityTypeNames.Lamp;
    public static readonly string Condition = EntityTypeNames.Condition;
    public static readonly string Action = EntityTypeNames.Action;
    public static readonly string ApiDefCategory = EntityTypeNames.ApiDefCategory;
    public static readonly string DeviceRoot = EntityTypeNames.DeviceRoot;

    public static bool Is(string? entityType, string expected) =>
        string.Equals(entityType, expected, StringComparison.Ordinal);

    public static bool IsWorkOrCall(string? entityType) =>
        Is(entityType, Work) || Is(entityType, Call);

    public static bool IsCanvasOpenable(string? entityType) =>
        Is(entityType, System) || Is(entityType, Flow) || Is(entityType, Work);
}
