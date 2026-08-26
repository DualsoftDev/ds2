using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Promaker.ViewModels;

/// <summary>
/// BusyOverlay 수명주기 단일 진입점 — "busy 진입/해제(리빌드 연동)" 관용구의 유일 사본.
/// 신규 장시간 작업은 전부 이 헬퍼를 쓸 것. (파일 열기 OpenFilePath 의 원조 패턴만
/// 최다 사용 경로 회귀 리스크를 피해 의도적으로 미이관 — 이관 시 예외 전파 의미가 바뀐다.)
/// </summary>
public partial class MainViewModel
{
    /// <summary>중첩 busy 카운터 — 겹친 작업(연속 Ctrl+V 등) 중 먼저 끝난 쪽이
    /// 오버레이를 조기 해제하던 결함을 단일 지점에서 방지.</summary>
    private int _busyDepth;

    /// <summary>
    /// BusyOverlay 아래에서 작업 실행. work 는 await 포함 다단계 흐름 가능
    /// (배경 직렬화 → UI 병합 등). 예외는 경고 다이얼로그로 종결(failPrefix 로 문구 지정).
    /// hideAfterRebuild=true(기본)면 작업이 트리 리빌드를 큐잉한 경우 리빌드 완료 시점에
    /// 해제, false 면 즉시 해제 (리빌드 없는 작업 — 복사 등).
    /// </summary>
    private async Task RunBusyAsync(
        string message, Func<Task> work, bool hideAfterRebuild = true, string? failPrefix = null)
    {
        // self-heal: 오버레이가 꺼져 있으면 depth=0 이어야 정상 — Reset/PrepareForLoadedStore 가
        // _pendingRebuildActions 를 비우며 deferred ReleaseBusy 가 유실된 경우의 잔존 depth 회복.
        if (!IsBusy)
            _busyDepth = 0;
        _busyDepth++;
        BusyMessage = message;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"{failPrefix ?? "작업 실패"}:\n{ex.Message}");
        }
        finally
        {
            if (hideAfterRebuild && _rebuildQueued)
                _pendingRebuildActions.Add(ReleaseBusy);
            else
                ReleaseBusy();
        }
    }

    /// <summary>동기 편집 작업용 어댑터 — 오버레이를 먼저 렌더(Yield)한 뒤 실행.
    /// 다이얼로그가 필요한 분기는 다이얼로그를 먼저 다 받고 store 작업만 감쌀 것
    /// (BusyOverlay 가 입력을 차단하므로 오버레이와 모달이 겹치면 안 된다).</summary>
    private Task RunBusyAsync(
        string message, Action work, bool hideAfterRebuild = true, string? failPrefix = null) =>
        RunBusyAsync(
            message,
            async () =>
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                work();
            },
            hideAfterRebuild,
            failPrefix);

    private void ReleaseBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth == 0)
            IsBusy = false;
    }
}
