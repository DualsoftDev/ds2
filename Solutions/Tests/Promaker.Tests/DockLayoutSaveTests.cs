using System;
using System.IO;
using Promaker.Dock;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// dock layout 저장은 종료 경로(MainWindow.Window_Closing)에서 일어난다. 여기서 던진 예외는
/// App 의 DispatcherUnhandledException(Handled=false) 을 타고 그대로 프로세스 종료가 되므로 —
/// 현장에서 "닫는 순간 튕김" 으로 보고된 경로 — 저장은 실패해도 기존 파일을 망가뜨리지 않아야 하고
/// 쓰레기 임시 파일을 남기지 않아야 한다.
/// </summary>
public class DockLayoutSaveTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "promaker-dock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveLayout_writes_the_file_and_leaves_no_temp_behind()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "dock-layout-v2.xml");
        try
        {
            StaTestRunner.Run(() =>
            {
                var host = new DockHost();
                host.SaveLayout(path);
                host.SaveLayout(path); // 덮어쓰기 경로도 같은 규칙
            });

            Assert.True(File.Exists(path));
            Assert.NotEmpty(File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 정리 실패는 테스트 결과와 무관 */ }
        }
    }

    [Fact]
    public void SaveLayout_failure_keeps_the_previous_file_and_cleans_the_temp()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "dock-layout-v2.xml");
        const string previous = "<previous-layout />";
        File.WriteAllText(path, previous);

        try
        {
            // 다른 인스턴스가 같은 파일을 붙들고 있는 상황의 대역 — 종료가 겹칠 때 실제로 나는 충돌.
            using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 공유 위반은 IOException 으로도, UnauthorizedAccessException 으로도 올라온다(여기 Move 는 후자).
                // 호출 측이 IOException 만 잡으면 그대로 새어 프로세스가 죽는다 — Window_Closing 이
                // Exception 을 받는 이유.
                Assert.ThrowsAny<Exception>(() =>
                    StaTestRunner.Run(() => new DockHost().SaveLayout(path)));
            }

            Assert.Equal(previous, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 정리 실패는 테스트 결과와 무관 */ }
        }
    }
}
