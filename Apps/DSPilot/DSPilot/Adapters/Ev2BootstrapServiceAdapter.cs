using DSPilot.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace DSPilot.Adapters;

/// <summary>
/// EV2 부트스트랩 서비스 (현재 실제 스키마 생성은 PlcCaptureService가 담당, 여기선 로깅만).
/// </summary>
public class Ev2BootstrapServiceAdapter : IHostedService
{
    private readonly DatabasePaths _paths;
    private readonly ILogger<Ev2BootstrapServiceAdapter> _logger;

    public Ev2BootstrapServiceAdapter(
        DatabasePathResolverAdapter pathResolver,
        ILogger<Ev2BootstrapServiceAdapter> logger)
    {
        _paths = pathResolver.GetDatabasePaths();
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_paths.DspTablesEnabled)
        {
            _logger.LogInformation("DspTables:Enabled=false, skipping DSP schema bootstrap.");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting EV2 Bootstrap Service");
        _logger.LogInformation("EV2 base schema initialization delegated to PlcCaptureService");
        _logger.LogInformation("DB Path: {DbPath}", _paths.SharedDbPath);
        _logger.LogInformation("EV2 Bootstrap completed successfully");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
