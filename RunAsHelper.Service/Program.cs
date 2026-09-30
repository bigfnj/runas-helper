using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RunAsHelper.Service.Worker;

// The install folder is on PATH from 2.3.0, so "RunAsHelper.Service" is typeable. Started from
// a shell this host would sit there as a console app holding the pipe name with a user token
// that cannot acquire the TrustedInstaller token, and clients would connect to it instead of
// the real service. (It could not pass as the tray: PipeServer.IsRunAsHelperTray accepts only
// an image named RunAsHelper.exe beside the service exe, and tray-level verbs also need that
// client elevated.) Refuse anything that is not the Service Control Manager, which is what
// IsWindowsService() reports (the parent is services.exe in session 0). Nothing is written to
// the event log before this point, so an interactive run leaves no entry behind.
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
