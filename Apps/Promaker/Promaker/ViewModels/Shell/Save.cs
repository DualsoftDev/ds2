using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core.Store;
using Ds2.Editor;

using Microsoft.Win32;
using Promaker.Dialogs;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveFile()
    {
        TrySaveFile();
    }

    private bool TrySaveFile()
    {
        if (_currentFilePath is null)
        {
            return TrySaveFileAs();
        }

        return SaveToPath(_currentFilePath);
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveFileAs()
    {
        TrySaveFileAs();
    }

    /// <summary>
    /// Promaker · DSPilot 공유 경로 (%ProgramData%\DualSoft\Shared\project.aasx) 로 AASX 저장.
    /// DSPilot 서비스가 같은 경로를 읽으므로 별도 업로드/설정 없이 모델이 동기화된다.
    /// 폴더가 없으면 자동 생성 (인스톨러가 보장하지만 클린 환경 대비).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveToSharedLocation()
    {
        try
        {
            Directory.CreateDirectory(SharedPaths.SharedDirectory);
        }
        catch (Exception ex)
        {
            Log.Error($"공유 폴더 생성 실패: {SharedPaths.SharedDirectory}", ex);
            _dialogService.ShowWarning($"공유 폴더를 만들 수 없습니다:\n{SharedPaths.SharedDirectory}\n\n{ex.Message}");
            return;
        }

        var ok = SaveToPath(SharedPaths.AasxFilePath);
        if (ok)
        {
            RecentFilesManager.AddRecentFile(SharedPaths.AasxFilePath);
            _dispatcher.InvokeAsync(LoadRecentFiles);
            StatusText = $"DSPilot 공유 경로에 저장됨: {SharedPaths.AasxFilePath}";
        }
    }

    /// <summary>
    /// Hub 모드(Control/VirtualPlant/Monitoring) 시뮬레이션 시작 직전에 호출되는 자동 publish.
    /// 현재 store 를 DSPilot 공유 AASX 경로로 silent export — 다이얼로그/StatusText 변경 없음.
    /// 호출자(SimulationPanelState)가 실패 로그를 sim event log 로 남긴다.
    /// </summary>
    internal bool TryPublishAasxToSharedForDspilot()
    {
        if (!HasProject)
            return false;

        try
        {
            Directory.CreateDirectory(SharedPaths.SharedDirectory);
        }
        catch (Exception ex)
        {
            Log.Error($"공유 폴더 생성 실패 (auto publish): {SharedPaths.SharedDirectory}", ex);
            return false;
        }

        try
        {
            var exported = AasxExporter.exportFromStore(
                _store, SharedPaths.AasxFilePath, AppSettings.IriPrefix, AppSettings.SplitDeviceAasx, AppSettings.CreateDefaultEntitiesOnEmptyAasx);
            if (exported)
                Log.Info($"AASX auto-published to DSPilot shared path: {SharedPaths.AasxFilePath}");
            else
                Log.Warn($"AASX auto-publish: no project to export ({SharedPaths.AasxFilePath})");
            return exported;
        }
        catch (Exception ex)
        {
            Log.Error($"AASX auto-publish 실패: {SharedPaths.AasxFilePath}", ex);
            return false;
        }
    }

    private bool TrySaveFileAs()
    {
        var projects = Queries.allProjects(_store);
        var suggestedName = _currentFilePath is not null
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : (!projects.IsEmpty ? projects.Head.Name : "project");

        // _currentFilePath 가 .yaml 인 상태에서 SaveAs default 가 .sdf 면 사용자 의도 위반 →
        // 현 경로 확장자 기준 동적 선택. 신규 프로젝트는 기존대로 .sdf.
        // SaveFileDialog.DefaultExt 는 점 없는 형식 ("yaml") 기대 — TrimStart('.').
        var defaultExt = _currentFilePath is null
            ? FileExtensions.Sdf.TrimStart('.')
            : Path.GetExtension(_currentFilePath).ToLowerInvariant().TrimStart('.');

        var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = string.IsNullOrEmpty(defaultExt) ? FileExtensions.Sdf.TrimStart('.') : defaultExt,
            FileName = suggestedName
        };

        if (dlg.ShowDialog() != true) return false;

        return SaveToPath(dlg.FileName);
    }

    private bool SaveToPath(string filePath)
    {
        if (FileTypeProbe.IsMermaid(filePath))
        {
            try
            {
                var result = Ds2.Mermaid.MermaidExporter.saveProjectToFile(_store, filePath);
                return SaveOutcomeFlow.TryCompleteMermaidSave(
                    result,
                    _dialogService.ShowWarning,
                    () => CompleteSave(filePath, "Mermaid"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save Mermaid '{filePath}' failed", ex);
                _dialogService.ShowWarning($"Mermaid 저장 실패: {ex.Message}");
                return false;
            }
        }

        if (FileTypeProbe.IsAasx(filePath))
        {
            try
            {
                // 시뮬 데이터가 있으면 AASX export 직전 자동으로 시나리오 박제 → Ds2.Core.TechnicalDataTypes.TechnicalData.SimulationResults
                try
                {
                    var captured = Simulation?.Report.TryCaptureScenario(
                        $"AutoCapture_{DateTime.Now:yyyyMMdd_HHmmss}");
                    if (captured != null)
                        Log.Info($"AASX 저장 전 시뮬 시나리오 박제됨: {captured.Meta.ScenarioName}");
                }
                catch (Exception capEx)
                {
                    Log.Warn($"AASX 저장 전 시뮬 시나리오 박제 실패 (무시): {capEx.Message}");
                }

                // 사용자 정의 AASX 템플릿 폴더 — 설정값을 export 직전에 set, 후 reset.
                var userTplFolder = Promaker.Presentation.AppSettingStore.LoadStringOrDefault(
                    Promaker.Services.SettingsPaths.AasxUserTemplatesFolder, "");
                var prevTplFolder = AasxExporter.UserTemplatesFolder;
                AasxExporter.UserTemplatesFolder =
                    string.IsNullOrWhiteSpace(userTplFolder) || !System.IO.Directory.Exists(userTplFolder)
                        ? Microsoft.FSharp.Core.FSharpOption<string>.None
                        : Microsoft.FSharp.Core.FSharpOption<string>.Some(userTplFolder);

                var exported = AasxExporter.exportFromStore(_store, filePath, AppSettings.IriPrefix, AppSettings.SplitDeviceAasx, AppSettings.CreateDefaultEntitiesOnEmptyAasx);

                AasxExporter.UserTemplatesFolder = prevTplFolder;

                // 사용자 폴더 SM 이 ds2 표준 SM 을 override 했는지 확인 → 사용자에게 상세 안내.
                if (exported)
                {
                    var overrides = AasxExporter.LastUserTemplateOverrides;
                    if (overrides != null && overrides.Any())
                    {
                        var lines = string.Join("\n",
                            overrides.Select(t => $"  • {t.Item1}  →  Submodel \"{t.Item2}\""));
                        var msg =
                            $"AASX 사용자 템플릿 폴더의 .aasx 파일이 ds2 기본 표준 Submodel 을 덮어썼습니다.\n\n" +
                            $"{lines}\n\n" +
                            $"⚠ 결과: 위 Submodel(들) 은 사용자 폴더의 .aasx 내용으로 출력되며,\n" +
                            $"     Promaker 의 입력 데이터(예: Nameplate 의 ManufacturerName/SerialNumber 등) 는 \n" +
                            $"     반영되지 않습니다.\n\n" +
                            $"폴더: {userTplFolder}\n" +
                            $"파일: {filePath}\n\n" +
                            $"의도한 동작이 아니라면, 사용자 템플릿 폴더에서 해당 파일을 제거하거나\n" +
                            $"Submodel idShort 를 ds2 표준과 다른 이름으로 변경하세요\n" +
                            $"(예: \"Nameplate\" → \"NameplateCustom\").";
                        Promaker.Dialogs.DialogHelpers.ShowThemedMessageBox(
                            msg, "AASX 사용자 템플릿 override 안내", System.Windows.MessageBoxButton.OK, "ⓘ");
                    }
                }
                if (!exported)
                    Log.Warn($"AASX save failed: no project ({filePath})");

                return SaveOutcomeFlow.TryCompleteAasxSave(
                    exported,
                    _dialogService.ShowWarning,
                    "No project available for AASX save.",
                    () => CompleteSave(filePath, "AASX"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save AASX '{filePath}' failed", ex);
                _dialogService.ShowWarning($"Failed to save AASX: {ex.Message}");
                return false;
            }
        }

        try
        {
            _store.SaveToFile(filePath);
            CompleteSave(filePath, "File");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Save file '{filePath}' failed", ex);
            _dialogService.ShowWarning($"Failed to save file: {ex.Message}");
            return false;
        }
    }

}
