using System;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using Ds2.Aasx;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.LlmAgent;
using Microsoft.Win32;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void CompleteOpen(string filePath, string kind)
    {
        _store.ClearHistory();
        _currentFilePath = filePath;
        FileWatcher.RecordMTime();
        IsDirty = false;
        HasProject = true;
        LlmChatVm?.OnProjectOpened();
        UpdateTitle();
        Log.Info($"{kind} opened: {filePath}");
        StatusText = $"Opened: {Path.GetFileName(filePath)}";

        // 최근 파일 목록에 추가
        RecentFilesManager.AddRecentFile(filePath);
        _dispatcher.InvokeAsync(LoadRecentFiles);

        RequestRebuildAll(AfterFileLoad);
    }

    private void ReplaceOpenedStore(string filePath, DsStore store, string kind)
    {
        _store.ReplaceStore(store);
        // 레거시 파일 자동 복구 — OriginFlowId 누락 ApiCall 을 Call→Work→Flow 로 채움.
        // YAML/JSON apply 경로 (ToolOperations.queueAddCall) 는 OriginFlowId 를 명시 set 하므로
        // 정상 흐름에서 healed=0. healed>0 면 dispatcher 갭 또는 외부 손상 파일 — silent 데이터
        // 변형 가시성을 위해 Warn 격상 (가시 dialog 까지는 over-spec, 로그 검토 책임).
        var healed = Ds2.Core.CallValidation.healMissingOriginFlowIds(_store);
        if (healed > 0)
            Log.Warn($"OriginFlowId auto-heal: {healed} ApiCall(s) restored from Call→Work→Flow chain. ({kind} '{filePath}')");
        CompleteOpen(filePath, kind);
    }

    private void CompleteSave(string filePath, string kind)
    {
        _currentFilePath = filePath;
        FileWatcher.RecordMTime();
        IsDirty = false;
        UpdateTitle();
        StatusText = "Saved.";
        Log.Info($"{kind} saved: {filePath}");
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (!GuardSimulationSemanticEdit("파일 열기"))
            return;

        if (!ConfirmDiscardChanges()) return;

        var dlg = new OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog() != true) return;

        OpenFilePath(dlg.FileName);
    }

    /// <summary>
    /// 지정된 경로의 파일을 연다. 드래그 &amp; 드롭에서도 재사용.
    /// 큰 프로젝트(AASX 등)는 수 초 걸릴 수 있으므로 BusyOverlay 를 먼저 렌더한 뒤
    /// Background 우선순위로 본 작업을 시작 → 사용자가 "로딩 중" 표시를 즉시 확인.
    /// 본 작업이 RequestRebuildAll 을 큐잉하면 그 rebuild 가 끝난 시점에 IsBusy=false.
    /// </summary>
    internal void OpenFilePath(string fileName)
    {
        BusyMessage = $"파일을 여는 중... {Path.GetFileName(fileName)}";
        IsBusy = true;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                OpenFilePathCore(fileName);
            }
            finally
            {
                // CompleteOpen 이 RequestRebuildAll 을 큐잉했으면 rebuild 완료 후 hide.
                // (실패 분기 / 빠른 분기에서는 큐잉이 없으므로 즉시 hide.)
                if (_rebuildQueued)
                    _pendingRebuildActions.Add(() => IsBusy = false);
                else
                    IsBusy = false;
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenFilePathCore(string fileName)
    {
        // _loadedAsLossy 는 yaml 분기에서만 set. 다른 분기 진입 직전 안전 reset.
        _loadedAsLossy = false;

        if (FileTypeProbe.IsYaml(fileName))
        {
            TryRunFileOperation(
                $"Open YAML '{fileName}'",
                () =>
                {
                    var yamlText = File.ReadAllText(fileName, Encoding.UTF8);
                    var result = ModelProtocolYamlIO.loadStoreFromYamlText(yamlText);
                    if (!TryGetResult(
                            result,
                            err => $"YAML 불러오기 실패:\n\n{err}",
                            out var store))
                        return;

                    PrepareForLoadedStore();
                    _loadedAsLossy = true;
                    ReplaceOpenedStore(fileName, store, "YAML");
                },
                ex => $"YAML 불러오기 실패: {ex.Message}");
        }
        else if (FileTypeProbe.IsMermaid(fileName))
        {
            TryRunFileOperation(
                $"Open Mermaid '{fileName}'",
                () =>
                {
                    if (!TryGetResult(
                            Ds2.Mermaid.MermaidImporter.loadProjectFromFile(fileName),
                            errors => $"Mermaid 불러오기 실패:\n{JoinLines(errors)}",
                            out var store))
                        return;

                    PrepareForLoadedStore();
                    ReplaceOpenedStore(fileName, store, "Mermaid");
                },
                ex => $"Mermaid 불러오기 실패: {ex.Message}");
        }
        else if (FileTypeProbe.IsAasx(fileName))
        {
            TryRunFileOperation(
                $"Open AASX '{fileName}'",
                () =>
                {
                    var result = AasxImporter.importIntoStoreWithError(_store, fileName);
                    if (result.IsError)
                    {
                        Log.Warn($"AASX open failed: {result.ErrorValue}");
                        _dialogService.ShowWarning($"AASX 파일 열기 실패:\n\n{result.ErrorValue}");
                        return;
                    }

                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "AASX");
                },
                ex => $"AASX 파일 열기 실패:\n\n{ex.Message}");
        }
        else
        {
            TryRunFileOperation(
                $"Open file '{fileName}'",
                () =>
                {
                    _store.LoadFromFile(fileName);
                    // 레거시 파일 자동 복구 — OriginFlowId 누락 ApiCall 을 Call→Work→Flow 로 채움.
                    // (과거 Panel.buildApiCall 경유 생성 시 미설정되던 버그의 뒤처리)
                    // healed>0 면 외부 손상/구버전 파일 — silent 데이터 변형 가시성 위해 Warn 격상.
                    var healed = Ds2.Core.CallValidation.healMissingOriginFlowIds(_store);
                    if (healed > 0)
                        Log.Warn($"OriginFlowId auto-heal: {healed} ApiCall(s) restored from Call→Work→Flow chain. ('{fileName}')");
                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "File");
                },
                ex => $"Failed to open file: {ex.Message}");
        }
    }

    private void AfterFileLoad()
    {
        ExplorerRebindRequested?.Invoke();

        // Control Tree 전체 확장
        ExpandAllNodes(ControlTreeRoots);

        // 첫 번째 System 캔버스를 띄움 (Flow 하이라이트 없이)
        ActivateInitialSystemTab();

        // 3D 창이 열려있으면 창 내부 참조·DeviceTree·씬 모두 새 프로젝트로 재동기화
        ResyncView3DIfOpen();

        // AutoLayoutIfNeeded가 좌표 없는 노드에 자동 배치를 적용하면서
        // undo 항목을 생성할 수 있으므로, 로드 완료 후 초기 상태로 확정
        _store.ClearHistory();
        IsDirty = false;

        // YAML lossy 재구성 — 시뮬 결과·position·alias 잃음. 사용자에게 "영구 보존은 .sdf SaveAs" 시그널.
        // CompleteOpen→AfterFileLoad 가 IsDirty=false 로 덮어쓰는 race 의 후속 정정.
        if (_loadedAsLossy)
        {
            IsDirty = true;
            _loadedAsLossy = false;
        }

        UpdateTitle();
    }

    private static void ExpandAllNodes(IEnumerable<EntityNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAllNodes(node.Children);
        }
    }
}
