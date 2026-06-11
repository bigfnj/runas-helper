using System;
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

            // Elevation hand-off: the non-elevated tray's "Activate" button
            // relaunches the exe elevated with this flag. It is NOT a CLI launch —
            // it opens the tray window, but waits briefly for the predecessor
            // (the non-elevated instance) to release the single-instance mutex.
            bool activateHandoff = args.Length == 1 &&
                args[0].Equals("--activate", StringComparison.OrdinalIgnoreCase);

            // CLI mode: RunAsHelper.exe [/p:n] <path with optional args>
            if (args.Length > 0 && !activateHandoff)
            {
                RunCli(string.Join(" ", args));
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
            Application.Run(new MainForm());
        }

        private static void RunCli(string rawArgs)
        {
            uint priority = NativeMethods.NORMAL_PRIORITY_CLASS;
            string commandLine = rawArgs;

            if (rawArgs.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) && rawArgs.Length >= 5)
            {
                char code = rawArgs[3];
                priority = code switch
                {
                    '1' => NativeMethods.NORMAL_PRIORITY_CLASS,
                    '2' => NativeMethods.IDLE_PRIORITY_CLASS,
                    '3' => NativeMethods.HIGH_PRIORITY_CLASS,
                    '4' => NativeMethods.REALTIME_PRIORITY_CLASS,
                    '5' => NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
                    '6' => NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS,
                    _   => NativeMethods.NORMAL_PRIORITY_CLASS,
                };
                commandLine = rawArgs[5..].TrimStart();
            }

            if (string.IsNullOrWhiteSpace(commandLine))
            {
                Console.Error.WriteLine("Usage: RunAsHelper.exe [/p:n] <path> [args]");
                Console.Error.WriteLine("  /p:1 Normal  /p:2 Idle  /p:3 High");
                Console.Error.WriteLine("  /p:4 Realtime  /p:5 BelowNormal  /p:6 AboveNormal");
                Environment.Exit(1);
                return;
            }

            var client = new PipeClient();
            client.LogMessage += msg => Console.WriteLine(msg);
            bool ok = client.LaunchElevatedAsync(commandLine, priority)
                            .GetAwaiter().GetResult();
            Environment.Exit(ok ? 0 : 1);
        }
    }
}
