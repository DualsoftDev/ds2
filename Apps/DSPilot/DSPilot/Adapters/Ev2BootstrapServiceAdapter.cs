using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DSPilot.Engine;

namespace DSPilot.Adapters;

/// <summary>
/// F# Ev2Bootstrap을 C#에서 사용하기 위한 IHostedService Adapter
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DatabaseInitialization.Schema.startAsync(_paths, _logger);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
