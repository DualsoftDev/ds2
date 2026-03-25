using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.ViewModels;

namespace Promaker.Controls;

/// <summary>
/// "ConditionCallNode" 드래그-드롭의 공통 시각 피드백(Border highlight/restore)을 처리합니다.
/// ConditionSectionControl, ConditionEditDialog, EditorCanvas에서 공유합니다.
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

    /// <summary>
    /// 드롭된 Call의 ApiCall을 조회 → Picker → store.AddConditionWithApiCalls 호출.
    /// PropertyPanelState, ConditionEditDialog, EditorCanvas에서 공용.
    /// </summary>
    internal static bool ExecuteConditionDrop(
        DsStore store,
        MainViewModel.HostBase host,
        Guid targetCallId,
        CallConditionType condType,
        Guid droppedCallId,
        Window? ownerWindow = null)
    {
        if (!host.TryRef(() => store.GetCallApiCallsForPanel(droppedCallId), out var rows))
            return false;

        if (rows.Length == 0)
        {
            host.SetStatusText("드롭된 Call에 ApiCall이 없습니다.");
            return false;
        }

        IReadOnlyList<Guid> selectedIds;
        if (rows.Length == 1)
        {
            selectedIds = [rows[0].ApiCallId];
        }
        else
        {
            var choices = rows
                .Select(r => new ApiCallPickerDialog.Choice(r.ApiCallId, $"{r.ApiDefDisplayName} / {r.Name}"))
                .ToList();
            var picker = new ApiCallPickerDialog(choices);
            if (ownerWindow is not null) picker.Owner = ownerWindow;
            else if (Application.Current.MainWindow is { } main) picker.Owner = main;
            if (picker.ShowDialog() != true || picker.SelectedApiCallIds.Count == 0)
                return false;
            selectedIds = picker.SelectedApiCallIds;
        }

        if (!host.TryAction(() => store.AddConditionWithApiCalls(targetCallId, condType, selectedIds)))
            return false;

        host.SetStatusText($"{selectedIds.Count} ApiCall(s) added to {condType}.");
        return true;
    }
}
