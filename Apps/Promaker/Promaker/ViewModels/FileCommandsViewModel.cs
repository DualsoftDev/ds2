using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using log4net;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// 파일 관련 명령을 담당하는 ViewModel
/// </summary>
public partial class FileCommandsViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FileCommandsViewModel));

    private readonly IFileService _fileService;
    private readonly IDialogService _dialogService;
    private readonly Func<DsStore> _getStore;
    private readonly Action<DsStore> _setStore;
    private readonly Action<string?, bool> _onFileOpened;
    private readonly Action<string?> _onFileSaved;
    private readonly Func<bool> _confirmDiscardChanges;

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveFileAsCommand))]
    private bool _hasProject;

    public FileCommandsViewModel(
        IFileService fileService,
        IDialogService dialogService,
        Func<DsStore> getStore,
        Action<DsStore> setStore,
        Action<string?, bool> onFileOpened,
        Action<string?> onFileSaved,
        Func<bool> confirmDiscardChanges)
    {
        _fileService = fileService;
        _dialogService = dialogService;
        _getStore = getStore;
        _setStore = setStore;
        _onFileOpened = onFileOpened;
        _onFileSaved = onFileSaved;
        _confirmDiscardChanges = confirmDiscardChanges;
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        if (!_confirmDiscardChanges())
            return;

        var filePath = _dialogService.ShowOpenFileDialog(FileService.FileFilter);
        if (filePath is null)
            return;

        try
        {
            DsStore? newStore = null;

            if (_fileService.HasExtension(filePath, ".md"))
            {
                newStore = await _fileService.ImportMermaidAsync(filePath);
            }
            else if (_fileService.HasExtension(filePath, ".aasx"))
            {
                newStore = await _fileService.ImportAasxAsync(filePath);
            }
            else
            {
                newStore = await _fileService.LoadProjectAsync(filePath);
            }

            if (newStore is not null)
            {
                _setStore(newStore);
                CurrentFilePath = filePath;
                HasProject = true;
                _onFileOpened(filePath, true);
            }
        }
        catch (FileServiceException ex)
        {
            Log.Error($"Failed to open file: {filePath}", ex);
            _dialogService.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error opening file: {filePath}", ex);
            _dialogService.ShowError($"파일 열기 실패: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task SaveFile()
    {
        await TrySaveFileAsync();
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task SaveFileAs()
    {
        await TrySaveFileAsAsync();
    }

    private async System.Threading.Tasks.Task<bool> TrySaveFileAsync()
    {
        if (CurrentFilePath is null)
        {
            return await TrySaveFileAsAsync();
        }

        return await SaveToPathAsync(CurrentFilePath);
    }

    private async System.Threading.Tasks.Task<bool> TrySaveFileAsAsync()
    {
        var store = _getStore();
        var projects = DsQuery.allProjects(store);
        var suggestedName = !projects.IsEmpty ? projects.Head.Name : "project";

        var filePath = _dialogService.ShowSaveFileDialog(FileService.FileFilter, suggestedName);
        if (filePath is null)
            return false;

        return await SaveToPathAsync(filePath);
    }

    private async System.Threading.Tasks.Task<bool> SaveToPathAsync(string filePath)
    {
        try
        {
            var store = _getStore();

            if (_fileService.HasExtension(filePath, ".md"))
            {
                await ((FileService)_fileService).SaveMermaidAsync(filePath, store);
            }
            else if (_fileService.HasExtension(filePath, ".aasx"))
            {
                await _fileService.ExportAasxAsync(filePath, store);
            }
            else
            {
                await _fileService.SaveProjectAsync(filePath, store);
            }

            CurrentFilePath = filePath;
            _onFileSaved(filePath);
            return true;
        }
        catch (FileServiceException ex)
        {
            Log.Error($"Failed to save file: {filePath}", ex);
            _dialogService.ShowError(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error saving file: {filePath}", ex);
            _dialogService.ShowError($"파일 저장 실패: {ex.Message}");
            return false;
        }
    }

    public async System.Threading.Tasks.Task<bool> SaveDuringDiscardCheckAsync()
    {
        try
        {
            return await TrySaveFileAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed during discard check", ex);
            _dialogService.ShowError($"저장 실패: {ex.Message}");
            return false;
        }
    }
}
