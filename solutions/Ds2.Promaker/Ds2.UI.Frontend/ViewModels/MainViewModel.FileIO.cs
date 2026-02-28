using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;
using log4net;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _editor.LoadFromFile(dlg.FileName);
            _currentFilePath = dlg.FileName;
            IsDirty = false;
            UpdateTitle();
            Log.Info($"파일 열기 완료: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error($"파일 열기 실패: {dlg.FileName}", ex);
            DialogHelpers.Warn($"파일을 열 수 없습니다: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveFile()
    {
        if (_currentFilePath is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".json"
            };

            if (dlg.ShowDialog() != true) return;
            _currentFilePath = dlg.FileName;
        }

        try
        {
            _editor.SaveToFile(_currentFilePath);
            IsDirty = false;
            UpdateTitle();
            StatusText = "Saved.";
            Log.Info($"파일 저장 완료: {_currentFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"파일 저장 실패: {_currentFilePath}", ex);
            DialogHelpers.Warn($"파일을 저장할 수 없습니다: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ImportAasx()
    {
        var dlg = new OpenFileDialog { Filter = "AASX Files (*.aasx)|*.aasx" };
        if (dlg.ShowDialog() != true) return;

        var storeOpt = AasxImporter.importFromAasxFile(dlg.FileName);
        if (!FSharpOption<DsStore>.get_IsSome(storeOpt))
        {
            Log.Warn($"AASX import 실패 (빈 결과): {dlg.FileName}");
            DialogHelpers.Warn("Failed to import AASX.");
            return;
        }

        try
        {
            _editor.ReplaceStore(storeOpt.Value);
            _currentFilePath = null;
            IsDirty = false;
            UpdateTitle();
            Log.Info($"AASX import 완료: {dlg.FileName}");
            StatusText = $"AASX import completed: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            Log.Error($"AASX import 실패 (ReplaceStore): {dlg.FileName}", ex);
            DialogHelpers.Warn($"Failed to apply imported AASX: {ex.Message}");
        }
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
                Log.Warn($"AASX export 실패: 프로젝트 없음 ({dlg.FileName})");
                DialogHelpers.Warn("No project available for export.");
                return;
            }

            Log.Info($"AASX export 완료: {dlg.FileName}");
            StatusText = $"AASX export completed: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            Log.Error($"AASX export 실패: {dlg.FileName}", ex);
            DialogHelpers.Warn($"Failed to export AASX: {ex.Message}");
        }
    }
}
