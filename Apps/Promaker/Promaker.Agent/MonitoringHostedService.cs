using System.Threading;
using System.Threading.Tasks;
using log4net;
using Microsoft.Extensions.Hosting;

namespace Promaker.Agent;

/// <summary>
/// <see cref="MonitoringSupervisor"/> 를 IHostedService 로 래핑.
/// Generic Host (UseWindowsService) 가 시작/정지 시그널을 받아 supervisor lifecycle 로 위임.
/// </summary>
internal sealed class MonitoringHostedService : IHostedService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MonitoringHostedService));
    private readonly MonitoringSupervisor _supervisor;

    public MonitoringHostedService(MonitoringSupervisor supervisor)
    {
        _supervisor = supervisor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Info("MonitoringHostedService.StartAsync — booting supervisor.");
        return _supervisor.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Info("MonitoringHostedService.StopAsync — disposing supervisor.");
        await _supervisor.DisposeAsync().ConfigureAwait(false);
    }
}
