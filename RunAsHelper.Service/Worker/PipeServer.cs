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

    // Whether CLI-sourced launches are permitted. Defaults OFF and is controlled
    // by the (elevated, human-driven) tray via the "setcli" verb; the tray resets
    // it to off on launch/exit. Tray-sourced launches are never gated by this.
    private volatile bool _allowCli;

    // PID of the tray that enabled the gate. The gate is lazily revoked if that
    // tray is no longer alive (e.g. it crashed without sending "setcli off").
    private volatile uint _allowCliOwnerPid;

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

    private static uint ClientPid(NamedPipeServerStream pipe)
    {
        try
        {
            return NativeMethods.GetNamedPipeClientProcessId(
                pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? pid : 0;
        }
        catch { return 0; }
    }

    // The gate owner is "alive" only if that PID is still a running RunAsHelper
    // tray (guards against PID reuse by an unrelated process).
    private static bool IsTrayAlive(uint pid)
    {
        if (pid == 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return !p.HasExited &&
                   string.Equals(p.ProcessName, "RunAsHelper", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

                logger.LogInformation("{Verb} request ({Source}): '{CommandLine}' priority=0x{Priority:X}",
                    request.Verb, request.Source, request.CommandLine, request.Priority);

                // Configuration: the tray toggles whether CLI-sourced launches are
                // allowed, and we remember which tray enabled it (for liveness).
                if (request.Verb == "setcli")
                {
                    _allowCli = string.Equals(request.CommandLine, "on", StringComparison.OrdinalIgnoreCase);
                    _allowCliOwnerPid = _allowCli ? ClientPid(pipe) : 0;
                    logger.LogInformation("CLI launches {State} (owner pid {Pid}).",
                        _allowCli ? "ENABLED" : "disabled", _allowCliOwnerPid);
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Success"), ct);
                    return;
                }

                // Gate: block command-line-sourced launches unless explicitly allowed.
                // Lazily revoke the gate if the tray that enabled it is gone.
                if (request.Verb == "launch" &&
                    string.Equals(request.Source, "cli", StringComparison.OrdinalIgnoreCase))
                {
                    if (_allowCli && !IsTrayAlive(_allowCliOwnerPid))
                    {
                        logger.LogInformation("CLI gate owner (pid {Pid}) is gone — reverting to disabled.", _allowCliOwnerPid);
                        _allowCli = false;
                        _allowCliOwnerPid = 0;
                    }
                    if (!_allowCli)
                    {
                        logger.LogWarning("Blocked CLI launch (command line disabled): {CommandLine}", request.CommandLine);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                            "Command line is disabled. Enable it in RunAS Helper > Settings > \"Allow command line\"."), ct);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        return;
                    }
                }

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
