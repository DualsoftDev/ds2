using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core.StandardSubmodels;
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

    /// <summary>
    /// 저장 직전 AID 바인딩을 재보장한다 — 편집으로 늘어난 IO맵/UserTag 주소를 endpoint interaction 에
    /// 병합하는 것이 목적. endpoint 가 있는 System 만, <b>그 endpoint 자체 값</b>으로 재보장한다
    /// (접속 편집의 정본은 System 속성 패널이 AID 에 직접 기록한 값이다).
    /// </summary>
    private void StampPlcConnection()
    {
        try
        {
            var sim = Simulation;
            if (sim is null) return;

            var stamped = 0;
            foreach (var entry in sim.ListPlcSystemEndpoints())
            {
                if (!entry.HasEndpoint) continue;
                var systemAddresses = sim.EnumeratePlcAddressesForSystem(entry.SystemId);

                // 벤더에 따라 어느 바인딩을 재보장하는지가 갈린다. SX 를 XGT 쪽에 넘기면 비-LS 라 거절돼(0)
                // 조용히 아무것도 안 되고, 모델에 새로 생긴 주소가 SX endpoint 에 병합되지 않는다.
                bool ok;
                if (entry.Vendor == PlcVendorChoice.MicrexSx)
                {
                    // SX 전용 값(매핑표·쓰기 허용)의 정본은 endpoint 자신이다.
                    var sxConn = _store.TryReadMicrexSxEndpoint(entry.SystemId);
                    ok = _store.EnsureMicrexSxEndpoint(
                        entry.SystemId, entry.Profile,
                        sxConn?.IoMapPath ?? string.Empty,
                        sxConn?.WritableAreas ?? System.Array.Empty<string>(),
                        systemAddresses);
                }
                else
                {
                    ok = _store.EnsureXgtEndpoint(entry.SystemId, entry.Vendor, entry.Profile, systemAddresses);
                }
                if (ok) stamped++;
            }
            if (stamped > 0)
                Log.Info($"AID PLC 바인딩 동기화 — System endpoint {stamped}개 (endpoint 값 기준, 주소 병합)");
        }
        catch (Exception ex)
        {
            Log.Warn($"AID endpoint 동기화 실패 (무시하고 저장 계속): {ex.Message}", ex);
        }
    }

    private bool SaveToPath(string filePath)
    {
        // 저장 형식과 무관하게 AID를 먼저 동기화한다.
        StampPlcConnection();

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
                bool exported;
                try
                {
                    AasxExporter.UserTemplatesFolder =
                        string.IsNullOrWhiteSpace(userTplFolder) || !System.IO.Directory.Exists(userTplFolder)
                            ? Microsoft.FSharp.Core.FSharpOption<string>.None
                            : Microsoft.FSharp.Core.FSharpOption<string>.Some(userTplFolder);

                    exported = AasxExporter.exportFromStore(
                        _store, filePath, AppSettings.IriPrefix,
                        AppSettings.SplitDeviceAasx, AppSettings.CreateDefaultEntitiesOnEmptyAasx);
                }
                finally
                {
                    // Export 실패 뒤에도 전역 템플릿 설정이 다음 저장으로 새지 않게 원상복구한다.
                    AasxExporter.UserTemplatesFolder = prevTplFolder;
                }

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
