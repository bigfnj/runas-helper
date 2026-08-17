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
        logger.LogInformation("Acquiring elevated token...");
        await Task.Run(() => _launcher.Initialize(msg => logger.LogInformation("{Message}", msg)), stoppingToken);

        if (_launcher.IsReady)
            logger.LogInformation("Token acquired. Listening for launch requests.");
        else
        {
            logger.LogWarning("Token acquisition failed. Will retry on each request.");
            EventLogHelper.TokenFailed("Token acquisition failed at service start; will retry on each launch request.");
        }

        EventLogHelper.ServiceStarted();

        var pipeServer = new PipeServer(_launcher, logger);
        await pipeServer.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        EventLogHelper.ServiceStopped();
        _launcher.ReleaseToken();
        await base.StopAsync(cancellationToken);
    }
}
