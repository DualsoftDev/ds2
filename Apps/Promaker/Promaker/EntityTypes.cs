using Ds2.UI.Core;

namespace Promaker;

public static class EntityTypes
{
    public const EntityKind Project = EntityKind.Project;
    public const EntityKind System = EntityKind.System;
    public const EntityKind Flow = EntityKind.Flow;
    public const EntityKind Work = EntityKind.Work;
    public const EntityKind Call = EntityKind.Call;
    public const EntityKind ApiDef = EntityKind.ApiDef;
    public const EntityKind Button = EntityKind.Button;
    public const EntityKind Lamp = EntityKind.Lamp;
    public const EntityKind Condition = EntityKind.Condition;
    public const EntityKind Action = EntityKind.Action;
    public const EntityKind ApiDefCategory = EntityKind.ApiDefCategory;
    public const EntityKind DeviceRoot = EntityKind.DeviceRoot;

    public static bool Is(EntityKind entityType, EntityKind expected) =>
        entityType == expected;

    public static bool IsWorkOrCall(EntityKind entityType) =>
        entityType == EntityKind.Work || entityType == EntityKind.Call;

    public static bool IsCanvasOpenable(EntityKind entityType) =>
        entityType == EntityKind.System || entityType == EntityKind.Flow || entityType == EntityKind.Work;
}
