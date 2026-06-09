using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RunAsTI.Service.Worker;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "RunASHelper")
    .ConfigureServices(services =>
    {
        services.AddHostedService<TrustedInstallerService>();
    })
    .Build();

await host.RunAsync();
