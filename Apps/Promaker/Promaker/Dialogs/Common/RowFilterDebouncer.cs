using System;
using System.Windows.Threading;

namespace Promaker.Dialogs;

/// <summary>
/// 그리드 필터 입력의 150ms 디바운스 — 빠른 타이핑 중 매 키마다 전체 행 재평가하는
/// 비용을 회피한다. TagWizardDialog / TagInspectorDialog 양쪽이 동일 패턴이라 공용 헬퍼로 추출.
/// </summary>
internal sealed class RowFilterDebouncer
{
    private readonly DispatcherTimer _timer;

    public RowFilterDebouncer(Action onTick, int milliseconds = 150)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            onTick();
        };
    }

    /// <summary>다음 발화까지 타이머 재설정 — 연속 입력 중에는 마지막 입력 후 한 번만 발화.</summary>
    public void Bump()
    {
        _timer.Stop();
        _timer.Start();
    }
}
