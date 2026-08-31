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
/// System/디바이스 트리의 인스턴스 간 복사/붙여넣기 (패키지 경로).
/// 기존 내부 클립보드(_clipboardSelection, Flow/Work/Call)와 별개의 경로 — 무회귀.
///
/// 전송층은 2중화: 파일 채널(SystemPackageSpool)이 정본이고 OS 클립보드는 best-effort.
/// Windows 클립보드는 전역 배타 자원이라 외부 프로세스(클립보드 관리자·원격 데스크톱·보안
/// 에이전트)가 점유/차단하면 앱이 손쓸 수 없이 실패한다(CLIPBRD_E_CANT_OPEN) — 그 경우에도
/// Ctrl+C/Ctrl+V 가 성립해야 하므로 사용자에게 환경 조치를 요구하지 않는다.
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

        // 리빌드 없는 작업 — 즉시 해제(hideAfterRebuild: false).
        _ = RunBusyAsync("시스템 복사 중...", async () =>
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

            // 파일 채널 먼저 — 클립보드가 막혀도 붙여넣기가 성립해야 한다(정본).
            var names = roots.Select(r => r.Name).ToList();
            var spooled = SystemPackageSpool.TryWritePayload(envelopeJson);

            // STA — UI 스레드 (await 후 복귀 지점). 클립보드는 best-effort (점유 실패 = 예외 아님).
            var writeMode = await SystemPackageClipboard.SetPackageTextAsync(envelopeJson);
            var published = writeMode != SystemPackageClipboard.WriteMode.Failed;

            // 클립보드 결과를 확정한 뒤 meta 커밋 = 스풀 공개. 게시 실패 시엔 그 시점의 시퀀스를
            // 함께 남겨, 붙여넣기 측이 "클립보드에 남은 옛 패키지"를 스풀보다 우선하지 않게 한다.
            if (spooled)
                spooled = SystemPackageSpool.TryCommitMeta(
                    names, SystemPackageClipboard.AppVersion, published, SystemPackageClipboard.CurrentSequence);

            if (!published && !spooled)
            {
                // 두 채널 모두 실패 = 진짜 복사 실패. 진단은 로그에 남아 있다.
                _dialogService.ShowWarning(
                    "복사에 실패했습니다 — 클립보드가 다른 프로그램에 점유되어 있고 "
                    + "파일 채널 기록도 실패했습니다.\n"
                    + "로그(도움말 ▸ 로그)의 진단 줄을 확인하세요.");
                return;
            }

            // "가장 최근 복사가 이긴다" — 이전 내부 클립보드(Flow/Work/Call)가 남아 있으면
            // 붙여넣기에서 그쪽이 우선돼 혼란스러우므로 비운다 (cut visual 포함).
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            _pasteCount = 0;
            Selection.ApplyCutPendingVisuals([]);

            var nameList = string.Join(", ", names);
            StatusText = $"시스템 복사됨: {nameList} — 다른 프로메이커 창에서 Ctrl+V 로 붙여넣기";
            switch (writeMode)
            {
                case SystemPackageClipboard.WriteMode.Volatile:
                    // 플러시 실패 폴백 = 이 프로세스가 클립보드 소유자 — 창을 닫으면 클립보드는 비지만
                    // 파일 채널이 남아 있으므로 붙여넣기 자체는 계속 가능하다.
                    Log.Warn("System package clipboard write fell back to volatile (no OleFlushClipboard).");
                    break;
                case SystemPackageClipboard.WriteMode.Failed:
                    Log.Warn("System package clipboard write failed — file channel only.");
                    StatusText += " (클립보드가 점유돼 파일 채널로 복사됨)";
                    break;
            }
            RefreshEditorCommandStates();
        }, hideAfterRebuild: false, failPrefix: "시스템 복사 실패");
    }

    /// <summary>
    /// 전송층(클립보드 → 파일 채널 폴백)에서 시스템 패키지 붙여넣기 시도. 패키지가 있으면
    /// true 를 즉시 반환하고 본 작업은 비동기 진행 (역직렬화=배경, 병합+리빌드=UI).
    /// 양쪽 다 없으면 false (호출부가 기존 "Clipboard is empty" 경로 유지).
    /// </summary>
    private bool TryPasteSystemPackage()
    {
        var meta = SystemPackageSpool.TryReadMeta();

        // 채널 우선순위: 스풀이 "클립보드에 못 올라간 복사분"이고 그 뒤로 클립보드가 바뀌지도
        // 않았다면, 클립보드에 남은 패키지는 확실히 더 낡은 것 → 스풀을 정본으로 쓴다(확인 불필요).
        var spoolIsNewer = meta is { ClipboardPublished: false }
                        && meta.ClipboardSequence == SystemPackageClipboard.CurrentSequence;

        // 클립보드 원문 읽기는 STA 필수 — 여기(UI)서 확보하고 파싱부터 배경으로.
        var rawText = !spoolIsNewer && SystemPackageClipboard.HasPackage()
            ? SystemPackageClipboard.TryGetRawText()
            : null;
        var source = "클립보드";

        if (rawText is null)
        {
            if (meta is null)
                return false;

            // 애매한 경우(클립보드를 못 읽거나 비었고, 스풀이 최신이라는 근거도 없음)에만
            // 무엇을·언제 복사한 것인지 밝히고 확인을 받는다(옛 복사분 오붙임 방지).
            // 확인 다이얼로그는 BusyOverlay 진입 전에 띄운다(오버레이는 입력을 차단한다).
            var elapsed = DateTime.UtcNow - meta.WrittenAtUtc;
            var when = elapsed < TimeSpan.FromMinutes(1)
                ? "방금"
                : elapsed < TimeSpan.FromHours(1)
                    ? $"{(int)elapsed.TotalMinutes}분 전"
                    : $"{(int)elapsed.TotalHours}시간 전";
            var what = string.Join(", ", meta.Names);
            if (!spoolIsNewer && !_dialogService.Confirm(
                    $"클립보드에서 시스템 패키지를 읽을 수 없습니다.\n"
                    + $"{when} 복사한 '{what}' 을(를) 붙여넣을까요?",
                    "시스템 붙여넣기"))
                return true;   // 사용자가 거절 = 처리 완료(호출부의 "Clipboard is empty" 문구 억제)

            rawText = SystemPackageSpool.TryReadPayload();
            if (rawText is null)
                return false;
            source = "파일 채널";
        }

        var payload = rawText;
        _ = RunBusyAsync($"시스템 붙여넣는 중... ({source})",
            () => PasteSystemPackageCoreAsync(payload, source),
            failPrefix: "시스템 붙여넣기 실패");
        return true;
    }

    private async Task PasteSystemPackageCoreAsync(string rawText, string sourceLabel)
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

        // 병합 + 트리 리빌드 = UI 스레드 계약 구간 (여기부터 스피너 정지 가능,
        // 오버레이 해제는 RunBusyAsync 가 리빌드 완료 시점으로 미룬다)
        if (!TryEditorFunc(
                () => _store.ImportSystemsFrom(source, project.Id, roots),
                out SystemImportSummary? summary,
                fallback: null))
            return;

        if (summary is not null)
            ReportSystemImport(summary, sourceLabel);
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
