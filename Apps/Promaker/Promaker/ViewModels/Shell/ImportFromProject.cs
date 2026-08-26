using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
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
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ImportFromProject()
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

        TryRunFileOperation(
            $"Import systems from '{picker.FileName}'",
            () =>
            {
                // headless 임시 store — 현재 _store 를 건드리지 않는다.
                var temp = DsStore.empty();
                if (FileTypeProbe.IsAasx(picker.FileName))
                {
                    var result = AasxImporter.importIntoStoreWithError(temp, picker.FileName);
                    if (result.IsError)
                    {
                        _dialogService.ShowWarning($"AASX 파일 열기 실패:\n\n{result.ErrorValue}");
                        return;
                    }
                }
                else
                {
                    temp.LoadFromFile(picker.FileName);
                }
                // 레거시 파일 자동 복구 — 파일 열기 경로(Open.cs)와 동일 뒤처리.
                _ = Ds2.Core.CallValidation.healMissingOriginFlowIds(temp);

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

                var dialog = new Promaker.Dialogs.ProjectImportDialog(
                    temp, sourceProject, Path.GetFileName(picker.FileName));
                if (_dialogService.ShowDialog(dialog) != true)
                    return;

                if (!TryEditorFunc(
                        () => _store.ImportSystemsFrom(temp, targetProject.Id, dialog.SelectedRoots),
                        out SystemImportSummary? summary,
                        fallback: null))
                    return;

                if (summary is not null)
                    ReportSystemImport(summary, $"'{Path.GetFileName(picker.FileName)}'");
            },
            ex => $"프로젝트 가져오기 실패:\n\n{ex.Message}");
    }
}
