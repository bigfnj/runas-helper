using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
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
    // by the (signed, elevated) tray via the "setcli" verb; the tray resets it on
    // launch/exit. When enabled, ANY process that can reach the pipe is elevated —
    // the pipe ACL is the real boundary (Administrators + SYSTEM + InteractiveSid).
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

    // Returns the full image path of the process on the other end of the pipe,
    // or null if it cannot be determined.
    private static unsafe string? GetClientExecutablePath(NamedPipeServerStream pipe)
    {
        uint pid = ClientPid(pipe);
        if (pid == 0) return null;
        IntPtr hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            const int MaxPath = 1024;
            char* buf = stackalloc char[MaxPath];
            uint  len = MaxPath;
            return NativeMethods.QueryFullProcessImageNameW(hProc, 0, buf, ref len)
                ? new string(buf, 0, (int)len)
                : null;
        }
        finally { NativeMethods.CloseHandle(hProc); }
    }

    // True if the pipe client is the signed RunAsHelper tray binary.
    // Verifies both the executable name and the presence of an Authenticode signature.
    private static bool IsRunAsHelperTray(NamedPipeServerStream pipe)
    {
        string? path = GetClientExecutablePath(pipe);
        if (path is null) return false;
        if (!path.EndsWith("RunAsHelper.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // No replacement for Authenticode PE reading
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert is not null;
        }
        catch { return false; }
    }

    // True if the client process holds an elevated (admin) token. Used together
    // with IsRunAsHelperTray to gate setcli — only the elevated, signed tray may
    // open or close the CLI gate.
    private static bool IsClientElevated(NamedPipeServerStream pipe)
    {
        uint pid = ClientPid(pipe);
        if (pid == 0) return false;
        try
        {
            IntPtr hProc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return false;
            try
            {
                if (!NativeMethods.OpenProcessToken(hProc, NativeMethods.TOKEN_QUERY, out IntPtr hToken))
                    return false;
                try   { return NativeMethods.IsTokenElevated(hToken); }
                finally { NativeMethods.CloseHandle(hToken); }
            }
            finally { NativeMethods.CloseHandle(hProc); }
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
        // Allow any interactively logged-on user to connect so that CLI launches
        // can reach the pipe when the gate is open. The gate itself is controlled
        // by the elevated signed tray, and the pipe ACL remains the outer boundary.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
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

                uint clientPid = ClientPid(pipe);
                // Determine identity server-side; Source field is logged for info only.
                bool isTray = IsRunAsHelperTray(pipe);
                // Elevation is only checked when identity passes — saves a syscall for
                // CLI/other connections that skip the tray path entirely.
                bool isTrayElevated = isTray && IsClientElevated(pipe);

                logger.LogInformation(
                    "{Verb} request (source={Source} identity={Identity} pid={Pid}): '{CommandLine}' priority=0x{Priority:X}",
                    request.Verb, request.Source,
                    isTrayElevated ? "tray-elevated" : isTray ? "tray-notelev" : "other",
                    clientPid, request.CommandLine, request.Priority);

                // ── setcli: signed tray + elevated (both required to control the gate) ──
                if (request.Verb == "setcli")
                {
                    if (!isTrayElevated)
                    {
                        string reason = !isTray ? "not the signed tray" : "tray is not elevated";
                        logger.LogWarning("Rejected setcli — {Reason} (pid {Pid}).", reason, clientPid);
                        EventLogHelper.Denied("setcli", $"pid {clientPid}: {reason}");
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        return;
                    }
                    _allowCli = string.Equals(request.CommandLine, "on", StringComparison.OrdinalIgnoreCase);
                    _allowCliOwnerPid = _allowCli ? clientPid : 0;
                    logger.LogInformation("CLI launches {State} (owner pid {Pid}).",
                        _allowCli ? "ENABLED" : "disabled", _allowCliOwnerPid);
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Success"), ct);
                    return;
                }

                // ── launch / validate: elevated signed tray always allowed;
                //    everyone else (any process, any elevation level) needs the CLI gate open ──
                if (!isTrayElevated)
                {
                    // Lazily revoke the gate if the owning tray is gone.
                    if (_allowCli && !IsTrayAlive(_allowCliOwnerPid))
                    {
                        logger.LogInformation(
                            "CLI gate owner (pid {Pid}) is gone — reverting to disabled.",
                            _allowCliOwnerPid);
                        _allowCli = false;
                        _allowCliOwnerPid = 0;
                    }

                    if (!_allowCli)
                    {
                        logger.LogWarning("Blocked launch (CLI gate closed) from pid {Pid}: {CommandLine}",
                            clientPid, request.CommandLine);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                            "Command line is disabled. Enable it in RunAS Helper > Settings > \"Allow command line\"."), ct);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        EventLogHelper.Denied(request.CommandLine, "CLI gate closed");
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
                        string sourceKind = isTrayElevated ? "tray" : "cli";
                        if (request.Verb != "validate")
                            EventLogHelper.RequestReceived(request.CommandLine, clientPid, sourceKind);

                        var launchTask = Task.Run(() =>
                        {
                            if (request.Verb == "validate")
                            {
                                bool ok = launcher.ValidateToken(out _);
                                logChannel.Writer.Complete();
                                return (ok, 0u);
                            }
                            uint pid = launcher.LaunchElevated(
                                request.CommandLine, request.Priority,
                                request.WorkingDirectory, request.ShowWindow, request.Account);
                            logChannel.Writer.Complete();
                            return (pid != 0, pid);
                        }, ct);

                        await foreach (string msg in logChannel.Reader.ReadAllAsync(ct))
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("log", msg), ct);

                        var (result, launchedPid) = await launchTask;

                        // Send PID before result so the tray can call AllowSetForegroundWindow
                        // before acknowledging success — the launched process needs the right
                        // while it is starting up.
                        if (result && launchedPid != 0)
                            await PipeProtocol.WriteAsync(pipe,
                                new PipeMessage("pid", launchedPid.ToString()), ct);

                        if (request.Verb != "validate")
                        {
                            if (result)
                                EventLogHelper.Launched(request.CommandLine, launchedPid);
                            else
                                EventLogHelper.Denied(request.CommandLine, "launch failed");
                        }

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
