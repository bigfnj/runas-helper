using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RunAsHelper.Core;

/// <summary>
/// Process-wide last-resort exception logging.
///
/// Before this, the app wired up no global exception handling at all, so any
/// exception raised off the WinForms UI message pump — a background thread (e.g.
/// the <c>EventLogWatcher</c> callback), an unobserved <see cref="Task"/>, a
/// startup/shutdown path, or the CLI mode with no message loop — terminated the
/// process with a bare <c>0xe0434352</c> ("unknown software exception") Windows
/// hard-error dialog and left nothing on disk or in the event log to diagnose.
///
/// <see cref="Install"/> subscribes the three global exception sources and appends
/// the full stack to <c>%AppData%\RunAsHelper\crash.log</c> (always writable) and,
/// best-effort, to the Application event log. A truly unhandled background
/// exception still ends the process — the CLR cannot be talked out of that — but
/// now it records <em>why</em> before it goes.
/// </summary>
internal static class CrashLogger
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunAsHelper");
    private static readonly string LogPath = Path.Combine(LogDir, "crash.log");

    // Matches the event source the installer registers for the service; a client
    // event ID distinct from the service's 1001–1005 range.
    private const string EventSource  = "RunAsHelper";
    private const int    ClientEventId = 1099;

    /// <summary>Wire the global handlers. Call once, first thing in <c>Main</c>.</summary>
    public static void Install()
    {
        // Background threads (incl. the EventLogWatcher callback) and any other
        // path with no UI-thread handler. Process is torn down after this fires;
        // logging first is the whole point.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        // WinForms UI-thread exceptions during message processing. Registering a
        // handler suppresses the default ThreadExceptionDialog; we log and let the
        // app keep running rather than crash on a recoverable UI hiccup.
        Application.ThreadException += (_, e) =>
            Log("Application.ThreadException", e.Exception, terminating: false);

        // Faulted Tasks whose exception was never observed (e.g. a fire-and-forget
        // launch). Marking observed keeps it from escalating; we still record it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("TaskScheduler.UnobservedTaskException", e.Exception, terminating: false);
            e.SetObserved();
        };
    }

    /// <summary>Append one exception record to the crash log (and event log, best-effort).</summary>
    public static void Log(string origin, Exception? ex, bool terminating)
    {
        string entry =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {origin} " +
            $"(terminating={terminating}, managedThread={Environment.CurrentManagedThreadId}, " +
            $"version={VersionString()})" + Environment.NewLine +
            (ex?.ToString() ?? "(no Exception object)") + Environment.NewLine + Environment.NewLine;

        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath, entry);
        }
        catch { /* logging must never throw */ }

        try
        {
            // Writing to an already-registered source needs no elevation. On an
            // unpackaged dev run where the source isn't registered, WriteEntry
            // throws; the file above stays the reliable record.
            EventLog.WriteEntry(EventSource, entry, EventLogEntryType.Error, ClientEventId);
        }
        catch { /* event-log write is best-effort */ }
    }

    private static string VersionString()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        }
        catch { return "?"; }
    }
}
