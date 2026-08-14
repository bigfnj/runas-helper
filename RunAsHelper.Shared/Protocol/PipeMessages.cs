using System.Text.Json.Serialization;

namespace RunAsHelper.Shared.Protocol;

/// <summary>
/// Sent by the tray client to the service to request a process launch.
/// New fields default to current behavior so older peers stay compatible:
/// <c>WorkingDirectory</c> empty = inherit; <c>ShowWindow</c> is a Win32 SW_*
/// value (1 = SW_SHOWNORMAL).
/// </summary>
public sealed record LaunchRequest(
    string CommandLine,
    uint   Priority,
    string Verb             = "launch",   // "launch" | "validate" | "validate-system" | "setcli"
    string WorkingDirectory = "",
    int    ShowWindow       = 1,
    string Account          = "ti",       // "ti" (TrustedInstaller) | "system"
    string Source           = "tray",     // "tray" | "cli" — gated by the service
    bool   CaptureOutput    = false,      // stream child stdout/stderr back through the pipe
    int    TimeoutSeconds   = 0);         // 0 = wait forever; > 0 = hard ceiling before closing the output stream

/// <summary>Sent by the service back to the client: either a streaming log line or the final result.</summary>
public sealed record PipeMessage(string Type, string Content);

[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(PipeMessage))]
public sealed partial class PipeJsonContext : JsonSerializerContext { }
