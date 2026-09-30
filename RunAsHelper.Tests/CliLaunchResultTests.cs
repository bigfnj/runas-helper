using RunAsHelper.Core;
using Xunit;

// The CLI's process exit code is derived from the launch outcome: 1 for a RunAsHelper
// failure, 124 on timeout, the child's own code under /capture, otherwise 0.
public class CliLaunchResultTests
{
    [Fact]
    public void Failure_Exits1()
        => Assert.Equal(1, new CliLaunchResult(false, null, false).ToProcessExitCode(captureRequested: true));

    [Fact]
    public void Timeout_Exits124()
        => Assert.Equal(124, new CliLaunchResult(true, null, true).ToProcessExitCode(captureRequested: true));

    [Fact]
    public void Timeout_BeatsAReportedExitCode()
        => Assert.Equal(124, new CliLaunchResult(true, 5, true).ToProcessExitCode(captureRequested: true));

    [Fact]
    public void Capture_PassesTheChildExitCode()
        => Assert.Equal(3, new CliLaunchResult(true, 3, false).ToProcessExitCode(captureRequested: true));

    [Fact]
    public void Capture_MapsAccessViolationDwordToNegativeInt()
        // 0xC000013A (STATUS_CONTROL_C_EXIT) as an unsigned DWORD is -1073741510 as a signed int.
        => Assert.Equal(-1073741510, new CliLaunchResult(true, 0xC000013Au, false).ToProcessExitCode(captureRequested: true));

    [Fact]
    public void NonCapture_Exits0_EvenWhenAChildCodeIsPresent()
        => Assert.Equal(0, new CliLaunchResult(true, 7, false).ToProcessExitCode(captureRequested: false));

    [Fact]
    public void CaptureAgainstAnOldService_Exits0()
        // A 2.2.0 service sends no "exit" frame, so ChildExitCode is null and capture maps to 0.
        => Assert.Equal(0, new CliLaunchResult(true, null, false).ToProcessExitCode(captureRequested: true));
}
