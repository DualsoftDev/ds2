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
    /// 'Agent에 업로드' — 모델(AASX)을 Promaker · DSPilot 공유 경로
    /// (%ProgramData%\DualSoft\Shared\project.aasx) 로 저장하고, PLC 설정과 함께 Promaker.Agent
    /// 모니터링 세션(session.json + active.flag)을 기록한다. Agent 는 파일 변경을 감지해
    /// 새 모델/설정으로 (재)시작하고, DSPilot 도 같은 경로를 읽어 동기화된다.
    /// 모니터링 PLAY 는 업로드 없이 Agent Hub 접속만 한다 — 업로드는 이 명령이 유일한 경로.
    /// 폴더가 없으면 자동 생성 (인스톨러가 보장하지만 클린 환경 대비).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private async System.Threading.Tasks.Task SaveToSharedLocation()
    {
        // 업로드 전 현재 파일 저장 선행 — 새 프로젝트(경로 없음)면 다른 이름으로 저장 다이얼로그가 뜨고,
        // 취소하면 업로드도 중단. 업로드본과 사용자 파일이 어긋난 채 배포되는 것을 방지.
        if (!TrySaveFile())
        {
            StatusText = "Agent 업로드 취소 — 파일 저장이 선행되어야 합니다.";
            return;
        }

        // 대상 결정: ◎로컬(이 머신 공유폴더) ○네트워크(특정 IP — 구조만, 미구현 안내).
        if (!AgentModelTransfer.TryResolveAasxPath(CurrentAgentTransferTarget, out var targetAasxPath, out var targetError))
        {
            _dialogService.ShowWarning(targetError);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetAasxPath)!);
        }
        catch (Exception ex)
        {
            Log.Error($"공유 폴더 생성 실패: {targetAasxPath}", ex);
            _dialogService.ShowWarning($"공유 폴더를 만들 수 없습니다:\n{targetAasxPath}\n\n{ex.Message}");
            return;
        }

        // 조용한 export — SaveToPath/CompleteSave 를 타면 _currentFilePath 가 공유 경로로 바뀌어
        // 이후 Ctrl+S 가 사용자의 원본 파일이 아닌 공유 AASX 로 가버린다. 업로드는 부수 내보내기일 뿐
        // 작업 파일 전환이 아니므로 현재 열린 파일/타이틀/IsDirty 를 건드리지 않는다.
        bool exported;
        try
        {
            exported = AasxExporter.exportFromStore(
                _store, targetAasxPath,
                AppSettings.IriPrefix, AppSettings.SplitDeviceAasx, AppSettings.CreateDefaultEntitiesOnEmptyAasx);
        }
        catch (Exception ex)
        {
            Log.Error($"Agent 업로드용 AASX export 실패: {targetAasxPath}", ex);
            _dialogService.ShowWarning($"공유 경로 AASX 저장 실패:\n{ex.Message}");
            return;
        }
        if (!exported)
        {
            _dialogService.ShowWarning("내보낼 프로젝트가 없습니다.");
            return;
        }

        // PLC 설정 검증 + 공유 경로 저장 — Agent 가 같은 PlcConnection.json 을 읽어 게이트웨이를 구성한다.
        // 검증 실패면 AASX 까지만 저장 (DSPilot 동기화는 유효) — Agent 세션은 기록하지 않는다.
        var plcConfig = Simulation.BuildPlcGatewayConfig(out var errors);
        if (plcConfig is null)
        {
            _dialogService.ShowWarning(
                "PLC 설정 검증 실패 — 모델(AASX)은 저장됐지만 Agent 세션은 기록하지 않았습니다:\n  - "
                + string.Join("\n  - ", errors));
            StatusText = "Agent 업로드 실패 — PLC 설정 오류";
            return;
        }
        Simulation.PlcSettings.Save();

        // session.RuntimeMode 로 Agent 가 engine 모드를 결정 — Control 이면 read-write, 그 외 read-only.
        var modeName = Simulation.SelectedRuntimeMode == Ds2.Core.RuntimeMode.Control ? "Control" : "Monitoring";
        var session = Promaker.Shared.AgentSession.ForCurrentDefaults(requestedBy: "promaker", runtimeMode: modeName);
        if (!session.TryWrite())
        {
            _dialogService.ShowWarning("Agent 세션 기록 실패 — 공유 폴더 권한을 확인하세요.");
            StatusText = "Agent 업로드 실패 — 세션 기록 불가";
            return;
        }

        // 네트워크 대상이면 방금 로컬 공유폴더에 만든 모델/설정/세션을 원격 Agent 로 zip 전송.
        // (원격 Agent 가 풀어서 자기 공유폴더 배치 + session 경로 로컬 교정 + active.flag → 모니터링 시작)
        if (CurrentAgentTransferTarget.Kind == AgentTransferTargetKind.Network)
        {
            StatusText = $"원격 Agent({CurrentAgentTransferTarget.Ip}) 업로드 중...";
            var (ok, msg) = await AgentUploadClient.UploadAsync(CurrentAgentTransferTarget.Ip);
            StatusText = msg;
            if (!ok) _dialogService.ShowWarning(msg);
            return;
        }

        StatusText = SimulationHubBridge.IsAgentAvailable
            ? $"Agent에 업로드됨 ({modeName}): {targetAasxPath}"
            : "Agent에 업로드됨 — Agent 서비스 미실행, 서비스 시작 시 자동 적용";
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
