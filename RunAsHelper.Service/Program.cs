using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RunAsHelper.Service.Worker;

// The install folder is on PATH from 2.3.0, so "RunAsHelper.Service" is typeable. Started from
// a shell this host would sit there as a console app holding the pipe name with a user token
// that cannot acquire the TrustedInstaller token, and its image path would still pass the tray
// identity check for clients. Refuse anything that is not the Service Control Manager, which is
// what IsWindowsService() reports (the parent is services.exe in session 0). Nothing is written
// to the event log before this point, so an interactive run leaves no entry behind.
if (!WindowsServiceHelpers.IsWindowsService())
{
    Console.Error.WriteLine(
        "RunAsHelper.Service.exe is the RunAS Helper service host and is started by the Service " +
        "Control Manager, not from a shell. Use 'RunAsHelper' (the command-line client) or " +
        "'sc start RunASHelper'.");
    return 1;
}

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "RunASHelper")
    .ConfigureServices(services =>
    {
        services.AddHostedService<RunAsHelperService>();
    })
    .Build();

await host.RunAsync();
return 0;
