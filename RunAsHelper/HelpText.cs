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
  the tray app and CLI ask it over a local named pipe. The service determines
  caller identity server-side from the kernel-reported connecting process token,
  cross-checking the authenticated pipe token when available. Tray-only controls
  also require the installed process path and elevation. Client-supplied names,
  SIDs and PIDs are never trusted. Network-logon tokens are rejected.
  Two launchers are installed: RunAsHelper.com for the command line, and the
  tray binary RunAsHelper.exe.

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
  RunAsHelper [/capture] [/timeout:N] [/ps:5|7] [/p:N] [/as:ACCOUNT] <path> [arguments]
  RunAsHelper /jobs | /kill:<id> | /joblog:<id>
  RunAsHelper /trusted | /trusted:add <SID|DOMAIN\user> | /trusted:remove <SID>

  /p:N         Priority class of the launched process:
                 1 Normal (default)   2 Idle          3 High
                 4 Realtime           5 Below Normal  6 Above Normal
  /as:ACCOUNT  Account to run as:
                 /as:ti       TrustedInstaller (default)
                 /as:system   LocalSystem
  /capture     Stream the child's stdout and stderr back and block until it
               exits (or the timeout). The CLI then exits with the CHILD's exit
               code. Skip for GUI apps (no stdout) and interactive shells.
  /timeout:N   Hard ceiling in seconds. On timeout the output stream closes, the
               child is left running (see /jobs) and the CLI exits 124.
  /ps:5|/ps:7  Which PowerShell hosts a .ps1 target: Windows PowerShell 5.1 or
               pwsh 7. Ignored when the script itself says so (#Requires, below)
               or when the target is not a .ps1.
  -h, --help, /?   Show this help.
  --revalidate     Re-run the post-install validation dialog.
  /jobs            List the launches currently holding a service launch slot,
                   with their job id, elapsed time, account, PID and command.
  /kill:<id>       Terminate the process behind one of those jobs.
  /joblog:<id>     Show the output an in-flight capture job has produced so far.
  /trusted             List the trusted command-line users (SID and account per line).
  /trusted:add X       Trust a user (SID or DOMAIN\user; groups are refused).
  /trusted:remove SID  Stop trusting a user.

  /jobs, /kill, /joblog and /trusted need the installed RunAsHelper running
  elevated: run them from an elevated shell (RunAsHelper.com runs at the shell's
  level) or use the tray (Active Jobs pane, Tools > Trusted command-line users).
  In practice the jobs listed are /capture launches: a fire-and-forget launch
  frees its slot as soon as the process starts.

  Non-executable targets are launched via their host automatically:
    .msc -> mmc.exe    .cpl -> control.exe    .bat/.cmd -> cmd /c
    .reg -> regedit /s    .ps1 -> PowerShell (see below)
    any other document -> its registered handler

  PowerShell host for a .ps1 target, first match wins:
    1. /ps:5 or /ps:7 on the command line.
    2. '#Requires -Version 6+' or '#Requires -PSEdition Core' in the script picks
       pwsh; '#Requires -PSEdition Desktop' picks Windows PowerShell 5.1.
    3. Command line only: the shell you typed the command in (pwsh picks pwsh,
       Windows PowerShell or ISE picks 5.1). cmd, Git Bash and the tray skip this.
    4. Windows PowerShell 5.1.
  If pwsh is wanted but not installed, 5.1 is used and the log says so. The CLI
  always prints one 'PowerShell host:' line naming the host and the reason.
  Naming a host yourself (a powershell.exe or pwsh.exe target with -File)
  bypasses the rule.

  A bare name (e.g. notepad.exe, lusrmgr.msc) is resolved on the PATH. The CLI
  streams the service log to stdout and exits 0 on success, 1 on failure. With
  /capture it also streams the child's output, blocking until exit or timeout.
  Requires the RunASHelper service and one authorization path: the installed
  RunAsHelper running elevated, the caller's exact user SID in Trusted
  command-line users, or the general command-line gate being open.

  SECURITY: the general command-line gate is DISABLED by default. An installed,
  elevated tray can either add selected accounts under Tools > Trusted
  command-line users, or enable the broad gate per session in Settings > ""Allow
  command line"". A trusted account works while that gate is closed. The broad
  allowance resets to OFF on every tray launch/exit and expires after Settings >
  ""...auto-close it after"" minutes (default 30, 0 = never). While it is open,
  every local process that can reach the pipe can request elevation.

SCRIPTING / AUTOMATION NOTES
  1. Two binaries, one command line. RunAsHelper.com is a console program: your
     shell waits for it, output streams, and $LASTEXITCODE / %ERRORLEVEL% are set.
     Because .COM precedes .EXE in PATHEXT and the install folder is on the PATH,
     the bare name 'RunAsHelper' runs the .com from cmd and PowerShell. Git Bash
     resolves bare names to .exe, so type 'RunAsHelper.com' there. The tray binary
     is RunAsHelper.exe, a GUI program a shell never waits for, so a call such as
     '& ""C:\Program Files\RunAsHelper\RunAsHelper.exe"" ...' returns before
     anything prints. Pipe it (| Out-String) or use Start-Process -Wait.
  2. Elevation. RunAsHelper.com runs at your shell's level. From an elevated shell
     the installed exe counts as the tray and needs no gate; from a normal shell
     you need your SID in /trusted (one-time, from an elevated shell) or the
     session gate.
  3. Exit codes: 0 success; 1 RunAsHelper failure (service unreachable, not
     trusted and gate closed, launch denied, no such job, bad switch); with
     /capture, the child's own exit code; 124 when /timeout fired. Service log
     lines go to stdout.
  4. Windows PowerShell 5.1 gotchas when it hosts your script: $PSScriptRoot is
     empty inside param() defaults; &&, ||, ?? and the ternary do not parse;
     Set-Content writes ANSI. Add '#Requires -Version 7' to opt into pwsh.

EXAMPLES
  RunAsHelper cmd.exe
  RunAsHelper /p:3 regedit.exe
  RunAsHelper /as:system cmd.exe
  RunAsHelper /as:ti lusrmgr.msc
  RunAsHelper ""C:\Program Files\Tool\tool.exe"" --flag
  RunAsHelper /capture /as:system powershell.exe -NoProfile -Command ""Get-Service Wuauserv""
  RunAsHelper /capture /as:system C:\scripts\fix.ps1        :: host picked by the rule above
  RunAsHelper /capture /ps:7 /as:system C:\scripts\fix.ps1  :: force pwsh
  RunAsHelper C:\patches\fix.reg              :: imported silently via regedit /s
  RunAsHelper C:\Windows\System32\drivers\etc\hosts   :: opens in your editor
  RunAsHelper /jobs                           :: what is holding a launch slot
  RunAsHelper /joblog:3                       :: what job 3 has printed so far
  RunAsHelper /kill:3                         :: stop a stuck job
  RunAsHelper /trusted                        :: who may call without the gate
  RunAsHelper /trusted:add elsewhere\admin    :: from an elevated shell

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
  Tools menu:           Settings, Trusted command-line users, Validate
                        Installation, Active Jobs, Open PowerShell
                        (TrustedInstaller), Import/Export saved apps, Clear
                        Recent History, How to Use.
  Theme:                Settings > Theme -- Follow system (default), Light or
                        Dark. Following the system repaints live when Windows
                        switches between light and dark.
  Not elevated?         Click the Activate bar to relaunch elevated (Avecto/UAC);
                        it disappears once elevated.
";
}
