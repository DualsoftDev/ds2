// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 파일 로그가 실제로 디스크에 떨어지는지. 이 경로가 조용히 죽어 있으면 서비스 모드에서 로그가 통째로
/// 사라져 현장 장애를 사후 추적할 수 없다(2026-09-22 사례) — "설정만 있고 안 쓰이는" 상태를 막는 회귀 테스트다.
/// </summary>
public class SharedFileLoggingTests
{
    [Fact]
    public void 파일_로그가_지정한_폴더에_메시지와_예외를_남긴다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dspilot-logtest-" + Guid.NewGuid().ToString("N"));
        var marker = "MARKER-" + Guid.NewGuid().ToString("N");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:SharedFile:Directory"] = dir,
                ["Logging:SharedFile:MinimumLevel"] = "Information",
            })
            .Build();

        try
        {
            using (var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddSharedFile(config);
            }))
            {
                var logger = factory.CreateLogger("DSPilot.Tests.Fake");
                logger.LogInformation("{Marker} 정보 한 줄", marker);
                logger.LogError(new InvalidOperationException(marker + "-EX"), "{Marker} 예외 한 줄", marker);
                // Debug 는 MinimumLevel=Information 에 걸려 파일에 남지 않아야 한다.
                logger.LogDebug("{Marker}-DEBUG 는 걸러져야 한다", marker);
            } // LoggerFactory 는 외부 주입 provider 를 Dispose 하지 않는다 — 기록은 백그라운드로 이어진다.

            var logFile = Path.Combine(dir, "dspilot.log");

            // 기록이 비동기라 잠깐 기다린다(운영과 같은 경로 — 호출 스레드를 막지 않는 대가).
            var text = WaitForText(logFile, marker + " 예외 한 줄", TimeSpan.FromSeconds(10));
            var dump = $"\n--- {logFile} ---\n{text}\n--- end ---";
            Assert.True(text.Contains(marker + " 정보 한 줄"), "정보 로그가 없습니다." + dump);
            // 카테고리가 실려야 어느 코드에서 난 로그인지 알 수 있다.
            Assert.True(text.Contains("DSPilot.Tests.Fake"), "카테고리가 없습니다." + dump);
            // 예외 본문까지 남아야 원인 추적이 된다.
            Assert.True(text.Contains(marker + "-EX"), "예외 본문이 없습니다." + dump);
            // MinimumLevel 게이트가 동작해야 한다.
            Assert.False(text.Contains(marker + "-DEBUG"), "Debug 가 걸러지지 않았습니다." + dump);
        }
        finally
        {
            // appender 가 파일 핸들을 놓아야 임시 폴더를 지울 수 있다.
            try { log4net.LogManager.GetRepository(typeof(SharedFileLogging).Assembly).Shutdown(); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { /* 임시 폴더 — 실패해도 무방 */ }
        }
    }

    /// <summary>기대한 문구가 파일에 나타날 때까지 폴링한다. 시간 안에 못 찾으면 마지막 내용으로 실패시킨다.</summary>
    private static string WaitForText(string path, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                text = ReadWhileLocked(path);
                if (text.Contains(expected)) return text;
            }
            Thread.Sleep(50);
        }
        return text;
    }

    /// <summary>
    /// log4net 은 기록 중인 파일을 쓰기로 열어 둔다. Windows 규칙상 뒤에 여는 쪽의 공유 모드가 기존 핸들의
    /// 접근(Write)을 허용해야 하므로 <see cref="File.ReadAllText(string)"/>(FileShare.Read)로는 못 읽는다.
    /// 운영에서 Get-Content·메모장이 되는 것과 같은 조건(ReadWrite 공유)으로 읽는다.
    /// </summary>
    private static string ReadWhileLocked(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
