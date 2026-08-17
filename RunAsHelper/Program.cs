using System;
using System.Linq;
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

            // Post-install / on-demand validation: open the validation dialog
            // standalone (used by the "Restart as administrator" recovery path,
            // which relaunches this exe elevated).
            if (args.Length == 1 &&
                (args[0].Equals("--revalidate", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("/validate", StringComparison.OrdinalIgnoreCase)))
            {
                ApplicationConfiguration.Initialize();
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

            // Elevation hand-off: the non-elevated tray's "Activate" button
            // relaunches the exe elevated with this flag. It is NOT a CLI launch —
            // it opens the tray window, but waits briefly for the predecessor
            // (the non-elevated instance) to release the single-instance mutex.
            bool activateHandoff = args.Length == 1 &&
                args[0].Equals("--activate", StringComparison.OrdinalIgnoreCase);

            // Login auto-start: open to the tray only (no window). Not a CLI launch.
            bool startTray = args.Length == 1 &&
                args[0].Equals("--tray", StringComparison.OrdinalIgnoreCase);

            // CLI mode: RunAsHelper.exe [/p:N] [/as:account] <path> [args]
            if (args.Length > 0 && !activateHandoff && !startTray)
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

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startHidden: startTray));
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

            // Consume leading /p:N, /as:ACCOUNT, /capture, /timeout:N flags in any order.
            int i = 0;
            for (; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) && a.Length >= 4)
                    priority = PriorityFromCode(a[3]);
                else if (a.StartsWith("/as:", StringComparison.OrdinalIgnoreCase))
                    account = a[4..].Equals("system", StringComparison.OrdinalIgnoreCase) ? "system" : "ti";
                else if (a.Equals("/capture", StringComparison.OrdinalIgnoreCase))
                    captureOutput = true;
                else if (a.StartsWith("/timeout:", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(a[9..], out int ts) && ts > 0)
                    timeoutSecs = ts;
                else
                    break;
            }

            // Re-quote tokens containing spaces so paths survive argv splitting.
            string commandLine = string.Join(" ", args[i..].Select(a =>
                a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a));

            if (string.IsNullOrWhiteSpace(commandLine))
            {
                Console.Error.WriteLine("Usage: RunAsHelper.exe [/capture] [/timeout:N] [/p:N] [/as:system|ti] <path> [args]");
                Console.Error.WriteLine("Run  RunAsHelper.exe --help  for details.");
                Environment.Exit(1);
                return;
            }

            var client = new PipeClient();
            client.LogMessage += msg => Console.WriteLine(msg);
            bool ok = client.LaunchFromCliAsync(commandLine, priority, account, captureOutput, timeoutSecs)
                            .GetAwaiter().GetResult();
            Environment.Exit(ok ? 0 : 1);
        }

        private static uint PriorityFromCode(char code) => code switch
        {
            '1' => NativeMethods.NORMAL_PRIORITY_CLASS,
            '2' => NativeMethods.IDLE_PRIORITY_CLASS,
            '3' => NativeMethods.HIGH_PRIORITY_CLASS,
            '4' => NativeMethods.REALTIME_PRIORITY_CLASS,
            '5' => NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
            '6' => NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS,
            _   => NativeMethods.NORMAL_PRIORITY_CLASS,
        };

        // Attach to the parent console so CLI/help output is visible when run
        // from cmd/powershell (the app is a WinExe with no console of its own).
        // When stdout is already redirected (piped shell, VSCode extension), the
        // handles are already wired — skip AttachConsole which would replace them
        // with the parent's console (which may not exist in that context).
        private static void ShowConsole()
        {
            if (!Console.IsOutputRedirected)
                try { NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS); } catch { }
        }
    }
}
