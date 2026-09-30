#Requires -Version 7
<#
.SYNOPSIS
  Live cases for the v2.3.1 service hardening (audit findings L3-01, L3-02, L3-03, L3-04,
  L3-06, L3-14, L1-03): client disconnects, timeout with the client gone, a descendant that
  keeps the output pipe open, a silent connection, bare-CR redraws and a very long line.
.DESCRIPTION
  Each case fails on an installed 2.3.0 service and passes on 2.3.1, so a run against 2.3.0
  is the mutation evidence for the suite (-ExpectedVersion records which build ran in the
  title). Runs from a non-elevated shell whose SID is a trusted caller; the elevated /jobs,
  /kill and handle.exe calls go through Invoke-Elevated. Leaves no launch slot held:
  leftover jobs are ended through /kill in the finally block.

  Handle leaks are measured on PROCESS-type handles of the service (Sysinternals handle.exe,
  elevated), not on the total handle count: the total moves by tens either way with
  thread-pool and finalizer timing, while the 2.3.0 defect leaked exactly one process
  handle per run.
#>
param(
    [string]$ExpectedVersion,
    [switch]$AllowElevated,
    [string[]]$Only
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\Elevated.ps1')

$com = Get-InstalledCom
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
if (-not (Test-Path $com)) { throw "installed RunAsHelper.com not found at $com" }

$skipTags = @()
if (-not $AllowElevated) { $skipTags += 'needs-elevated' }
$filter = @{ SkipTags = $skipTags; Integration = $true }
if ($Only) { $filter.Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
Reset-Run -Filter $filter

function Get-ServiceHandleCount {
    $p = Get-Process RunAsHelper.Service -ErrorAction SilentlyContinue
    if ($null -eq $p) { throw 'service process not found' }
    $p.Refresh()
    $p.HandleCount
}

function Get-ServiceProcessHandleCount {
    # Counts the service's handles of type Process with Sysinternals handle.exe (needs
    # administrator rights for a SYSTEM process). Returns -1 when handle.exe is unavailable.
    $svc = Get-Process RunAsHelper.Service -ErrorAction SilentlyContinue
    if ($null -eq $svc) { throw 'service process not found' }
    if (-not (Get-Command handle.exe -ErrorAction SilentlyContinue)) { return -1 }
    $r = Invoke-Elevated -FilePath 'handle.exe' -ArgumentList @('-accepteula', '-nobanner', '-a', '-p', "$($svc.Id)") -TimeoutSec 120
    @($r.Output | Where-Object { $_ -match ':\s+Process\s' }).Count
}

function Start-CaptureAndDropClient {
    # Starts the launcher with the given arguments, waits $KillAfterMs, then ends the
    # RunAsHelper.exe child the launcher started (the client side of the pipe), which is
    # what Ctrl+C or a closed terminal does. Returns the launcher process (still exiting).
    param([string[]]$Arguments, [int]$KillAfterMs)
    $psi = [Diagnostics.ProcessStartInfo]::new($com)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.Environment['__COMPAT_LAYER'] = 'RunAsInvoker'
    $launcher = [Diagnostics.Process]::Start($psi)
    # Drain in the background so the child never blocks on our pipe before the kill.
    $launcher.BeginOutputReadLine(); $launcher.BeginErrorReadLine()
    Start-Sleep -Milliseconds $KillAfterMs
    $child = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($launcher.Id)" | Where-Object { $_.Name -eq 'RunAsHelper.exe' })
    foreach ($c in $child) { Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue }
    $launcher.WaitForExit(15000) | Out-Null
    [pscustomobject]@{ Launcher = $launcher; ChildrenEnded = $child.Count }
}

function Get-ActiveJobIds {
    $r = Invoke-Elevated -FilePath $com -ArgumentList @('/jobs')
    @($r.Output | Where-Object { $_ -match '^\s*(\d+)\s+\d+:\d\d' } | ForEach-Object { [int]$Matches[1] })
}

function Assert-NoProcessHandleLeak {
    param([int]$Before, [int]$After, [int]$TotalBefore, [int]$TotalAfter, [int]$Runs, [string]$Label)
    Write-Host ("  process handles: before {0} after {1}; total handles: before {2} after {3} (information only)" -f $Before, $After, $TotalBefore, $TotalAfter)
    if ($Before -lt 0) { Skip-Case -Reason 'handle.exe (Sysinternals) is not on PATH, so process handles cannot be counted' }
    Assert-True (($After - $Before) -lt $Runs) "${Label}: process-handle growth $($After - $Before) stays below one per run (2.3.0 leaked one per run)"
}

$W = New-WorkDir
try {
    Invoke-Case -Id 'H1' -Name 'client dropped mid-stream: no job left, slot released, no process-handle leak (L3-14, L3-01)' -Tags @('needs-elevated') -Test {
        Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        $t0 = Get-ServiceHandleCount
        $p0 = Get-ServiceProcessHandleCount
        $ended = 0
        foreach ($i in 1..5) {
            $r = Start-CaptureAndDropClient -Arguments @('/capture', '/as:system', 'cmd', '/c', 'for /l %i in (1,1,100000) do @echo line %i') -KillAfterMs 1000
            $ended += $r.ChildrenEnded
        }
        Assert-True ($ended -ge 5) "the client exe was ended in every run ($ended of 5)"
        $none = Wait-Until -TimeoutSec 15 -Condition { @(Get-ActiveJobIds).Count -eq 0 }
        Assert-True $none 'no job is still listed 15 s after the clients dropped (2.3.0 keeps them until /kill)'
        Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        Start-Sleep -Seconds 3
        Assert-NoProcessHandleLeak -Before $p0 -After (Get-ServiceProcessHandleCount) -TotalBefore $t0 -TotalAfter (Get-ServiceHandleCount) -Runs 5 -Label 'dropped clients'
    }

    Invoke-Case -Id 'H2' -Name 'timeout fires with the client gone: no process-handle leak (L3-02)' -Tags @('needs-elevated') -Test {
        Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        $t0 = Get-ServiceHandleCount
        $p0 = Get-ServiceProcessHandleCount
        foreach ($i in 1..5) {
            Start-CaptureAndDropClient -Arguments @('/capture', '/timeout:5', '/as:system', 'cmd', '/c', 'ping -n 12 127.0.0.1 >nul') -KillAfterMs 2000 | Out-Null
            Start-Sleep -Seconds 7
        }
        Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        Start-Sleep -Seconds 3
        Assert-NoProcessHandleLeak -Before $p0 -After (Get-ServiceProcessHandleCount) -TotalBefore $t0 -TotalAfter (Get-ServiceHandleCount) -Runs 5 -Label 'timeouts with the client gone'
    }

    Invoke-Case -Id 'H3' -Name 'a descendant holding the output pipe does not defeat /timeout (L3-03)' -Test {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Console -FilePath $com -TimeoutSec 40 -Env $comp -ArgumentList @('/capture', '/timeout:20', '/as:system', 'cmd', '/c', 'start /b ping -n 20 127.0.0.1 >nul & echo parent-done')
        $sw.Stop()
        Write-Host ("  returned after {0:n1} s, exit {1}" -f $sw.Elapsed.TotalSeconds, $r.ExitCode)
        Assert-Match 'parent-done' $r.Stdout 'the parent output arrived'
        Assert-True ($sw.Elapsed.TotalSeconds -lt 8) 'returned within 8 s (3 s drain grace plus slack; 2.3.0 waits for the 20 s ping)'
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match 'still held open' $r.Stdout 'the detach note is printed'
    }

    Invoke-Case -Id 'H4' -Name 'a connection that never sends a request is dropped after 30 s (L3-06)' -Test {
        $c = [IO.Pipes.NamedPipeClientStream]::new('.', 'RunAsHelper', [IO.Pipes.PipeDirection]::InOut)
        try {
            $c.Connect(5000)
            $buf = New-Object byte[] 4
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $task = $c.ReadAsync($buf, 0, 4)
            $done = $task.Wait(60000)
            $sw.Stop()
            $closed = $false
            if ($done) { $closed = ($task.IsFaulted -or $task.Result -eq 0) }
            Write-Host ("  read returned after {0:n1} s (completed={1})" -f $sw.Elapsed.TotalSeconds, $done)
            Assert-True $closed 'the server closed the silent connection (2.3.0 keeps it open)'
            Assert-InRange $sw.Elapsed.TotalSeconds 25 45 'time to close'
        } finally { $c.Dispose() }
    }

    Invoke-Case -Id 'H5' -Name 'bare-CR redraws arrive as separate lines and no line ends in CR (L1-03)' -Test {
        # Invoke-Console's process reader splits on CR itself and would mask the defect, so
        # capture the launcher's raw stdout bytes through a cmd file redirect instead.
        $out = Join-Path $W 'h5-redraw.bin'
        $cmdLine = '"' + $com + '" /capture /timeout:15 /as:system powershell -NoProfile -Command "[Console]::Out.Write(''10%'' + [char]13 + ''20%'' + [char]13 + ''30%'' + [char]13 + [char]10)" > "' + $out + '" 2>&1'
        $env:__COMPAT_LAYER = 'RunAsInvoker'
        & cmd.exe /d /c $cmdLine | Out-Null
        $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($out))
        $lineWithCr = [regex]::Matches($text, '\d\d%\r\n').Count
        Write-Host ("  redraw lines terminated by CRLF: {0}; embedded CR between redraws: {1}" -f $lineWithCr, ($text -match '%\r\d\d%'))
        Assert-Equal 3 $lineWithCr 'three redraws each on its own CRLF-terminated line (2.3.0 delivers one line with embedded CRs)'
        Assert-True (-not ($text -match '%\r\d\d%')) 'no bare CR left between redraws'
        Assert-True (-not ($text -match '\r\r\n')) 'no line ends in a doubled CR'
    }

    Invoke-Case -Id 'H6' -Name 'a 5 MiB line is delivered in 1 MiB pieces instead of dropping the client (L3-04)' -Test {
        $r = Invoke-Console -FilePath $com -TimeoutSec 120 -Env $comp -ArgumentList @('/capture', '/timeout:90', '/as:system', 'powershell', '-NoProfile', '-Command', "'a' * 5MB")
        Assert-ExitCode 0 $r.ExitCode
        $aLines = @($r.Stdout -split "`r?`n" | Where-Object { $_ -match '^a+$' })
        $total = ($aLines | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum
        $longest = ($aLines | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
        Write-Host "  pieces $($aLines.Count), total $total, longest $longest"
        Assert-Equal (5 * 1048576) $total 'all 5 MiB of a arrived'
        Assert-True ($longest -le 1048576) 'no piece longer than 1 MiB'
    }
} finally {
    if ($AllowElevated) {
        foreach ($id in @(Get-ActiveJobIds)) { Invoke-Elevated -FilePath $com -ArgumentList @("/kill:$id") | Out-Null }
    }
    Remove-Item -Recurse -Force $W -ErrorAction SilentlyContinue
}

Finish-Run -Title ($(if ($ExpectedVersion) { "service hardening on $ExpectedVersion" } else { 'service hardening' }))
