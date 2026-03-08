using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Microsoft.Win32;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private bool TryRunFileOperation(string operation, Action action, Func<Exception, string> warnMessage)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{operation} failed", ex);
            DialogHelpers.Warn(warnMessage(ex));
            return false;
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        var fileName = dlg.FileName;
        TryRunFileOperation(
            $"Open file '{fileName}'",
            () =>
            {
                _store.LoadFromFile(fileName);
                _currentFilePath = fileName;
                IsDirty = false;
                UpdateTitle();
                Log.Info($"File opened: {fileName}");
            },
            ex => $"Failed to open file: {ex.Message}");
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

        var filePath = _currentFilePath;
        TryRunFileOperation(
            $"Save file '{filePath}'",
            () =>
            {
                _store.SaveToFile(filePath);
                IsDirty = false;
                UpdateTitle();
                StatusText = "Saved.";
                Log.Info($"File saved: {filePath}");
            },
            ex => $"Failed to save file: {ex.Message}");
    }

    [RelayCommand]
    private void ImportAasx()
    {
        var dlg = new OpenFileDialog { Filter = "AASX Files (*.aasx)|*.aasx" };
        if (dlg.ShowDialog() != true) return;

        var fileName = dlg.FileName;
        TryRunFileOperation(
            $"Import AASX '{fileName}'",
            () =>
            {
                if (!AasxImporter.importIntoStore(_store, fileName))
                {
                    Log.Warn($"AASX import failed: empty result ({fileName})");
                    DialogHelpers.Warn("Failed to import AASX.");
                    return;
                }

                _currentFilePath = null;
                IsDirty = false;
                UpdateTitle();
                Log.Info($"AASX imported: {fileName}");
                StatusText = $"AASX import completed: {System.IO.Path.GetFileName(fileName)}";
            },
            ex => $"Failed to import AASX: {ex.Message}");
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

        var fileName = dlg.FileName;
        TryRunFileOperation(
            $"Export AASX '{fileName}'",
            () =>
            {
                if (!AasxExporter.exportFromStore(_store, fileName))
                {
                    Log.Warn($"AASX export failed: no project ({fileName})");
                    DialogHelpers.Warn("No project available for export.");
                    return;
                }

                Log.Info($"AASX exported: {fileName}");
                StatusText = $"AASX export completed: {System.IO.Path.GetFileName(fileName)}";
            },
            ex => $"Failed to export AASX: {ex.Message}");
    }
}
