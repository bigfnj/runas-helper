namespace RunAsHelper.Core;

/// <summary>
/// Outcome of one CLI launch. <see cref="ChildExitCode"/> and <see cref="TimedOut"/> come
/// from the 2.3.0 "exit" and "timeout" frames and are absent against a 2.2.0 service, in
/// which case a capture launch maps to 0 exactly as it did before.
/// </summary>
internal sealed record CliLaunchResult(bool Ok, uint? ChildExitCode, bool TimedOut)
{
    public const int TimeoutExitCode = 124;   // GNU timeout(1) convention

    /// <summary>
    /// The process exit code the CLI should return: 1 for a RunAsHelper failure, 124 on a
    /// timeout, the child's own code when /capture was requested and the service reported it
    /// (0xC000013A becomes -1073741510, the same 32-bit value), otherwise 0.
    /// </summary>
    public int ToProcessExitCode(bool captureRequested)
    {
        if (!Ok) return 1;                       // unreachable / denied / launch failed
        if (TimedOut) return TimeoutExitCode;
        if (captureRequested && ChildExitCode is uint c) return unchecked((int)c);
        return 0;                                // non-capture, or capture against an old service
    }
}
