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

  Non-executable targets are launched via their host automatically:
    .msc -> mmc.exe    .cpl -> control.exe    .bat/.cmd -> cmd /c    .ps1 -> powershell

  A bare name (e.g. notepad.exe, lusrmgr.msc) is resolved on the PATH. The CLI
  streams the service log to stdout and exits 0 on success, 1 on failure. With
  /capture it also streams the child's output, blocking until exit or timeout.
  Requires the RunASHelper service running and an elevated context.

  SECURITY: the command line is DISABLED by default. Enable it per session in
  RunAS Helper > Settings > ""Allow command line"" (the tray must be running and
  elevated; it resets to OFF on every tray launch and on exit).

EXAMPLES
  RunAsHelper.exe cmd.exe
  RunAsHelper.exe /p:3 regedit.exe
  RunAsHelper.exe /as:system cmd.exe
  RunAsHelper.exe /as:ti lusrmgr.msc
  RunAsHelper.exe ""C:\Program Files\Tool\tool.exe"" --flag
  RunAsHelper.exe /capture /as:system powershell.exe -NoProfile -Command ""Get-Service Wuauserv""
  RunAsHelper.exe /capture /timeout:30 /as:system powershell.exe -NoProfile -File C:\scripts\fix.ps1

TRAY APP
  Quick run (one-off):  pick a priority, type or Browse... to a path, then click
                        ""Run as TrustedInstaller"" or ""Run as SYSTEM"".
  Saved applications:   Add Application stores name, location, parameters,
                        working directory, window state, account and priority.
                        Double-click or Run to launch; Edit / Remove / Up / Down
                        to manage. Del = remove, F2 = edit, Enter = run.
  Tools menu:           Settings, Validate Installation, Open PowerShell
                        (TrustedInstaller), Import/Export saved apps, How to Use.
  Not elevated?         Click the Activate bar to relaunch elevated (Avecto/UAC);
                        it disappears once elevated.
";
}
