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
    string Verb             = "launch",   // launch/validation, tray controls, or trusted-caller policy verbs
    string WorkingDirectory = "",
    int    ShowWindow       = 1,
    string Account          = "ti",       // "ti" (TrustedInstaller) | "system"
    string Source           = "tray",     // "tray" | "cli" — gated by the service
    bool   CaptureOutput    = false,      // stream child stdout/stderr back through the pipe
    int    TimeoutSeconds   = 0,          // 0 = wait forever; > 0 = hard ceiling before closing the output stream
    int    GateMinutes      = 30);        // "setcli on": minutes before the CLI gate auto-closes (0 = no expiry)

/// <summary>
/// Sent by the service back to the client: either a streaming line or the final result.
/// <c>Type</c> is one of: <c>log</c> (a service log line), <c>stdout</c> (one captured
/// child output line), <c>pid</c> (the launched process id), <c>result</c> (Success or
/// Failed, always last), <c>gate</c> (setcli acknowledgement: the request's own
/// GateMinutes echoed back, 0 = no expiry; not an absolute deadline),
/// <c>trustedcaller</c> (one policy SID), <c>job</c> (a JobInfo as JSON), <c>slots</c>
/// (N/M slot usage), <c>exit</c> (the child's exit code as an unsigned int, capture only),
/// and <c>timeout</c> (the elapsed TimeoutSeconds, capture only). A client that does not
/// know a type ignores it, so <c>exit</c> and <c>timeout</c> are additive: a 2.2.0 client
/// drops them and behaves as before.
/// </summary>
public sealed record PipeMessage(string Type, string Content);

/// <summary>
/// One in-flight launch, reported by the <c>"jobs"</c> verb as a <c>"job"</c> message
/// whose Content is this record as JSON. Only launches that outlive their request show
/// up in practice: a fire-and-forget launch completes as soon as the process is created,
/// whereas a <c>/capture</c> launch holds its slot until the child exits or its
/// <c>/timeout</c> ceiling fires.
/// </summary>
public sealed record JobInfo(
    int    Id,
    string CommandLine,
    string Account,
    string Source,
    uint   Pid,
    long   StartedUnixMs,
    bool   CaptureOutput,
    int    TimeoutSeconds);

[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(JobInfo))]
public sealed partial class PipeJsonContext : JsonSerializerContext { }
