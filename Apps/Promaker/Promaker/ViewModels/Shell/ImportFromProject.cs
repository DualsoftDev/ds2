using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// "다른 프로젝트에서 시스템 가져오기" (전송층 A: 파일).
/// 상대 프로젝트의 저장 파일을 headless 임시 store 로 읽어(기존 로더 재사용 = 구버전
/// 마이그레이션 공짜 상속) 사용자가 고른 System 폐포만 remap 병합한다. 현재 프로젝트 불변,
/// 원본 파일은 읽기 전용. 무손실 형식만 허용 — YAML/Mermaid 는 GUID 비저장이라 부적합.
///
/// 옵션 B: 파일 로드는 Task.Run 배경(임시 store = 현재 store 와 독립이라 레이스 없음) —
/// 스피너가 돈다. 선택 다이얼로그 동안 오버레이 해제(입력 차단 방지), 병합+리빌드는
/// UI 스레드 계약이라 그 구간만 스피너 정지 가능.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task ImportFromProject()
    {
        if (!GuardSimulationSemanticEdit("다른 프로젝트에서 시스템 가져오기"))
            return;

        var picker = new OpenFileDialog
        {
            Title = "다른 프로젝트에서 시스템 가져오기",
            Filter = "프로젝트 파일 (*.sdf;*.json;*.aasx)|*.sdf;*.json;*.aasx"
                   + "|SDF Files (*.sdf)|*.sdf|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx",
        };
        if (picker.ShowDialog() != true)
            return;

        var fileName = Path.GetFileName(picker.FileName);

        // ── 1단계: headless 임시 store 로드 (배경 스레드 — 스피너 유지) ──────────
        BusyMessage = $"프로젝트 파일 읽는 중... {fileName}";
        IsBusy = true;
        DsStore? temp = null;
        string? loadError = null;
        try
        {
            temp = await Task.Run(() =>
            {
                var store = DsStore.empty();
                if (FileTypeProbe.IsAasx(picker.FileName))
                {
                    var result = AasxImporter.importIntoStoreWithError(store, picker.FileName);
                    if (result.IsError)
                    {
                        loadError = $"AASX 파일 열기 실패:\n\n{result.ErrorValue}";
                        return null;
                    }
                }
                else
                {
                    store.LoadFromFile(picker.FileName);
                }
                // 레거시 파일 자동 복구 — 파일 열기 경로(Open.cs)와 동일 뒤처리.
                _ = CallValidation.healMissingOriginFlowIds(store);
                return store;
            });
        }
        catch (Exception ex)
        {
            loadError = $"프로젝트 가져오기 실패:\n\n{ex.Message}";
        }
        finally
        {
            // 선택 다이얼로그 전에 오버레이 해제 — BusyOverlay 는 전체 입력을 차단한다.
            IsBusy = false;
        }
        if (loadError is not null)
        {
            Log.Warn($"Import from project failed: '{picker.FileName}' — {loadError}");
            _dialogService.ShowWarning(loadError);
            return;
        }
        if (temp is null)
            return;

        // ── 2단계: 선택 다이얼로그 (UI, 오버레이 없음) ───────────────────────────
        var sourceProject = Queries.allProjects(temp).FirstOrDefault();
        if (sourceProject is null)
        {
            _dialogService.ShowWarning("선택한 파일에 프로젝트가 없습니다.");
            return;
        }
        if (sourceProject.ActiveSystemIds.Count == 0 && sourceProject.PassiveSystemIds.Count == 0)
        {
            _dialogService.ShowWarning("선택한 파일에 가져올 시스템이 없습니다.");
            return;
        }

        var targetProject = Queries.allProjects(_store).FirstOrDefault();
        if (targetProject is null)
        {
            _dialogService.ShowWarning("가져올 대상 프로젝트가 없습니다.");
            return;
        }

        var dialog = new Promaker.Dialogs.ProjectImportDialog(temp, sourceProject, fileName);
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        // ── 3단계: 병합 + 리빌드 (UI 스레드 계약 — 오버레이 렌더 기회만 주고 진행) ──
        BusyMessage = $"시스템 가져오는 중... {fileName}";
        IsBusy = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);   // 오버레이 먼저 렌더

            if (!TryEditorFunc(
                    () => _store.ImportSystemsFrom(temp, targetProject.Id, dialog.SelectedRoots),
                    out SystemImportSummary? summary,
                    fallback: null))
                return;

            if (summary is not null)
                ReportSystemImport(summary, $"'{fileName}'");
        }
        finally
        {
            if (_rebuildQueued)
                _pendingRebuildActions.Add(() => IsBusy = false);
            else
                IsBusy = false;
        }
    }
}
