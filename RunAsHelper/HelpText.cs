namespace RunAsHelper;

/// <summary>
/// Single source of truth for the "How to Use" text, shown both in the Tools →
/// How to Use dialog and on the command line (-h / --help / /?).
/// </summary>
internal static class HelpText
{
    public const string Cli =
@"RunAS Helper - run programs as TrustedInstaller or SYSTEM
=========================================================

OVERVIEW
  Launches any program at TrustedInstaller or LocalSystem level. A background
  Windows service (RunASHelper, running as LocalSystem) performs the elevation;
  the tray app and CLI ask it over a named pipe whose ACL admits only
  BUILTIN\Administrators and SYSTEM. The caller must already be elevated.

ACCOUNTS  (who the launched program runs as)
  TrustedInstaller (default)
      A SYSTEM token carrying the NT SERVICE\TrustedInstaller group. Needed to
      modify TrustedInstaller-owned files, registry keys and services.
  SYSTEM
      A pure LocalSystem token (no TrustedInstaller group).
  Note: the TrustedInstaller service runs as LocalSystem, so in BOTH cases
  'whoami' reports 'nt authority\system'. The difference is the TrustedInstaller
  group membership, which is what grants access to TI-owned objects.

COMMAND LINE
  RunAsHelper.exe [/capture] [/timeout:N] [/p:N] [/as:ACCOUNT] <path> [arguments]

  /p:N         Priority class of the launched process:
                 1 Normal (default)   2 Idle          3 High
                 4 Realtime           5 Below Normal  6 Above Normal
  /as:ACCOUNT  Account to run as:
                 /as:ti       TrustedInstaller (default)
                 /as:system   LocalSystem
  /capture     Stream the child's stdout and stderr back so the CLI caller sees
               them directly. The call blocks until the child exits (or until the
               timeout). Skip for GUI apps (no stdout) and interactive shells.
  /timeout:N   Hard ceiling in seconds. Without it the CLI waits forever. On
               timeout the output stream closes but the child is left running.
  -h, --help, /?   Show this help.
  --revalidate     Re-run the post-install validation dialog.
  /jobs            List the launches currently holding a service launch slot,
                   with their job id, elapsed time, account, PID and command.
  /kill:<id>       Terminate the process behind one of those jobs.
  /joblog:<id>     Show the output an in-flight capture job has produced so far.

  /jobs, /kill and /joblog need the installed RunAsHelper.exe running elevated
  (the same check that guards the CLI toggle), so they are not available to an
  arbitrary process through an open CLI gate. The tray equivalent is the Active
  Jobs pane (click the status bar's Jobs count, or Tools > Active Jobs). In practice the jobs listed are /capture launches: a fire-and-forget
  launch frees its slot as soon as the process starts.

  Non-executable targets are launched via their host automatically:
    .msc -> mmc.exe    .cpl -> control.exe    .bat/.cmd -> cmd /c    .ps1 -> powershell
    .reg -> regedit /s     any other document -> its registered handler

  A bare name (e.g. notepad.exe, lusrmgr.msc) is resolved on the PATH. The CLI
  streams the service log to stdout and exits 0 on success, 1 on failure. With
  /capture it also streams the child's output, blocking until exit or timeout.
  Requires the RunASHelper service running and an elevated context.

  SECURITY: the command line is DISABLED by default. Enable it per session in
  RunAS Helper > Settings > ""Allow command line"" (the tray must be running and
  elevated; it resets to OFF on every tray launch and on exit). The allowance also
  expires on its own after Settings > ""...auto-close it after"" minutes (default
  30, 0 = never); the service enforces that, and re-enabling restarts the clock.

SCRIPTING / AUTOMATION NOTES
  Two things surprise callers that drive this programmatically:

  1. RunAsHelper.exe is a GUI-subsystem binary. PowerShell's call operator (&)
     neither waits for it nor captures its output, so a script that does
     '$out = & RunAsHelper.exe /jobs' silently gets nothing. Redirect explicitly:
       $p = Start-Process RunAsHelper.exe -ArgumentList '/jobs' -PassThru -Wait -RedirectStandardOutput out.txt
     From cmd.exe, 'start /wait /b' behaves similarly. (Output is written to the
     parent console normally when run interactively.)

  2. An ELEVATED call made from the INSTALLED RunAsHelper.exe is treated as the
     tray, not as a foreign CLI caller, so it bypasses the ""Allow command line""
     gate entirely. Automation running elevated from C:\Program Files\RunAsHelper
     therefore needs no gate toggle; a copy of the exe anywhere else does.

  Exit codes: 0 = success, 1 = failure (service unreachable, gate closed or
  expired, launch denied, or no such job). The service's log lines are written to
  stdout, so a failed call explains itself there.

EXAMPLES
  RunAsHelper.exe cmd.exe
  RunAsHelper.exe /p:3 regedit.exe
  RunAsHelper.exe /as:system cmd.exe
  RunAsHelper.exe /as:ti lusrmgr.msc
  RunAsHelper.exe ""C:\Program Files\Tool\tool.exe"" --flag
  RunAsHelper.exe /capture /as:system powershell.exe -NoProfile -Command ""Get-Service Wuauserv""
  RunAsHelper.exe /capture /timeout:30 /as:system powershell.exe -NoProfile -File C:\scripts\fix.ps1
  RunAsHelper.exe C:\patches\fix.reg              :: imported silently via regedit /s
  RunAsHelper.exe C:\Windows\System32\drivers\etc\hosts   :: opens in your editor
  RunAsHelper.exe /jobs                           :: what is holding a launch slot
  RunAsHelper.exe /joblog:3                       :: what job 3 has printed so far
  RunAsHelper.exe /kill:3                         :: stop a stuck job

TRAY APP
  Quick run (one-off):  pick a priority, type or Browse... to a path, then click
                        ""Run as TrustedInstaller"" or ""Run as SYSTEM"".
  Saved applications:   Add Application stores name, location, parameters,
                        working directory, window state, account and priority.
                        Double-click or Run to launch; Edit / Remove / Up / Down
                        to manage. Del = remove, F2 = edit, Enter = run.
                        Rows show the target's own icon, can be dragged to
                        reorder, and the Filter box narrows a long list. (While
                        a filter is active, reordering is disabled -- a row's
                        position on screen is not its position in the saved
                        order.) Hover a row for its full path.
  Active Jobs:          A pane on the right of the window showing what is
                        currently holding a service launch slot, with slot usage,
                        the output each job has produced so far, and a Kill
                        button for one that is stuck. Click the status bar's
                        Jobs count (or Tools > Active Jobs) to expand it, and
                        again to collapse it; the window grows to the right
                        rather than squeezing the saved-apps list, and hands that
                        width back when the pane closes. Drag the divider to
                        resize it (the width is remembered). It starts collapsed
                        on every launch. Needs an elevated tray to list anything.
  Status bar:           Bottom of the window -- service state, whether the CLI
                        gate is open (and how long it has left), and how many
                        launch slots are in use. The gate and jobs labels are
                        clickable: CLI: off opens the gate, and the jobs count
                        shows or hides the Active Jobs pane.
  Tools menu:           Settings, Validate Installation, Active Jobs, Open
                        PowerShell (TrustedInstaller), Import/Export saved apps,
                        Clear Recent History, How to Use.
  Theme:                Settings > Theme -- Follow system (default), Light or
                        Dark. Following the system repaints live when Windows
                        switches between light and dark.
  Not elevated?         Click the Activate bar to relaunch elevated (Avecto/UAC);
                        it disappears once elevated.
";
}
