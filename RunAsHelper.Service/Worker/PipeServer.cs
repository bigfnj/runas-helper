using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
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

    // Limits concurrent launches. Each connection has its own log channel so
    // messages are routed correctly without serializing launches. The bound (10)
    // prevents runaway resource use while allowing multiple concurrent callers.
    // A 30-second wait timeout prevents new requests from queuing behind a stuck
    // job indefinitely — callers get a "service busy" error and can retry.
    private const int MaxConcurrentLaunches = 10;
    private readonly SemaphoreSlim _launchGate = new(MaxConcurrentLaunches, MaxConcurrentLaunches);

    // Whether CLI-sourced launches are permitted. Defaults OFF and is controlled
    // by the (installed, elevated) tray via the "setcli" verb; the tray resets it
    // on launch/exit. When enabled, ANY process that can reach the pipe is elevated
    // — the pipe ACL is the real boundary (Administrators + SYSTEM + InteractiveSid).
    private volatile bool _allowCli;

    // PID of the tray that enabled the gate. The gate is lazily revoked if that
    // tray is no longer alive (e.g. it crashed without sending "setcli off").
    private volatile uint _allowCliOwnerPid;

    // When the open gate lapses, as UTC ticks (0 = no expiry). Checked lazily on the
    // next request rather than from a timer: the gate only matters at the moment
    // something tries to use it, and this keeps the service timer-free. Long ticks are
    // read/written via Interlocked so a 64-bit value is never torn on a 32-bit read.
    private long _allowCliExpiresUtcTicks;

    // In-flight launches, keyed by a monotonic job id, so the tray can show what is
    // currently holding a launch slot and terminate a job that is stuck. A launch is
    // registered once it holds a slot and removed in the same finally that releases
    // it, so this set always mirrors slot occupancy.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TrackedJob> _jobs = new();
    private int _nextJobId;

    // A tracked launch. hProcess is only retained for capture jobs (a fire-and-forget
    // launch closes its handle immediately and finishes right away), so Kill applies
    // to exactly the jobs that can actually get stuck.
    private sealed class TrackedJob
    {
        public required JobInfo Info { get; init; }
        public volatile uint LivePid;

        // Rolling tail of the child's captured output, so the tray can show what a job
        // is actually doing rather than just its command line. Bounded: a chatty job
        // must not grow the service's memory without limit, and only the recent lines
        // are useful for "why is this stuck?".
        private const int MaxLines = 200;
        private readonly Queue<string> _output = new();

        public void AddOutput(string line)
        {
            lock (_output)
            {
                _output.Enqueue(line);
                while (_output.Count > MaxLines) _output.Dequeue();
            }
        }

        public string[] OutputTail()
        {
            lock (_output) return _output.ToArray();
        }
    }

    // Clears every piece of gate state together, so a revoked gate can never leave a
    // stale owner PID or deadline behind for the next check to reason about.
    private void CloseCliGate()
    {
        _allowCli = false;
        _allowCliOwnerPid = 0;
        Interlocked.Exchange(ref _allowCliExpiresUtcTicks, 0);
    }

    /// <summary>Snapshot of in-flight launches, plus how many slots are in use.</summary>
    private (JobInfo[] Jobs, int InUse) SnapshotJobs()
    {
        var jobs = _jobs.Values
            .Select(j => j.Info with { Pid = j.LivePid })
            .OrderBy(j => j.Id)
            .ToArray();
        return (jobs, jobs.Length);
    }

    // Terminates a launched child by PID. The service runs as LocalSystem, so it can
    // open a TrustedInstaller/SYSTEM child. Opening by PID (rather than caching the
    // handle) keeps this working for every tracked job, and the open itself fails
    // harmlessly if the process has already exited.
    private bool TerminateByPid(uint pid)
    {
        IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
        if (hProc == IntPtr.Zero) return false;
        try { return NativeMethods.TerminateProcess(hProc, 1); }
        finally { NativeMethods.CloseHandle(hProc); }
    }

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

    // True if the pipe client is the RunAsHelper tray binary installed alongside
    // this service. Identity is established by image path — the file must be named
    // RunAsHelper.exe AND live in the same directory as this service's own
    // executable (the per-machine install location). That cannot be faked by a
    // spoofed Source field, and — unlike an Authenticode check — it does not break
    // unsigned local or CI builds, which would otherwise never open the CLI gate.
    // A code signature, when present, is reported for diagnostics (IsClientSigned)
    // and can later be pinned to the official publisher as optional hardening.
    private static bool IsRunAsHelperTray(NamedPipeServerStream pipe)
    {
        string? path = GetClientExecutablePath(pipe);
        if (path is null) return false;
        if (!path.EndsWith("\\RunAsHelper.exe", StringComparison.OrdinalIgnoreCase)) return false;

        string? clientDir  = Path.GetDirectoryName(path);
        string? installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (clientDir is null || installDir is null) return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(clientDir),
            Path.TrimEndingDirectorySeparator(installDir),
            StringComparison.OrdinalIgnoreCase);
    }

    // Best-effort: true if the client binary carries an Authenticode signature.
    // Reported in the connection log for diagnostics; NOT a gate (see
    // IsRunAsHelperTray for why unsigned builds must still be trusted).
    private static bool IsClientSigned(NamedPipeServerStream pipe)
    {
        string? path = GetClientExecutablePath(pipe);
        if (path is null) return false;
        try
        {
#pragma warning disable SYSLIB0057 // No managed replacement for Authenticode PE reading
            return X509Certificate.CreateFromSignedFile(path) is not null;
#pragma warning restore SYSLIB0057
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
        //
        // Be clear about the scope of this rule: INTERACTIVE covers every process
        // in the interactive session, non-elevated ones and standard (non-admin)
        // users included. So an open CLI gate is a SESSION-WIDE grant of
        // TrustedInstaller, not a grant to one caller. That is deliberate -- the
        // gate exists precisely so unelevated scripts can use the service -- and it
        // is why the gate is off by default, is revoked when its owning tray dies,
        // and expires after AppSettings.CliGateMinutes. To make it per-user instead,
        // replace InteractiveSid with the tray owner's user SID.
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
                    "{Verb} request (source={Source} identity={Identity} signed={Signed} pid={Pid}): '{CommandLine}' priority=0x{Priority:X}",
                    request.Verb, request.Source,
                    isTrayElevated ? "tray-elevated" : isTray ? "tray-notelev" : "other",
                    isTray && IsClientSigned(pipe),
                    clientPid, request.CommandLine, request.Priority);

                // ── setcli: signed tray + elevated (both required to control the gate) ──
                if (request.Verb == "setcli")
                {
                    if (!isTrayElevated)
                    {
                        string reason = !isTray ? "not the installed tray" : "tray is not elevated";
                        logger.LogWarning("Rejected setcli — {Reason} (pid {Pid}).", reason, clientPid);
                        EventLogHelper.Denied("setcli", $"pid {clientPid}: {reason}");
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        return;
                    }
                    _allowCli = string.Equals(request.CommandLine, "on", StringComparison.OrdinalIgnoreCase);
                    _allowCliOwnerPid = _allowCli ? clientPid : 0;

                    // Opening (or re-opening) the gate starts a fresh countdown, so a tray
                    // that re-asserts the setting extends it rather than letting a stale
                    // deadline close the gate underneath it.
                    long expiresTicks = 0;
                    if (_allowCli && request.GateMinutes > 0)
                        expiresTicks = DateTime.UtcNow.AddMinutes(request.GateMinutes).Ticks;
                    Interlocked.Exchange(ref _allowCliExpiresUtcTicks, expiresTicks);

                    logger.LogInformation("CLI launches {State} (owner pid {Pid}){Expiry}.",
                        _allowCli ? "ENABLED" : "disabled", _allowCliOwnerPid,
                        expiresTicks == 0
                            ? (_allowCli ? " with no expiry" : "")
                            : $" for {request.GateMinutes} minute(s)");

                    // Report the effective deadline so the tray can mirror the countdown
                    // rather than assume its own clock matches the enforcer's.
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("gate",
                        expiresTicks == 0 ? "0" : request.GateMinutes.ToString()), ct);
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Success"), ct);
                    return;
                }

                // ── jobs / killjob: same gate as setcli (installed tray + elevated) ──
                // Deliberately NOT reachable through the open CLI gate: listing exposes the
                // command lines of elevated launches, and killing is destructive, so neither
                // may be available to every process that can merely reach the pipe.
                if (request.Verb is "jobs" or "killjob" or "joblog")
                {
                    if (!isTrayElevated)
                    {
                        string reason = !isTray ? "not the installed tray" : "tray is not elevated";
                        logger.LogWarning("Rejected {Verb} — {Reason} (pid {Pid}).", request.Verb, reason, clientPid);
                        EventLogHelper.Denied(request.Verb, $"pid {clientPid}: {reason}");
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        return;
                    }

                    // joblog: the captured output a job has produced so far. Same gate as
                    // the listing — this is the child's output from an elevated launch.
                    if (request.Verb == "joblog")
                    {
                        if (int.TryParse(request.CommandLine, out int logId) &&
                            _jobs.TryGetValue(logId, out var logJob))
                        {
                            foreach (string line in logJob.OutputTail())
                                await PipeProtocol.WriteAsync(pipe, new PipeMessage("stdout", line), ct);
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Success"), ct);
                        }
                        else
                        {
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                                "No such job — it may have finished already."), ct);
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        }
                        return;
                    }

                    if (request.Verb == "jobs")
                    {
                        var (jobs, inUse) = SnapshotJobs();
                        foreach (var job in jobs)
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("job",
                                JsonSerializer.Serialize(job, PipeJsonContext.Default.JobInfo)), ct);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("slots",
                            $"{inUse}/{MaxConcurrentLaunches}"), ct);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Success"), ct);
                        return;
                    }

                    // killjob: CommandLine carries the job id.
                    bool killed = false;
                    if (int.TryParse(request.CommandLine, out int killId) &&
                        _jobs.TryGetValue(killId, out var target))
                    {
                        uint pid = target.LivePid;
                        if (pid != 0)
                        {
                            killed = TerminateByPid(pid);
                            logger.LogWarning("Kill job {Id} (pid {Pid}) requested by tray pid {Client}: {Result}.",
                                killId, pid, clientPid, killed ? "terminated" : "failed");
                            EventLogHelper.JobTerminated(killId, pid, target.Info.CommandLine, killed);
                        }
                        else
                        {
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                                "That job has not started a process yet — try again in a moment."), ct);
                        }
                    }
                    else
                    {
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                            "No such job — it may have finished already."), ct);
                    }

                    await PipeProtocol.WriteAsync(pipe,
                        new PipeMessage("result", killed ? "Success" : "Failed"), ct);
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
                        CloseCliGate();
                    }

                    // ...and likewise once its countdown has lapsed, so an open gate is
                    // not left usable for the whole life of a long-running tray session.
                    // Remember which of the two closed it so the single denial below can
                    // say why, rather than emitting a second event for the same request.
                    string closedReason = "CLI gate closed";
                    long expiresTicks = Interlocked.Read(ref _allowCliExpiresUtcTicks);
                    if (_allowCli && expiresTicks != 0 && DateTime.UtcNow.Ticks >= expiresTicks)
                    {
                        logger.LogInformation("CLI gate expired — reverting to disabled.");
                        closedReason = "CLI gate expired";
                        CloseCliGate();
                    }

                    if (!_allowCli)
                    {
                        logger.LogWarning("Blocked launch ({Reason}) from pid {Pid}: {CommandLine}",
                            closedReason, clientPid, request.CommandLine);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                            closedReason == "CLI gate expired"
                                ? "Command line was enabled but the allowance expired. Re-enable it in RunAS Helper > Settings > \"Allow command line\"."
                                : "Command line is disabled. Enable it in RunAS Helper > Settings > \"Allow command line\"."), ct);
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                        EventLogHelper.Denied(request.CommandLine, closedReason);
                        return;
                    }
                }

                // Allow up to 10 concurrent launches; reject new requests quickly
                // if the service is busy rather than queuing them indefinitely.
                bool acquired = await _launchGate.WaitAsync(30_000, ct);
                if (!acquired)
                {
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                        "Service is busy with too many concurrent launches — try again in a moment."), ct);
                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("result", "Failed"), ct);
                    return;
                }
                string sourceKind = isTrayElevated ? "tray" : "cli";
                bool isValidate = request.Verb is "validate" or "validate-system";

                // Track this launch for the lifetime of the slot it holds, so the tray's
                // Active Jobs view mirrors slot occupancy exactly (registered here,
                // removed in the same finally that releases the gate).
                var tracked = new TrackedJob
                {
                    Info = new JobInfo(
                        Id:             Interlocked.Increment(ref _nextJobId),
                        CommandLine:    isValidate ? $"({request.Verb})" : request.CommandLine,
                        Account:        request.Account,
                        Source:         sourceKind,
                        Pid:            0,
                        StartedUnixMs:  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        CaptureOutput:  request.CaptureOutput,
                        TimeoutSeconds: request.TimeoutSeconds),
                };
                _jobs[tracked.Info.Id] = tracked;

                try
                {
                    var logChannel = Channel.CreateUnbounded<string>(
                        new UnboundedChannelOptions { SingleWriter = true });

                    void LogCallback(string msg) => logChannel.Writer.TryWrite(msg);

                    if (!isValidate)
                        EventLogHelper.RequestReceived(request.CommandLine, clientPid, sourceKind);

                    // Run the blocking work (token chain + CreateProcess) on a thread-pool
                    // thread so the pipe log-drain loop runs concurrently.
                    // try/finally ensures logChannel is always completed even if the
                    // launcher throws, preventing the drain loop from hanging forever.
                    var launchTask = Task.Run(() =>
                    {
                        try
                        {
                            if (request.Verb == "validate")
                            {
                                bool ok = launcher.ValidateToken(out _, LogCallback);
                                return (ok, 0u, IntPtr.Zero, (System.IO.Stream?)null);
                            }
                            if (request.Verb == "validate-system")
                            {
                                bool ok = launcher.ValidateSystemToken(out _, LogCallback);
                                return (ok, 0u, IntPtr.Zero, (System.IO.Stream?)null);
                            }

                            // Warn when /capture is requested without a timeout — an infinite
                            // wait blocks this launch slot until the child exits naturally.
                            if (request.CaptureOutput && request.TimeoutSeconds <= 0)
                                LogCallback("[warning] /capture used without /timeout — this launch slot is held until the child process exits. Use /timeout:N to set a ceiling.");

                            var (pid, hProc, stdout) = launcher.LaunchElevated(
                                request.CommandLine, request.Priority,
                                request.WorkingDirectory, request.ShowWindow, request.Account,
                                captureOutput: request.CaptureOutput,
                                log: LogCallback);
                            return (pid != 0, pid, hProc, stdout);
                        }
                        finally
                        {
                            logChannel.Writer.TryComplete();
                        }
                    }, ct);

                    await foreach (string msg in logChannel.Reader.ReadAllAsync(ct))
                        await PipeProtocol.WriteAsync(pipe, new PipeMessage("log", msg), ct);

                    var (result, launchedPid, hProcess, stdoutStream) = await launchTask;
                    tracked.LivePid = launchedPid;

                    // When output capture is active, stream the child's stdout/stderr
                    // back to the caller as "stdout" messages and wait for the child to
                    // exit (up to the requested timeout) before sending "result".
                    if (result && stdoutStream is not null)
                    {
                        uint waitMs = request.TimeoutSeconds > 0
                            ? (uint)(request.TimeoutSeconds * 1_000)
                            : NativeMethods.INFINITE;

                        // Pump stdout on a background task so we don't block the
                        // await below. The read end is an asynchronous pipe, so cancelling
                        // pumpCts aborts a pending ReadLineAsync straight away — that is what
                        // lets a /timeout release this caller (and its launch slot) while the
                        // child keeps running. Disposing the stream cannot do that on its own:
                        // a read on a synchronous handle only returns at EOF, i.e. when the
                        // child finally exits.
                        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var pumpTask = Task.Run(async () =>
                        {
                            try
                            {
                                using var reader = new System.IO.StreamReader(stdoutStream);
                                string? line;
                                while ((line = await reader.ReadLineAsync(pumpCts.Token)) is not null)
                                {
                                    tracked.AddOutput(line);
                                    await PipeProtocol.WriteAsync(pipe, new PipeMessage("stdout", line), ct);
                                }
                            }
                            catch (Exception ex)
                                when (ex is System.IO.IOException or ObjectDisposedException
                                          or OperationCanceledException) { }
                        }, pumpCts.Token);

                        // Wait for the child process to exit (blocking, on a thread-pool thread).
                        uint waitResult = await Task.Run(
                            () => NativeMethods.WaitForSingleObject(hProcess, waitMs), ct);

                        if (waitResult == NativeMethods.WAIT_TIMEOUT)
                        {
                            await PipeProtocol.WriteAsync(pipe, new PipeMessage("log",
                                $"[timeout] Process did not exit within {request.TimeoutSeconds}s — closing output stream (the process keeps running)."), ct);
                            pumpCts.Cancel();
                        }

                        NativeMethods.CloseHandle(hProcess);
                        try { await pumpTask; }
                        catch (OperationCanceledException) { /* expected on timeout */ }
                        await stdoutStream.DisposeAsync();
                    }
                    else if (hProcess != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(hProcess);
                    }

                    // Send PID before result so the tray can call AllowSetForegroundWindow
                    // before acknowledging success — the launched process needs the right
                    // while it is starting up.
                    if (result && launchedPid != 0)
                        await PipeProtocol.WriteAsync(pipe,
                            new PipeMessage("pid", launchedPid.ToString()), ct);

                    if (!isValidate)
                    {
                        if (result)
                            EventLogHelper.Launched(request.CommandLine, launchedPid);
                        else
                            EventLogHelper.Denied(request.CommandLine, "launch failed");
                    }

                    await PipeProtocol.WriteAsync(pipe,
                        new PipeMessage("result", result ? "Success" : "Failed"), ct);
                }
                finally
                {
                    _jobs.TryRemove(tracked.Info.Id, out _);
                    _launchGate.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Pipe handler error."); }
        }
    }
}
