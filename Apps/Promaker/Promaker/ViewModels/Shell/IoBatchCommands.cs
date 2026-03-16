using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void OpenIoBatchDialog()
    {
        var rows = CreateDummyIoBatchRows();
        var dialog = new IoBatchSettingsDialog(rows);
        DialogHelpers.ShowOwnedDialog(dialog);
    }

    private static List<IoBatchRow> CreateDummyIoBatchRows() =>
    [
        new(Guid.Empty, Guid.Empty, "CARTYPE", "CAR_2ND_CT", "DATA.CT", "", "", "", "CARTYPE_DATA_CT", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_LOAD_AIR", "", "", "%QX1.0.0", "S141_INDEX_LOAD_A", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_LOAD_CLP", "", "", "%QX1.0.30", "S141_INDEX_LOAD_C", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_LOCK.ADV", "", "Tag", "%QX1.0.4", "S141_INDEX_LOCK_AI", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_LOCK.RET", "", "", "%QX1.0.5", "S141_INDEX_LOCK_RE", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_POS.SET", "", "Tag", "%QX1.0.21", "S141_INDEX_POS_SET", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_INDEX", "INDEX_SLIDE.SV_P", "", "In", "", "Out", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_RB1", "RB1.CT_1ST_IN_OI", "", "", "%QX1.0.35", "S141_RB1_CT_1ST_IN_", ""),
        new(Guid.Empty, Guid.Empty, "S141", "S141_RB1", "RB1.CT_2ND_IN_C", "", "", "%QX1.0.37", "S141_RB1_CT_2ND_IN", ""),
    ];
}
