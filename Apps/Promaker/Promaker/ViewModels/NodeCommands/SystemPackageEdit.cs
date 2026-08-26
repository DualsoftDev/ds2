using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// System/디바이스 트리의 인스턴스 간 복사/붙여넣기 (OS 클립보드 패키지 경로).
/// 기존 내부 클립보드(_clipboardSelection, Flow/Work/Call)와 별개의 경로 — 무회귀.
/// 코어는 F# SystemPackage(폐포 수집 + Guid remap)가 전담하고, 여기는 봉투/상태 표시만.
///
/// 옵션 B(작업을 스레드로): 직렬화/역직렬화는 Task.Run 배경 실행 — BusyOverlay 스피너가
/// 계속 돈다. UI 계약 구간(클립보드 STA 접근, store 병합, 트리 리빌드)만 UI 스레드 —
/// 병합~리빌드 동안은 스피너가 잠시 정지할 수 있다(구조적 한계).
/// 배경 직렬화 중 store 변형(키보드 단축키 등) 레이스는 Revision 사전/사후 대조로 감지·취소.
/// </summary>
public partial class MainViewModel
{
    /// <summary>선택된 System 노드들을 OS 클립보드에 패키지로 복사.</summary>
    private void CopySystemsToOsClipboard(IReadOnlyList<SelectionKey> keys)
    {
        var project = Queries.allProjects(_store).FirstOrDefault();
        if (project is null)
        {
            StatusText = "복사할 프로젝트가 없습니다.";
            return;
        }

        var roots = new List<SystemPackageClipboard.RootEntry>();
        foreach (var key in keys)
        {
            if (!_store.SystemsReadOnly.TryGetValue(key.Id, out var system))
                continue;
            var isActive = project.ActiveSystemIds.Contains(key.Id);
            roots.Add(new SystemPackageClipboard.RootEntry(key.Id, isActive, system.Name));
        }
        if (roots.Count == 0)
        {
            StatusText = "Nothing to copy.";
            return;
        }

        _ = CopySystemsToOsClipboardAsync(roots);
    }

    private async Task CopySystemsToOsClipboardAsync(List<SystemPackageClipboard.RootEntry> roots)
    {
        BusyMessage = "시스템 복사 중... (클립보드)";
        IsBusy = true;
        try
        {
            // 폐포 수집 + JSON 직렬화 = 배경 스레드 (store 읽기 전용). BusyOverlay 가 마우스를
            // 차단하지만 키보드 단축키는 못 막으므로 Revision 으로 변형 감지 → 안전 취소.
            var revisionBefore = _store.Revision;
            var envelopeJson = await Task.Run(() => SystemPackageClipboard.BuildEnvelopeJson(_store, roots));
            if (_store.Revision != revisionBefore)
            {
                _dialogService.ShowWarning("복사 중 모델이 변경되어 복사를 취소했습니다. 다시 복사하세요.");
                return;
            }

            Clipboard.SetText(envelopeJson);   // STA — UI 스레드 (await 후 복귀 지점)

            // "가장 최근 복사가 이긴다" — 이전 내부 클립보드(Flow/Work/Call)가 남아 있으면
            // 붙여넣기에서 그쪽이 우선돼 혼란스러우므로 비운다 (cut visual 포함).
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            _pasteCount = 0;
            Selection.ApplyCutPendingVisuals([]);

            var names = string.Join(", ", roots.Select(r => r.Name));
            StatusText = $"시스템 복사됨(클립보드): {names} — 다른 프로메이커 창에서 Ctrl+V 로 붙여넣기";
            RefreshEditorCommandStates();
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"시스템 클립보드 복사 실패:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// OS 클립보드의 시스템 패키지 붙여넣기 시도. 패키지가 있으면 true 를 즉시 반환하고
    /// 본 작업은 비동기 진행 (역직렬화=배경, 병합+리빌드=UI). 패키지가 없으면 false
    /// (호출부가 기존 "Clipboard is empty" 경로 유지).
    /// </summary>
    private bool TryPasteSystemPackageFromOsClipboard()
    {
        if (!SystemPackageClipboard.HasPackage())
            return false;
        // 클립보드 원문 읽기는 STA 필수 — 여기(UI)서 확보하고 파싱부터 배경으로.
        var rawText = SystemPackageClipboard.TryGetRawText();
        if (rawText is null)
            return false;

        _ = PasteSystemPackageAsync(rawText);
        return true;
    }

    private async Task PasteSystemPackageAsync(string rawText)
    {
        BusyMessage = "시스템 붙여넣는 중... (클립보드)";
        IsBusy = true;
        try
        {
            // 봉투 파싱 + 부분 store 역직렬화 = 배경 스레드 (독립 객체 생성뿐 — 레이스 없음)
            var (source, envelope, error) = await Task.Run(() =>
            {
                var env = SystemPackageClipboard.ParseEnvelope(rawText, out var err);
                if (env is null)
                    return ((DsStore?)null, env, err);
                return (SystemPackageClipboard.DeserializeStore(env), env, "");
            });

            if (envelope is null || source is null)
            {
                if (!string.IsNullOrEmpty(error))
                    _dialogService.ShowWarning(error);
                else
                    StatusText = "클립보드에서 시스템 패키지를 찾지 못했습니다.";
                return;
            }

            var project = Queries.allProjects(_store).FirstOrDefault();
            if (project is null)
            {
                StatusText = "붙여넣을 프로젝트가 없습니다.";
                return;
            }

            var roots = envelope.Roots
                .Where(r => source.SystemsReadOnly.ContainsKey(r.Id))
                .Select(r => new SystemImportRoot(r.Id, r.IsActive))
                .ToList();
            if (roots.Count == 0)
            {
                _dialogService.ShowWarning("클립보드 패키지에 가져올 시스템이 없습니다.");
                return;
            }

            // 병합 + 트리 리빌드 = UI 스레드 계약 구간 (여기부터 스피너 정지 가능)
            if (!TryEditorFunc(
                    () => _store.ImportSystemsFrom(source, project.Id, roots),
                    out SystemImportSummary? summary,
                    fallback: null))
                return;

            if (summary is not null)
                ReportSystemImport(summary, "클립보드");
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"시스템 붙여넣기 실패:\n{ex.Message}");
        }
        finally
        {
            // 병합이 트리 리빌드를 큐잉했으면 리빌드 완료 시점에 오버레이 해제 (파일 열기 패턴)
            if (_rebuildQueued)
                _pendingRebuildActions.Add(() => IsBusy = false);
            else
                IsBusy = false;
        }
    }

    /// <summary>임포트 결과 요약 표시 — 개명 내역/경고는 조용히 삼키지 않는다 (설계 §6 결과 요약).</summary>
    private void ReportSystemImport(SystemImportSummary summary, string sourceLabel)
    {
        var status =
            $"{sourceLabel}에서 가져옴: 시스템 {summary.SystemCount}개 (+디바이스 {summary.DeviceCount}) — "
            + $"Flow {summary.FlowCount} · Work {summary.WorkCount} · Call {summary.CallCount}";
        if (summary.Renames.Count > 0)
        {
            var renamed = string.Join(", ", summary.Renames.Take(3).Select(r => $"{r.OldName}→{r.NewName}"));
            var more = summary.Renames.Count > 3 ? $" 외 {summary.Renames.Count - 3}건" : "";
            status += $" · 개명 {renamed}{more}";
        }
        StatusText = status;

        if (summary.Warnings.Count > 0)
        {
            const int maxLines = 20;
            var lines = string.Join("\n", summary.Warnings.Take(maxLines));
            var suffix = summary.Warnings.Count > maxLines
                ? $"\n... (총 {summary.Warnings.Count}건)"
                : "";
            _dialogService.ShowWarning($"가져오기 경고 {summary.Warnings.Count}건:\n\n{lines}{suffix}");
        }

        RefreshEditorCommandStates();
    }
}
