using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ImportAasx()
    {
        var dlg = new OpenFileDialog { Filter = "AASX Files (*.aasx)|*.aasx" };
        if (dlg.ShowDialog() != true) return;

        var storeOpt = AasxImporter.importFromAasxFile(dlg.FileName);
        if (!FSharpOption<DsStore>.get_IsSome(storeOpt))
        {
            DialogHelpers.Warn("Failed to import AASX.");
            return;
        }

        _editor.ReplaceStore(storeOpt.Value);
        _currentFilePath = null;
        IsDirty = false;
        UpdateTitle();
        StatusText = $"AASX import completed: {System.IO.Path.GetFileName(dlg.FileName)}";
    }

    [RelayCommand]
    private void ExportAasx()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "AASX Files (*.aasx)|*.aasx",
            DefaultExt = ".aasx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            if (!AasxExporter.tryExportFirstProjectToAasxFile(_store, dlg.FileName))
            {
                DialogHelpers.Warn("No project available for export.");
                return;
            }

            StatusText = $"AASX export completed: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            DialogHelpers.Warn($"Failed to export AASX: {ex.Message}");
        }
    }
}
