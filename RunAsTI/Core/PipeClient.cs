using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RunAsTI.Shared.Protocol;

namespace RunAsTI.Core;

/// <summary>
/// Sends launch requests to the RunAsTI Windows service over a named pipe.
/// Thread-safe; multiple concurrent calls are allowed (each opens its own pipe).
/// </summary>
internal sealed class PipeClient
{
    private const string PipeName = "RunAsHelper";

    public event Action<string>? LogMessage;

    private void Log(string msg) => LogMessage?.Invoke(msg);

    /// <summary>
    /// Ask the RunAsTI service to launch <paramref name="commandLine"/> as TrustedInstaller.
    /// Returns true on success, false if the service is unreachable or the launch failed.
    /// </summary>
    public async Task<bool> LaunchAsTIAsync(
        string commandLine,
        uint priority,
        CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(5_000, ct);
        }
        catch (TimeoutException)
        {
            Log("Could not connect to RunAsTI service — connection timed out.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log($"Could not connect to RunAsTI service: {ex.Message}");
            return false;
        }

        try
        {
            await PipeProtocol.WriteAsync(pipe, new LaunchRequest(commandLine, priority), ct);

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
