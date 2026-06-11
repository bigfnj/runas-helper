using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RunAsHelper.Service.Core;
using RunAsHelper.Shared.Protocol;

namespace RunAsHelper.Service.Worker;

internal sealed class PipeServer(ElevationLauncher launcher, ILogger logger)
{
    private const string PipeName = "RunAsHelper";

    // Serializes launches so log messages route to the correct connection.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to create named pipe.");
                await Task.Delay(5_000, ct);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pipe accept error.");
                pipe.Dispose();
                continue;
            }

            // Fire-and-forget — HandleConnectionAsync owns the pipe lifetime.
            _ = HandleConnectionAsync(pipe, ct);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        // Also allow the service itself (running as SYSTEM).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize:  4096,
            outBufferSize: 4096,
            pipeSecurity:  security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var request = await PipeProtocol.ReadLaunchRequestAsync(pipe, ct);
                if (request is null) return;

                logger.LogInformation("{Verb} request: '{CommandLine}' priority=0x{Priority:X}",
                    request.Verb, request.CommandLine, request.Priority);

                await _gate.WaitAsync(ct);
                try
                {
                    var logChannel = Channel.CreateUnbounded<string>(
                        new UnboundedChannelOptions { SingleWriter = true });

                    void LogHandler(string msg) => logChannel.Writer.TryWrite(msg);
                    launcher.LogMessage += LogHandler;
                    try
                    {
                        var launchTask = Task.Run(() =>
                        {
                            bool ok = request.Verb == "validate"
                                ? launcher.ValidateToken(out _)
                                : launcher.LaunchElevated(
                                    request.CommandLine, request.Priority,
                                    request.WorkingDirectory, request.ShowWindow, request.Account);
                            logChannel.Writer.Complete();
                            return ok;
                        }, ct);

                        await foreach (string msg in logChannel.Reader.ReadAllAsync(ct))
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("log", msg), ct);

                        bool result = await launchTask;

                        await PipeProtocol.WriteAsync(pipe,
                            new PipeMessage("result", result ? "Success" : "Failed"), ct);
                    }
                    // Always unsubscribe, even if the client disconnects mid-launch;
                    // otherwise stale handlers accumulate on the shared launcher.
                    finally { launcher.LogMessage -= LogHandler; }
                }
                finally { _gate.Release(); }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Pipe handler error."); }
        }
    }
}
