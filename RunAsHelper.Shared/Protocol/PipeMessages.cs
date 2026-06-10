using System.Text.Json.Serialization;

namespace RunAsHelper.Shared.Protocol;

/// <summary>Sent by the tray client to the service to request a process launch.</summary>
public sealed record LaunchRequest(string CommandLine, uint Priority, string Verb = "launch");

/// <summary>Sent by the service back to the client: either a streaming log line or the final result.</summary>
public sealed record PipeMessage(string Type, string Content);

[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(PipeMessage))]
public sealed partial class PipeJsonContext : JsonSerializerContext { }
