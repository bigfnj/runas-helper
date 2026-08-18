using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunAsHelper.Shared.Protocol;

namespace RunAsHelper.Core;

/// <summary>
/// Sends launch requests to the RunAsHelper Windows service over a named pipe.
/// Thread-safe; multiple concurrent calls are allowed (each opens its own pipe).
/// </summary>
internal sealed class PipeClient
{
    private const string PipeName = "RunAsHelper";

    public event Action<string>? LogMessage;

    private void Log(string msg) => LogMessage?.Invoke(msg);

    /// <summary>
    /// Ask the RunAsHelper service to launch <paramref name="commandLine"/> as TrustedInstaller.
    /// Returns true on success, false if the service is unreachable or the launch failed.
    /// </summary>
    public Task<bool> LaunchElevatedAsync(
        string commandLine,
        uint priority,
        CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority), ct);

    /// <summary>
    /// Launch with an explicit working directory and window state (SW_* value).
    /// Used by saved applications, which carry those settings.
    /// </summary>
    public Task<bool> LaunchElevatedAsync(
        string commandLine,
        uint priority,
        string workingDirectory,
        int showWindow,
        CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority, "launch", workingDirectory, showWindow), ct);

    /// <summary>
    /// Launch with working directory, window state, and account ("ti" for
    /// TrustedInstaller or "system" for LocalSystem).
    /// </summary>
    public Task<bool> LaunchElevatedAsync(
        string commandLine,
        uint priority,
        string workingDirectory,
        int showWindow,
        string account,
        CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority, "launch", workingDirectory, showWindow, account), ct);

    /// <summary>
    /// Ask the service to acquire and release a TrustedInstaller token without
    /// launching anything — used by post-install validation. Streams the service's
    /// log lines (including the resolved account) via <see cref="LogMessage"/>.
    /// </summary>
    public Task<bool> ValidateTokenAsync(CancellationToken ct = default)
        => SendAsync(new LaunchRequest(string.Empty, NativeMethods.NORMAL_PRIORITY_CLASS, "validate"), ct);

    /// <summary>
    /// Ask the service to open and inspect the LocalSystem (SYSTEM) token — the
    /// second validation step alongside <see cref="ValidateTokenAsync"/>.
    /// </summary>
    public Task<bool> ValidateSystemTokenAsync(CancellationToken ct = default)
        => SendAsync(new LaunchRequest(string.Empty, NativeMethods.NORMAL_PRIORITY_CLASS, "validate-system"), ct);

    /// <summary>
    /// Tells the service whether to allow CLI-sourced launches (tray only). When opening
    /// the gate, <paramref name="gateMinutes"/> is how long the service will honour it
    /// before auto-closing it (0 = no expiry); re-sending "on" restarts the countdown.
    /// </summary>
    public Task<bool> SetCommandLineAllowedAsync(bool allow, int gateMinutes = 30, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(allow ? "on" : "off", NativeMethods.NORMAL_PRIORITY_CLASS, "setcli",
            GateMinutes: gateMinutes), ct);

    /// <summary>
    /// Lists the launches currently holding a slot, with "N/M" slot usage. Like
    /// <see cref="SetCommandLineAllowedAsync"/> this is tray-only: the service requires
    /// the installed, elevated tray, so it returns empty for any other caller.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<JobInfo> Jobs, string Slots)> ListJobsAsync(CancellationToken ct = default)
    {
        var jobs  = new List<JobInfo>();
        string slots = string.Empty;
        bool ok = await SendAsync(new LaunchRequest(string.Empty, NativeMethods.NORMAL_PRIORITY_CLASS, "jobs"), ct,
            onMessage: msg =>
            {
                switch (msg.Type)
                {
                    case "job":
                        var job = JsonSerializer.Deserialize(msg.Content, PipeJsonContext.Default.JobInfo);
                        if (job is not null) jobs.Add(job);
                        break;
                    case "slots":
                        slots = msg.Content;
                        break;
                }
            });
        return (ok, jobs, slots);
    }

    /// <summary>Terminates the process behind an in-flight job (tray-only, like the listing).</summary>
    public Task<bool> KillJobAsync(int jobId, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(jobId.ToString(), NativeMethods.NORMAL_PRIORITY_CLASS, "killjob"), ct);

    /// <summary>
    /// The output a capture job has produced so far (a bounded tail kept by the service),
    /// so the tray can show what a job is doing rather than only what it was asked to run.
    /// </summary>
    public async Task<IReadOnlyList<string>> JobOutputAsync(int jobId, CancellationToken ct = default)
    {
        var lines = new List<string>();
        // logStdout: false — these lines are being collected for the caller to render.
        // Without it they would ALSO go out through LogMessage, and a CLI that prints
        // both the stream and the returned list shows every line twice.
        await SendAsync(new LaunchRequest(jobId.ToString(), NativeMethods.NORMAL_PRIORITY_CLASS, "joblog"), ct,
            onMessage: msg => { if (msg.Type == "stdout") lines.Add(msg.Content); },
            logStdout: false);
        return lines;
    }

    /// <summary>Launch from the command line (tagged Source="cli", gated by the service).</summary>
    public Task<bool> LaunchFromCliAsync(string commandLine, uint priority, string account, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority, "launch", "", 1, account, "cli"), ct);

    /// <summary>
    /// CLI launch with output capture: the child's stdout/stderr streams back through
    /// the pipe as "stdout" messages and appears in the caller's console (or the tray
    /// log area). The call blocks until the child exits or <paramref name="timeoutSeconds"/>
    /// elapses (0 = wait forever).
    /// </summary>
    public Task<bool> LaunchFromCliAsync(string commandLine, uint priority, string account,
        bool captureOutput, int timeoutSeconds = 0, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority, "launch", "", 1, account, "cli",
            CaptureOutput: captureOutput, TimeoutSeconds: timeoutSeconds), ct);

    // Extensions the service already knows how to host, plus the directly-runnable ones.
    // Anything else with an extension is a document and needs its handler resolved here.
    private static readonly string[] ServiceHandledExtensions =
        [".exe", ".com", ".msc", ".cpl", ".bat", ".cmd", ".ps1", ".reg"];

    /// <summary>
    /// Rewrites a document target into an explicit "handler + file" command line.
    /// </summary>
    /// <remarks>
    /// Done client-side on purpose: file associations are per-user, and the service runs
    /// as SYSTEM, where the user's default-app choice does not exist. Resolving in the
    /// service picks the OpenWith.exe chooser instead of the real handler, so the launch
    /// looks like it did nothing. Left untouched when the type has no handler, so the
    /// service still reports a proper failure.
    /// </remarks>
    private static string ResolveDocumentTarget(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return commandLine;

        // Only the bare target is a document candidate; anything with arguments already
        // names a program to run.
        string trimmed = commandLine.Trim();
        string path    = trimmed.StartsWith('"') && trimmed.IndexOf('"', 1) > 0
            ? trimmed[1..trimmed.IndexOf('"', 1)]
            : trimmed;
        if (path.Length != trimmed.Length && !trimmed.EndsWith('"')) return commandLine; // has args
        if (path.Contains(' ') && path == trimmed) return commandLine;                   // unquoted args

        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length == 0 ||
            Array.Exists(ServiceHandledExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return commandLine;

        return NativeMethods.ResolveDocumentCommand(path) ?? commandLine;
    }

    private async Task<bool> SendAsync(LaunchRequest request, CancellationToken ct,
        Action<PipeMessage>? onMessage = null, bool logStdout = true)
    {
        // Single choke point: every launch, from the tray or the CLI, passes through here.
        if (request.Verb == "launch")
        {
            string resolved = ResolveDocumentTarget(request.CommandLine);
            if (!ReferenceEquals(resolved, request.CommandLine))
                request = request with { CommandLine = resolved };
        }

        using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(5_000, ct);
        }
        catch (TimeoutException)
        {
            Log("Could not connect to RunAsHelper service — connection timed out.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log($"Could not connect to RunAsHelper service: {ex.Message}");
            return false;
        }

        try
        {
            await PipeProtocol.WriteAsync(pipe, request, ct);

            while (true)
            {
                PipeMessage? msg = await PipeProtocol.ReadPipeMessageAsync(pipe, ct);
                if (msg is null) break;

                // Verb-specific messages (e.g. "job"/"slots" from the jobs listing).
                onMessage?.Invoke(msg);

                switch (msg.Type)
                {
                    case "log":
                        Log(msg.Content);
                        break;
                    case "stdout":
                        // Child process output — route through the same LogMessage event so
                        // it appears in the tray log area and the CLI caller's console.
                        // Suppressed when the caller is collecting these lines itself.
                        if (logStdout) Log(msg.Content);
                        break;
                    case "pid":
                        // Grant the launched process the right to bring itself to
                        // the foreground. Must arrive before "result" so the process
                        // has the activation right while it is still starting up.
                        if (uint.TryParse(msg.Content, out uint fgPid) && fgPid != 0)
                            NativeMethods.AllowSetForegroundWindow(fgPid);
                        break;
                    case "result":
                        return msg.Content == "Success";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log($"Pipe communication error: {ex.Message}");
        }

        return false;
    }
}
