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
        // Kick off token acquisition in the background so the pipe is reachable
        // immediately after SCM marks the service Running. LaunchElevated calls
        // Initialize() lazily and is idempotent, so any launch request that
        // arrives before the token is cached will wait inside _initLock without
        // dropping the request.
        _ = Task.Run(() =>
        {
            logger.LogInformation("Acquiring elevated token...");
            _launcher.Initialize(msg => logger.LogInformation("{Message}", msg));
            if (_launcher.IsReady)
                logger.LogInformation("Token acquired. Ready for launch requests.");
            else
            {
                logger.LogWarning("Token acquisition failed. Will retry on each request.");
                EventLogHelper.TokenFailed("Token acquisition failed at service start; will retry on each launch request.");
            }
        }, stoppingToken);

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
