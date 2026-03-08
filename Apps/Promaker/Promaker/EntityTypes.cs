using Ds2.UI.Core;

namespace Promaker;

public static class EntityTypes
{
    public static bool IsWorkOrCall(EntityKind entityType) =>
        entityType == EntityKind.Work || entityType == EntityKind.Call;

    public static bool IsCanvasOpenable(EntityKind entityType) =>
        entityType == EntityKind.System || entityType == EntityKind.Flow || entityType == EntityKind.Work;
}
