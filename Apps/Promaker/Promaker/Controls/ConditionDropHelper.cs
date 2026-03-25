using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Store;
using Promaker.ViewModels;

namespace Promaker.Controls;

/// <summary>
/// "ConditionCallNode" 드래그-드롭의 공통 시각 피드백(Border highlight/restore)을 처리합니다.
/// ConditionSectionControl과 ConditionEditDialog에서 공유합니다.
/// </summary>
internal static class ConditionDropHelper
{
    internal const string DataFormat = "ConditionCallNode";

    internal static bool IsConditionCallDrag(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormat);

    internal static EntityNode? GetDroppedCallNode(DragEventArgs e) =>
        e.Data.GetData(DataFormat) is EntityNode { EntityType: EntityKind.Call } node ? node : null;

    internal static void HandleDragEnter(DragEventArgs e, Border? border, ref Brush? savedBrush, FrameworkElement resourceHost)
    {
        if (!IsConditionCallDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (border is not null)
        {
            savedBrush = border.BorderBrush;
            border.BorderBrush = (Brush)resourceHost.FindResource("AccentBrush");
            border.BorderThickness = new Thickness(2);
        }
        e.Handled = true;
    }

    internal static void HandleDragOver(DragEventArgs e)
    {
        e.Effects = IsConditionCallDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    internal static void RestoreBorder(Border? border, ref Brush? savedBrush, FrameworkElement resourceHost)
    {
        if (border is null) return;
        border.BorderBrush = savedBrush ?? (Brush)resourceHost.FindResource("BorderBrush");
        border.BorderThickness = new Thickness(1);
        savedBrush = null;
    }
}
