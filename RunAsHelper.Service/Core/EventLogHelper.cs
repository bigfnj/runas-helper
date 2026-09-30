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

    // 1001 - a launch request was received on the pipe (validate requests are not logged)
    internal static void RequestReceived(string commandLine, uint clientPid, string sourceKind) =>
        Write(EventLogEntryType.Information, 1001,
            $"Launch requested: '{commandLine}'\nSource: {sourceKind}  ClientPID: {clientPid}");

    // 1002 - process was created successfully
    internal static void Launched(string commandLine, uint pid) =>
        Write(EventLogEntryType.Information, 1002,
            $"Launch succeeded: '{commandLine}'  PID: {pid}");

    // 1003 - request was blocked (gate closed or expired, tray-only verb from another
    // client, unknown verb, or launch failure)
    internal static void Denied(string commandLine, string reason) =>
        Write(EventLogEntryType.Warning, 1003,
            $"Launch denied: '{commandLine}'\nReason: {reason}");

    // 1003 - identity mismatch (request continues without a SID). The SID read from the
    // pipe token and the SID read from the client's process token differed, so the caller's
    // SID is treated as unknown: it cannot match the trusted-caller policy and the request
    // is authorized on the installed-tray identity or the open CLI gate alone. Same id as a
    // denial so existing 1003 monitoring still sees it; the text says it was not a denial.
    internal static void IdentityMismatch(uint clientPid, string pipeSid, string processSid) =>
        Write(EventLogEntryType.Warning, 1003,
            $"Client identity mismatch: PID {clientPid} presented pipe token SID {pipeSid} but process token SID {processSid}.\n" +
            "The caller's SID is treated as unknown and the request continues; it is authorized on the installed-tray identity or the open command-line gate only.");

    // 1006 - an operator terminated an in-flight job from the tray's Active Jobs view.
    // Distinct from 1003: this is a deliberate administrative action on an elevated
    // process, not a blocked request, and a SIEM should be able to tell them apart.
    internal static void JobTerminated(int jobId, uint pid, string commandLine, bool succeeded) =>
        Write(EventLogEntryType.Warning, 1006,
            $"Job {jobId} (PID {pid}) {(succeeded ? "terminated" : "could not be terminated")} by operator: '{commandLine}'");

    // 1004 - TrustedInstaller token could not be acquired at startup or on demand
    internal static void TokenFailed(string reason) =>
        Write(EventLogEntryType.Error, 1004,
            $"Token acquisition failed: {reason}");

    // 1005 - service lifecycle events
    internal static void ServiceStarted() =>
        Write(EventLogEntryType.Information, 1005, "RunAsHelper service started.");

    internal static void ServiceStopped() =>
        Write(EventLogEntryType.Information, 1005, "RunAsHelper service stopped.");
}
