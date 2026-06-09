using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunAsHelper.Service.Core;

namespace RunAsHelper.Service.Worker;

internal sealed class RunAsHelperService(ILogger<RunAsHelperService> logger) : BackgroundService
{
    private readonly ElevationLauncher _launcher = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _launcher.LogMessage += msg => logger.LogInformation("{Message}", msg);

        logger.LogInformation("Acquiring elevated token...");
        await Task.Run(_launcher.Initialize, stoppingToken);

        if (_launcher.IsReady)
            logger.LogInformation("Token acquired. Listening for launch requests.");
        else
            logger.LogWarning("Token acquisition failed. Will retry on each request.");

        var pipeServer = new PipeServer(_launcher, logger);
        await pipeServer.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _launcher.ReleaseToken();
        await base.StopAsync(cancellationToken);
    }
}
