using System;
using System.IO;
using log4net;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// 외부 에디터 등으로 현재 열린 파일이 변경되었는지 윈도우 포커스 복귀 시 비교 → 사용자 확인 후 reload.
/// MainViewModel 의 partial 에서 분리. mtime 캐시 + 재진입 가드 + reload 흐름 코디네이션을 보유.
///
/// reload 경로는 OpenFilePath → _store.LoadFromFile / ReplaceStore / importIntoStore 중 하나로 분기되며,
/// 모두 DsStore.ApplyNewStore hook 을 통과 → Revision++ → LLM snapshot 다음 turn 자동 첨부.
/// </summary>
public sealed class ExternalFileChangeWatcher
{
    private static readonly ILog Log = LogManager.GetLogger("Promaker");

    private DateTime? _currentFileMTime;
    private bool      _checkInProgress;

    private readonly Func<string?> _currentFilePath;
    private readonly Func<bool>    _isDirty;
    private readonly Func<bool>    _isBusy;
    private readonly IDialogService _dialogService;
    private readonly Func<bool>    _confirmDiscardChanges;
    private readonly Action<string> _openFilePath;

    public ExternalFileChangeWatcher(
        Func<string?>   currentFilePath,
        Func<bool>      isDirty,
        Func<bool>      isBusy,
        IDialogService  dialogService,
        Func<bool>      confirmDiscardChanges,
        Action<string>  openFilePath)
    {
        _currentFilePath        = currentFilePath;
        _isDirty                = isDirty;
        _isBusy                 = isBusy;
        _dialogService          = dialogService;
        _confirmDiscardChanges  = confirmDiscardChanges;
        _openFilePath           = openFilePath;
    }

    /// <summary>Open / Save 완료 직후 호출 — 현재 디스크 mtime 을 캐시.</summary>
    public void RecordMTime() => _currentFileMTime = TryReadFileMTime(_currentFilePath());

    /// <summary>CSV import 등 mtime 비교가 의미 없어진 시점에 호출.</summary>
    public void ResetMTime() => _currentFileMTime = null;

    private static DateTime? TryReadFileMTime(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException ex)
        {
            Log.Warn($"외부 파일 mtime 읽기 실패 ({path}): {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"외부 파일 mtime 권한 거부 ({path}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 윈도우 포커스 복귀 시 호출 — 외부 변경 감지 + reload prompt.
    /// IsDirty 시 표준 ConfirmDiscardChanges (Save/Discard/Cancel 3 분기) 흐름으로 분기.
    /// </summary>
    public void CheckChange()
    {
        if (_checkInProgress || _isBusy()) return;
        var path = _currentFilePath();
        if (path is null || _currentFileMTime is null) return;
        if (!File.Exists(path)) return;

        var current = TryReadFileMTime(path);
        if (current is null) return;
        if (current.Value <= _currentFileMTime.Value) return;

        _checkInProgress = true;
        try
        {
            var fileName = Path.GetFileName(path);
            Log.Info($"[external-mtime] detected change file={fileName} prev={_currentFileMTime.Value:O} curr={current.Value:O} isDirty={_isDirty()}");
            var alertMsg = _isDirty()
                ? $"외부에서 파일이 변경되었습니다:\n  {fileName}\n\n" +
                  $"현재 편집 중인 변경 내용이 있습니다.\n" +
                  $"다음 다이얼로그에서 저장 / 폐기 / 취소를 선택할 수 있습니다.\n\n" +
                  $"계속하시겠습니까?"
                : $"외부에서 파일이 변경되었습니다:\n  {fileName}\n\n다시 불러오시겠습니까?";

            if (FileTypeProbe.IsYaml(path))
                alertMsg += "\n\n주의: .yaml 은 lossy 포맷 — 다시 불러오면 GUID·위치·alias·시뮬 결과가 재발행/소실됩니다.";

            if (!_dialogService.Confirm(alertMsg, "외부 파일 변경 감지"))
            {
                // 사용자가 거절 — 다음 변경까지는 재질의 안 하도록 mtime 갱신.
                Log.Info($"[external-mtime] user declined reload file={fileName} — suppress until next change");
                _currentFileMTime = current;
                return;
            }

            // dirty 시 표준 Save/Discard/Cancel 흐름 재사용. Save 선택 시 현재 변경분이 디스크에 기록되어
            // 외부 변경분을 덮어쓰지만, 사용자가 그 trade-off 를 명시 선택한 것이므로 정상.
            // Cancel 또는 Save 실패 시 reload 건너뜀.
            if (_isDirty() && !_confirmDiscardChanges())
            {
                Log.Info($"[external-mtime] dirty Save/Discard/Cancel — Cancel 또는 Save 실패로 reload 건너뜀 file={fileName}");
                return;
            }

            // OpenFilePath 가 BeginInvoke 로 분기되므로 mtime 은 CompleteOpen 안에서 다시 기록됨.
            Log.Info($"[external-mtime] reload 진행 file={fileName}");
            _openFilePath(path);
        }
        finally
        {
            _checkInProgress = false;
        }
    }
}
