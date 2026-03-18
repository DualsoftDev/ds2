using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// 편집 관련 명령(Undo/Redo, Copy/Paste)을 담당하는 ViewModel
/// </summary>
public partial class EditCommandsViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(EditCommandsViewModel));

    private readonly Func<DsStore> _getStore;
    private readonly Action _requestRebuildAll;
    private readonly Func<List<Ds2.UI.Core.SelectionKey>> _getOrderedSelection;
    private readonly Func<Xywh?> _getPendingAddPosition;
    private readonly Action<Action?> _requestRebuildAllWithCallback;

    private readonly List<Ds2.UI.Core.SelectionKey> _clipboardSelection = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    private bool _hasClipboardData;

    public EditCommandsViewModel(
        Func<DsStore> getStore,
        Action requestRebuildAll,
        Func<List<Ds2.UI.Core.SelectionKey>> getOrderedSelection,
        Func<Xywh?> getPendingAddPosition,
        Action<Action?> requestRebuildAllWithCallback)
    {
        _getStore = getStore;
        _requestRebuildAll = requestRebuildAll;
        _getOrderedSelection = getOrderedSelection;
        _getPendingAddPosition = getPendingAddPosition;
        _requestRebuildAllWithCallback = requestRebuildAllWithCallback;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        try
        {
            var store = _getStore();
            store.Undo();
            _requestRebuildAll();
            Log.Info("Undo executed");
        }
        catch (Exception ex)
        {
            Log.Error("Undo failed", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        try
        {
            var store = _getStore();
            store.Redo();
            _requestRebuildAll();
            Log.Info("Redo executed");
        }
        catch (Exception ex)
        {
            Log.Error("Redo failed", ex);
        }
    }

    [RelayCommand]
    private void Copy()
    {
        _clipboardSelection.Clear();
        _clipboardSelection.AddRange(_getOrderedSelection());
        HasClipboardData = _clipboardSelection.Count > 0;
        Log.Info($"Copied {_clipboardSelection.Count} items to clipboard");
    }

    [RelayCommand(CanExecute = nameof(HasClipboardData))]
    private void Paste()
    {
        if (_clipboardSelection.Count == 0)
            return;

        try
        {
            var store = _getStore();
            var targetPos = _getPendingAddPosition() ?? new Xywh(100, 100, 120, 60);

            // 복사된 항목들을 붙여넣기
            var pastedIds = new List<Guid>();

            foreach (var key in _clipboardSelection)
            {
                // 실제 paste 로직은 DsStore API에 따라 구현 필요
                // 여기서는 간단한 구조만 작성
                // SelectionKey는 Ds2.UI.Core에 정의되어 있으므로, 사용 방법 확인 필요
                Log.Info($"Pasting entity");
            }

            _requestRebuildAllWithCallback(() =>
            {
                // 붙여넣은 항목 선택
                Log.Info($"Pasted {pastedIds.Count} items");
            });
        }
        catch (Exception ex)
        {
            Log.Error("Paste failed", ex);
        }
    }

    [RelayCommand]
    private void Cut()
    {
        Copy();

        try
        {
            var store = _getStore();
            var selection = _getOrderedSelection();

            // 선택된 항목 삭제 (DeleteEntities 구현 필요)
            // store.DeleteEntities(selection.Select(s => s.Id).ToList());

            _clipboardSelection.Clear();
            _clipboardSelection.AddRange(selection);
            HasClipboardData = true;

            _requestRebuildAll();
            Log.Info($"Cut {selection.Count} items");
        }
        catch (Exception ex)
        {
            Log.Error("Cut failed", ex);
        }
    }

    public void ClearClipboard()
    {
        _clipboardSelection.Clear();
        HasClipboardData = false;
    }

    public void UpdateUndoRedoState(bool canUndo, bool canRedo)
    {
        CanUndo = canUndo;
        CanRedo = canRedo;
    }
}
