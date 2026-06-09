using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RunAsHelper.Service.Worker;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "RunASHelper")
    .ConfigureServices(services =>
    {
        services.AddHostedService<RunAsHelperService>();
    })
    .Build();

await host.RunAsync();
