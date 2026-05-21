using System;
using System.IO;
using System.Threading.Tasks;
using log4net;
using log4net.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Promaker.Agent;

/// <summary>
/// Promaker.Agent — 헤드리스 모니터링 서비스 진입점.
/// 외부 Generic Host (UseWindowsService) 가 SCM 시그널 처리 + 프로세스 lifecycle 관리.
/// 실제 모니터링 로직은 <see cref="MonitoringSupervisor"/> (IHostedService 로 래핑) 가
/// active.flag / session.json / PlcConnection.json 변경에 반응하여 BackendHost 를 동적으로 start/stop.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        InitializeLogging();
        var log = LogManager.GetLogger("Promaker.Agent");
        var runningAsService = WindowsServiceHelpers.IsWindowsService();

        // Windows Service 로 떴을 때 작업 디렉터리는 System32 이므로 exe 위치로 고정 — 로그 상대경로 보호.
        if (runningAsService)
            Environment.CurrentDirectory = AppContext.BaseDirectory;

        log.Info("================ Promaker.Agent starting ================");
        log.Info($"BaseDirectory : {AppContext.BaseDirectory}");
        log.Info($"RunAsService  : {runningAsService}");

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            if (runningAsService)
                builder.Services.AddWindowsService(o => o.ServiceName = "PromakerAgentService");

            // Supervisor 는 Agent 의 모든 모니터링 lifecycle 의 단일 진입점.
            // 외부 host 가 UseWindowsService 로 SCM 신호 받고, 종료 시 IHostedService.StopAsync →
            // supervisor.DisposeAsync → BackendHost.stop 까지 정상 종료.
            builder.Services.AddSingleton(_ => new MonitoringSupervisor());
            builder.Services.AddHostedService<MonitoringHostedService>();

            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            log.Fatal("Fatal error during Agent host run.", ex);
            return 1;
        }
    }

    private static void InitializeLogging()
    {
        var cfg = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (cfg.Exists)
        {
            var repo = LogManager.GetRepository(typeof(Program).Assembly);
            XmlConfigurator.Configure(repo, cfg);
        }
    }
}
