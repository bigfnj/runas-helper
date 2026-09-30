#Requires -Version 7
# Process primitives: run a console child and capture its output/exit code without
# deadlocking, and enumerate service-launched children (TI/SYSTEM orphans whose parent
# is the service). Nothing here starts or stops a tray: the runners that do (smoke A6,
# the install cycle) check for a running instance themselves first.
Set-StrictMode -Version Latest

function Invoke-Console {
    <#
      Runs $FilePath with $ArgumentList, redirecting stdout/stderr with async reads so
      a chatty child never deadlocks a full pipe buffer. $Env is merged into the child
      environment (for example __COMPAT_LAYER=RunAsInvoker or a refreshed Path). On
      timeout the child is killed and TimedOut is set. Returns a result object.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSec = 60,
        [hashtable]$Env,
        [string]$WorkingDirectory
    )
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    foreach ($a in $ArgumentList) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($Env) { foreach ($k in $Env.Keys) { $psi.Environment[$k] = [string]$Env[$k] } }

    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $outSb = [Text.StringBuilder]::new()
    $errSb = [Text.StringBuilder]::new()
    $outEvt = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -Action {
        if ($null -ne $Event.SourceEventArgs.Data) { [void]$Event.MessageData.AppendLine($Event.SourceEventArgs.Data) }
    } -MessageData $outSb
    $errEvt = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
        if ($null -ne $Event.SourceEventArgs.Data) { [void]$Event.MessageData.AppendLine($Event.SourceEventArgs.Data) }
    } -MessageData $errSb

    $started = Get-Date
    $timedOut = $false
    $win32 = $null
    try {
        [void]$p.Start()
        $p.BeginOutputReadLine()
        $p.BeginErrorReadLine()
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            $timedOut = $true
            try { $p.Kill($true) } catch { }
            $p.WaitForExit(5000) | Out-Null
        }
        # Let the async handlers drain the last lines.
        $p.WaitForExit()
    } catch [ComponentModel.Win32Exception] {
        $win32 = $_.Exception
    } finally {
        Unregister-Event -SourceIdentifier $outEvt.Name -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errEvt.Name -ErrorAction SilentlyContinue
    }

    $durationMs = [int]((Get-Date) - $started).TotalMilliseconds
    if ($win32) {
        return [pscustomobject]@{
            ExitCode = -1; Stdout = ''; Stderr = ''; Lines = @(); DurationMs = $durationMs
            TimedOut = $false; Win32 = $win32; Win32Code = $win32.NativeErrorCode; Win32Message = $win32.Message
        }
    }
    $stdout = $outSb.ToString()
    [pscustomobject]@{
        ExitCode = $p.ExitCode
        Stdout = $stdout
        Stderr = $errSb.ToString()
        Lines = @($stdout -split "`r?`n")
        DurationMs = $durationMs
        TimedOut = $timedOut
        Win32 = $null
        Win32Code = 0
        Win32Message = ''
    }
}

function Get-ServiceChildren {
    # TI/SYSTEM launches are created by the service via CreateProcessAsUser, so an
    # orphan that outlives its request still has the service as its parent. A Medium
    # shell can enumerate these; killing one needs an elevated taskkill /PID.
    $svc = Get-Process RunAsHelper.Service -ErrorAction SilentlyContinue
    if (-not $svc) { return @() }
    @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($svc.Id)")
}
