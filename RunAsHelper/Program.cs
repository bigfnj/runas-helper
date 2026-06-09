using System;
using System.Windows.Forms;
using RunAsHelper.Core;

namespace RunAsHelper
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // CLI mode: RunAsHelper.exe [/p:n] <path with optional args>
            if (args.Length > 0)
            {
                RunCli(string.Join(" ", args));
                return;
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
