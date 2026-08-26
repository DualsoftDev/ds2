using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// System/디바이스 트리의 인스턴스 간 복사/붙여넣기 (OS 클립보드 패키지 경로).
/// 기존 내부 클립보드(_clipboardSelection, Flow/Work/Call)와 별개의 경로 — 무회귀.
/// 코어는 F# SystemPackage(폐포 수집 + Guid remap)가 전담하고, 여기는 봉투/상태 표시만.
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

        if (SystemPackageClipboard.TryCopy(_store, roots, out var error))
        {
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
        else
        {
            _dialogService.ShowWarning($"시스템 클립보드 복사 실패:\n{error}");
        }
    }

    /// <summary>
    /// OS 클립보드의 시스템 패키지 붙여넣기 시도. 처리했으면(성공/오류 안내 포함) true,
    /// 패키지가 아예 없으면 false (호출부가 기존 "Clipboard is empty" 경로 유지).
    /// </summary>
    private bool TryPasteSystemPackageFromOsClipboard()
    {
        if (!SystemPackageClipboard.HasPackage())
            return false;

        var envelope = SystemPackageClipboard.TryRead(out var error);
        if (envelope is null)
        {
            if (!string.IsNullOrEmpty(error))
            {
                _dialogService.ShowWarning(error);
                return true;   // 패키지가 있었지만 사용할 수 없음 — 안내로 종결
            }
            return false;
        }

        var project = Queries.allProjects(_store).FirstOrDefault();
        if (project is null)
        {
            StatusText = "붙여넣을 프로젝트가 없습니다.";
            return true;
        }

        DsStore source;
        try
        {
            source = SystemPackageClipboard.DeserializeStore(envelope);
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"시스템 패키지 역직렬화 실패:\n{ex.Message}");
            return true;
        }

        var roots = envelope.Roots
            .Where(r => source.SystemsReadOnly.ContainsKey(r.Id))
            .Select(r => new SystemImportRoot(r.Id, r.IsActive))
            .ToList();
        if (roots.Count == 0)
        {
            _dialogService.ShowWarning("클립보드 패키지에 가져올 시스템이 없습니다.");
            return true;
        }

        if (!TryEditorFunc(
                () => _store.ImportSystemsFrom(source, project.Id, roots),
                out SystemImportSummary? summary,
                fallback: null))
            return true;

        if (summary is not null)
            ReportSystemImport(summary, "클립보드");
        return true;
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
