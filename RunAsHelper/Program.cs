using System;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using RunAsHelper.Core;

namespace RunAsHelper
{
    internal static class Program
    {
        // Unique name — prevents collisions with other apps on the system.
        private const string MutexName = AppInstance.MutexName;

        [STAThread]
        static void Main(string[] args)
        {
            // Last-resort exception logging for every entry path (GUI, CLI, tray,
            // validation). Without this a background-thread throw kills the process
            // with a bare "0xe0434352" dialog and no diagnostics; now the stack is
            // recorded to %AppData%\RunAsHelper\crash.log first.
            CrashLogger.Install();

            // WinForms' own initialisation has to come first, before *anything* that can
            // create a window: SetCompatibleTextRenderingDefault (inside Initialize) throws
            // once any IWin32Window exists. ApplyStartupColorMode below is one such thing —
            // Application.SetColorMode creates a window handle when another instance of the
            // app is already running — which crashed the elevated "Activate" hand-off every
            // time, since that instance starts while its predecessor is still alive.
            // Initialize() itself creates no window, so the color mode set straight after it
            // still lands before the first form. (Deliberately unconditional: the CLI paths
            // below only set flags, they never touch a form.)
            ApplicationConfiguration.Initialize();

            // Turn on WinForms' own dark mode before any window exists. This is what makes
            // the *native* chrome follow the theme — scrollbars, combo drop-down buttons,
            // menu dropdowns, disabled text — none of which can be reached by setting
            // BackColor/ForeColor from managed code. Must run before the first window is
            // created, so it reads the setting directly rather than waiting for a form.
            ApplyStartupColorMode();

            // Post-install / on-demand validation: open the validation dialog
            // standalone (used by the "Restart as administrator" recovery path,
            // which relaunches this exe elevated).
            if (args.Length == 1 &&
                (args[0].Equals("--revalidate", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("/validate", StringComparison.OrdinalIgnoreCase)))
            {
                Application.Run(new ValidationForm(standalone: true));
                return;
            }

            // Help: -h / --help / -help / /? / /h / help
            if (args.Length >= 1 && IsHelpFlag(args[0]))
            {
                ShowConsole();
                Console.WriteLine(HelpText.Cli);
                return;
            }

            // Active-job diagnostics: list what is holding a service launch slot, or
            // terminate a stuck job. The service applies the same tray-only gate it uses
            // for the CLI toggle, so this works from the installed exe when elevated.
            if (args.Length >= 1 &&
                (args[0].Equals("/jobs", StringComparison.OrdinalIgnoreCase) ||
                 args[0].StartsWith("/kill:", StringComparison.OrdinalIgnoreCase) ||
                 args[0].StartsWith("/joblog:", StringComparison.OrdinalIgnoreCase)))
            {
                ShowConsole();
                RunJobsCommand(args[0]);
                return;
            }

            // Trusted command-line user management (list / add / remove). The service
            // limits these verbs to the installed elevated RunAsHelper, the same gate as
            // /jobs, so they run from an elevated shell without the tray UI.
            if (args.Length >= 1 &&
                (args[0].Equals("/trusted", StringComparison.OrdinalIgnoreCase) ||
                 args[0].StartsWith("/trusted:", StringComparison.OrdinalIgnoreCase)))
            {
                ShowConsole();
                RunTrustedCommand(args);
                return;
            }

            // Elevation hand-off: the non-elevated tray's "Activate" button
            // relaunches the exe elevated with this flag. It is NOT a CLI launch —
            // it opens the tray window, but waits briefly for the predecessor
            // (the non-elevated instance) to release the single-instance mutex.
            bool activateHandoff = args.Length == 1 &&
                args[0].Equals("--activate", StringComparison.OrdinalIgnoreCase);

            // Login auto-start: open to the tray only (no window). Not a CLI launch.
            bool startTray = args.Length == 1 &&
                args[0].Equals("--tray", StringComparison.OrdinalIgnoreCase);

            // Installer hand-off: the MSI's "Launch RunAS Helper now" action starts us
            // with this flag once, right after installing. It is the ONLY thing that
            // triggers the post-install validation popup — an ordinary launch, the
            // --activate hand-off and the login --tray start never do. Not a CLI launch.
            bool postInstall = args.Length == 1 &&
                args[0].Equals("--postinstall", StringComparison.OrdinalIgnoreCase);

            // CLI mode: RunAsHelper.exe [/p:N] [/as:account] <path> [args]
            if (args.Length > 0 && !activateHandoff && !startTray && !postInstall)
            {
                RunCli(args);
                return;
            }

            // Single-instance guard — second launch exits silently; user sees the
            // existing tray icon and can click it to show the window.
            using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                if (!activateHandoff) return;
                // The non-elevated predecessor is exiting; wait for it to drop the
                // mutex so this elevated instance can take over cleanly.
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return;
                }
                catch (AbandonedMutexException)
                {
                    // Predecessor exited without releasing — we now own it.
                }
            }

            Application.Run(new MainForm(startHidden: startTray, postInstall: postInstall));
        }

        // Maps the saved Theme preference onto WinForms' color mode. Best-effort: theming
        // must never be the reason the app fails to start.
        private static void ApplyStartupColorMode()
        {
            try
            {
                var settings = Settings.AppSettings.Load();
                Theme.Mode = (ThemeMode)Math.Clamp(settings.Theme, 0, 2);
#pragma warning disable WFO5001 // SetColorMode is still marked experimental in .NET 10
                Application.SetColorMode(Theme.Mode switch
                {
                    ThemeMode.Light => SystemColorMode.Classic,
                    ThemeMode.Dark  => SystemColorMode.Dark,
                    _               => SystemColorMode.System,
                });
#pragma warning restore WFO5001
            }
            catch { /* fall back to the default light look */ }
        }

        private static bool IsHelpFlag(string a) =>
            a.Equals("-h", StringComparison.OrdinalIgnoreCase)     ||
            a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-help", StringComparison.OrdinalIgnoreCase)  ||
            a.Equals("/?", StringComparison.OrdinalIgnoreCase)     ||
            a.Equals("/h", StringComparison.OrdinalIgnoreCase)     ||
            a.Equals("help", StringComparison.OrdinalIgnoreCase);

        private static void RunCli(string[] args)
        {
            ShowConsole();

            uint   priority      = NativeMethods.NORMAL_PRIORITY_CLASS;
            string account       = "ti";
            bool   captureOutput = false;
            int    timeoutSecs   = 0;
            PowerShellEdition psHost = PowerShellEdition.Unspecified;

            // Consume leading /p:N, /as:ACCOUNT, /capture, /timeout:N, /ps:5|7 flags in any order.
            // A token that starts like one of the valued switches but does not parse is a
            // usage error (exit 1), never the launch target: "/timeout:abc cmd" used to be sent
            // to the service as a launch of "/timeout:abc", which failed with a misleading log.
            int i = 0;
            for (; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("/p:", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Length != 4 || !TryPriorityFromCode(a[3], out priority))
                        ExitWithUsage($"Bad priority '{a}'. Use /p:1 to /p:6 (see --help).");
                }
                else if (a.StartsWith("/as:", StringComparison.OrdinalIgnoreCase))
                    account = a[4..].Equals("system", StringComparison.OrdinalIgnoreCase) ? "system" : "ti";
                else if (a.Equals("/capture", StringComparison.OrdinalIgnoreCase))
                    captureOutput = true;
                else if (a.StartsWith("/timeout:", StringComparison.OrdinalIgnoreCase))
                {
                    // 0 is the protocol's "no ceiling" and is accepted as such.
                    if (!int.TryParse(a[9..], out int ts) || ts < 0)
                        ExitWithUsage($"Bad timeout '{a}'. Use /timeout:N with N in whole seconds (0 = no ceiling).");
                    timeoutSecs = ts;
                }
                else if (a.StartsWith("/ps:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!PowerShellSwitch.TryParse(a[4..], out psHost))
                        ExitWithUsage($"Unknown PowerShell host '{a[4..]}'. Use /ps:5 (Windows PowerShell 5.1) or /ps:7 (pwsh).");
                }
                else
                    break;
            }

            // The service enforces the ceiling only while it is pumping captured output, so
            // a fire-and-forget launch cannot time out. Say so once instead of pretending.
            if (timeoutSecs > 0 && !captureOutput)
                Console.WriteLine("/timeout applies with /capture; ignored for this launch.");

            // Re-quote tokens containing spaces so paths survive argv splitting.
            string commandLine = string.Join(" ", args[i..].Select(a =>
                a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a));

            if (string.IsNullOrWhiteSpace(commandLine))
                ExitWithUsage(null);

            var client = new PipeClient();
            client.LogMessage += msg => Console.WriteLine(msg);
            // CallerShell.Detect is a method group, invoked only for a .ps1 target when
            // neither /ps: nor #Requires decided the host.
            CliLaunchResult result = client
                .LaunchFromCliAsync(commandLine, priority, account, captureOutput, timeoutSecs, psHost, CallerShell.Detect)
                .GetAwaiter().GetResult();
            Environment.Exit(result.ToProcessExitCode(captureOutput));
        }

        // /jobs        — list the launches currently holding a slot
        // /kill:<id>   — terminate the process behind one of them
        private static void RunJobsCommand(string arg)
        {
            var client = new PipeClient();
            client.LogMessage += msg => Console.WriteLine(msg);

            if (arg.StartsWith("/joblog:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(arg[8..], out int logId))
                {
                    Console.Error.WriteLine("Usage: RunAsHelper /joblog:<job id>   (see /jobs)");
                    Environment.Exit(1);
                    return;
                }
                var (logOk, lines) = client.JobOutputAsync(logId).GetAwaiter().GetResult();
                foreach (string line in lines) Console.WriteLine(line);
                if (!logOk)
                {
                    Console.Error.WriteLine(
                        $"Job {logId} is not available: it has finished, never existed, or this caller is not the installed RunAsHelper running elevated.");
                    Environment.Exit(1);
                    return;
                }
                if (lines.Count == 0) Console.WriteLine("(no output captured yet)");
                Environment.Exit(0);
                return;
            }

            if (arg.StartsWith("/kill:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(arg[6..], out int id))
                {
                    Console.Error.WriteLine("Usage: RunAsHelper /kill:<job id>   (see /jobs)");
                    Environment.Exit(1);
                    return;
                }
                bool killed = client.KillJobAsync(id).GetAwaiter().GetResult();
                Console.WriteLine(killed ? $"Job {id} terminated." : $"Could not terminate job {id}.");
                Environment.Exit(killed ? 0 : 1);
                return;
            }

            var (ok, jobs, slots) = client.ListJobsAsync().GetAwaiter().GetResult();
            if (!ok)
            {
                Console.Error.WriteLine(
                    "Could not read active jobs. This needs the installed RunAsHelper running elevated.");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine($"Slots in use: {slots}");
            if (jobs.Count == 0)
            {
                Console.WriteLine("No active jobs.");
                Environment.Exit(0);
                return;
            }

            Console.WriteLine($"{"JOB",-5} {"ELAPSED",-9} {"ACCOUNT",-16} {"SRC",-5} {"PID",-7} COMMAND");
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var job in jobs)
            {
                var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - job.StartedUnixMs));
                string account = job.Account == "system" ? "SYSTEM" : "TrustedInstaller";
                string pid     = job.Pid == 0 ? "-" : job.Pid.ToString();
                Console.WriteLine(
                    $"{job.Id,-5} {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}      " +
                    $"{account,-16} {job.Source,-5} {pid,-7} {job.CommandLine}");
            }
            Environment.Exit(0);
        }

        // /trusted              — list the trusted command-line user SIDs and accounts
        // /trusted:add <acct>    — trust a user (SID or DOMAIN\user; groups are refused)
        // /trusted:remove <acct> — stop trusting a user (a SID need not still resolve)
        private static void RunTrustedCommand(string[] args)
        {
            var client = new PipeClient();
            client.LogMessage += msg => Console.WriteLine(msg);

            string verb = args[0];

            if (verb.Equals("/trusted", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, sids) = client.ListTrustedCallersAsync().GetAwaiter().GetResult();
                if (!ok)
                {
                    Console.Error.WriteLine(
                        "Could not read trusted command-line users. This needs the installed RunAsHelper run from an elevated shell (or the elevated tray).");
                    Environment.Exit(1);
                    return;
                }
                if (sids.Count == 0)
                {
                    Console.WriteLine("No trusted command-line users.");
                    Environment.Exit(0);
                    return;
                }
                // One SID<TAB>account per line, no header, so an agent can parse it.
                foreach (string sid in sids)
                {
                    string display = WindowsAccountResolver.TryResolveSid(sid, out var account, out string error)
                        ? account!.AccountName
                        : $"(unresolved: {error})";
                    Console.WriteLine($"{sid}\t{display}");
                }
                Environment.Exit(0);
                return;
            }

            if (verb.Equals("/trusted:add", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: RunAsHelper /trusted:add <SID | DOMAIN\\user>");
                    Environment.Exit(1);
                    return;
                }
                if (!TryCanonicalSid(args[1], out string sid, out string display, out string err))
                {
                    Console.Error.WriteLine(err);
                    Environment.Exit(1);
                    return;
                }
                // The service still refuses a group principal and reports it as a log line,
                // which the LogMessage hook prints; the SID that resolved may already be present.
                bool ok = client.AddTrustedCallerAsync(sid).GetAwaiter().GetResult();
                if (ok)
                {
                    Console.WriteLine($"Trusted: {display} ({sid})");
                    Environment.Exit(0);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"Could not add {display}. This needs the installed RunAsHelper run from an elevated shell, and the account must be a user.");
                    Environment.Exit(1);
                }
                return;
            }

            if (verb.Equals("/trusted:remove", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: RunAsHelper /trusted:remove <SID | DOMAIN\\user>");
                    Environment.Exit(1);
                    return;
                }
                if (!TryCanonicalSid(args[1], out string sid, out string display, out string err))
                {
                    Console.Error.WriteLine(err);
                    Environment.Exit(1);
                    return;
                }
                bool ok = client.RemoveTrustedCallerAsync(sid).GetAwaiter().GetResult();
                if (ok)
                {
                    Console.WriteLine($"Removed: {sid}");
                    Environment.Exit(0);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"Could not remove {display}. This needs the installed RunAsHelper run from an elevated shell.");
                    Environment.Exit(1);
                }
                return;
            }

            Console.Error.WriteLine(
                "Usage: RunAsHelper /trusted | /trusted:add <SID|DOMAIN\\user> | /trusted:remove <SID|DOMAIN\\user>");
            Environment.Exit(1);
        }

        // Turns a SID string or an account name into a canonical SID. A SID is accepted on
        // valid syntax alone (the service validates it is a user, and a deleted account must
        // stay removable); a name must resolve through LSA to a user SID.
        private static bool TryCanonicalSid(string input, out string sid, out string display, out string error)
        {
            sid = string.Empty;
            display = input;
            error = string.Empty;
            input = input.Trim();

            if (input.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    sid = new SecurityIdentifier(input).Value;
                }
                catch (ArgumentException)
                {
                    error = $"\"{input}\" is not a valid Windows SID.";
                    return false;
                }
                display = WindowsAccountResolver.TryResolveSid(sid, out var account, out _)
                    ? account!.AccountName
                    : sid;
                return true;
            }

            if (WindowsAccountResolver.TryResolveUser(input, out var resolved, out error))
            {
                sid = resolved!.Sid;
                display = resolved.AccountName;
                return true;
            }
            return false;
        }

        // Bad switch: one line naming the problem (when given), the usage line, exit 1.
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void ExitWithUsage(string? problem)
        {
            if (problem is not null) Console.Error.WriteLine(problem);
            Console.Error.WriteLine("Usage: RunAsHelper [/capture] [/timeout:N] [/ps:5|7] [/p:N] [/as:system|ti] <path> [args]");
            Console.Error.WriteLine("Run  RunAsHelper --help  for details.");
            Environment.Exit(1);
        }

        private static bool TryPriorityFromCode(char code, out uint priority)
        {
            priority = NativeMethods.NORMAL_PRIORITY_CLASS;
            switch (code)
            {
                case '1': priority = NativeMethods.NORMAL_PRIORITY_CLASS;       return true;
                case '2': priority = NativeMethods.IDLE_PRIORITY_CLASS;         return true;
                case '3': priority = NativeMethods.HIGH_PRIORITY_CLASS;         return true;
                case '4': priority = NativeMethods.REALTIME_PRIORITY_CLASS;     return true;
                case '5': priority = NativeMethods.BELOW_NORMAL_PRIORITY_CLASS; return true;
                case '6': priority = NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS; return true;
                default:  return false;
            }
        }

        // Attach to the parent console so CLI/help output is visible when run
        // from cmd/powershell (the app is a WinExe with no console of its own).
        // When stdout is already redirected (piped shell, VSCode extension), the
        // handles are already wired — skip AttachConsole which would replace them
        // with the parent's console (which may not exist in that context).
        // Makes CLI output land somewhere a caller can see it. This app is a WinExe, so
        // it has no console of its own and starts with no standard handles at all unless
        // the caller gave it some.
        //
        // The decision has to be made on the *handle*, not on Console.IsOutputRedirected.
        // A null stdout has file type FILE_TYPE_UNKNOWN, which that property reports as
        // "redirected" — so keying off it skipped the attach in exactly the case that
        // needs it (an interactive terminal), and every CLI message, --help included,
        // went into TextWriter.Null and the process exited 0 in silence. Piped and
        // file-redirected callers keep their own handle (FILE_TYPE_PIPE / _DISK) and are
        // left alone, which is what v1.6.3 set out to fix.
        //
        // Console.Out is bound lazily on first write, so it picks up the handle the
        // attach installs; no Console.SetOut is needed. Note the shell does not wait for
        // a WinExe, so in an interactive terminal the text can arrive after the next
        // prompt is drawn — 'start /wait' or piping fixes the ordering, caller-side.
        private static void ShowConsole()
        {
            if (NativeMethods.HasUsableStdOut()) return;
            try { NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS); } catch { }
        }
    }
}
