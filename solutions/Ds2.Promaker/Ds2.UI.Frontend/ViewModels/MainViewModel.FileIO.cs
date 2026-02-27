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
            DialogHelpers.Warn("AASX 임포트에 실패했습니다.");
            return;
        }

        _editor.ReplaceStore(storeOpt.Value);
        _currentFilePath = null;
        IsDirty = false;
        UpdateTitle();
        StatusText = $"AASX 임포트 완료: {System.IO.Path.GetFileName(dlg.FileName)}";
    }

    [RelayCommand]
    private void ExportAasx()
    {
        var project = _store.Projects.Values.FirstOrDefault();
        if (project is null)
        {
            DialogHelpers.Warn("익스포트할 프로젝트가 없습니다.");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "AASX Files (*.aasx)|*.aasx",
            DefaultExt = ".aasx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            AasxExporter.exportToAasxFile(_store, project, dlg.FileName);
            StatusText = $"AASX 익스포트 완료: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            DialogHelpers.Warn($"AASX 익스포트에 실패했습니다: {ex.Message}");
        }
    }
}
