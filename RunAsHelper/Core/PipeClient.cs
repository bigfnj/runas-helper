using System;
using System.IO;
using System.IO.Pipes;
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

    /// <summary>Tells the service whether to allow CLI-sourced launches (tray only).</summary>
    public Task<bool> SetCommandLineAllowedAsync(bool allow, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(allow ? "on" : "off", NativeMethods.NORMAL_PRIORITY_CLASS, "setcli"), ct);

    /// <summary>Launch from the command line (tagged Source="cli", gated by the service).</summary>
    public Task<bool> LaunchFromCliAsync(string commandLine, uint priority, string account, CancellationToken ct = default)
        => SendAsync(new LaunchRequest(commandLine, priority, "launch", "", 1, account, "cli"), ct);

    private async Task<bool> SendAsync(LaunchRequest request, CancellationToken ct)
    {
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

                switch (msg.Type)
                {
                    case "log":
                        Log(msg.Content);
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
