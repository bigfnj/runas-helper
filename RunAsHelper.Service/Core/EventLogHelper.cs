using System.Diagnostics;

namespace RunAsHelper.Service.Core;

internal static class EventLogHelper
{
    private const string Source = "RunAsHelper";
    private const string Log    = "Application";

    private static void Write(EventLogEntryType type, int eventId, string message)
    {
        try
        {
            if (!EventLog.SourceExists(Source))
                EventLog.CreateEventSource(Source, Log);
            EventLog.WriteEntry(Source, message, type, eventId);
        }
        catch { }
    }

    // 1001 — a launch or validate request was received on the pipe
    internal static void RequestReceived(string commandLine, uint clientPid, string sourceKind) =>
        Write(EventLogEntryType.Information, 1001,
            $"Launch requested: '{commandLine}'\nSource: {sourceKind}  ClientPID: {clientPid}");

    // 1002 — process was created successfully
    internal static void Launched(string commandLine, uint pid) =>
        Write(EventLogEntryType.Information, 1002,
            $"Launch succeeded: '{commandLine}'  PID: {pid}");

    // 1003 — request was blocked (gate closed, identity mismatch, or launch failure)
    internal static void Denied(string commandLine, string reason) =>
        Write(EventLogEntryType.Warning, 1003,
            $"Launch denied: '{commandLine}'\nReason: {reason}");

    // 1004 — TrustedInstaller token could not be acquired at startup or on demand
    internal static void TokenFailed(string reason) =>
        Write(EventLogEntryType.Error, 1004,
            $"Token acquisition failed: {reason}");

    // 1005 — service lifecycle events
    internal static void ServiceStarted() =>
        Write(EventLogEntryType.Information, 1005, "RunAsHelper service started.");

    internal static void ServiceStopped() =>
        Write(EventLogEntryType.Information, 1005, "RunAsHelper service stopped.");
}
