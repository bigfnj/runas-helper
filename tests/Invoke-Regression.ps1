#Requires -Version 7
<#
.SYNOPSIS
  The 2.2.0 behavior suite: what must not change in v2.3.0. Every case not tagged
  [changed-in-2.3.0] must PASS against the installed 2.2.0 build, and everything must
  pass against the installed 2.3.0 build with -NewBuild.
.DESCRIPTION
  Read-only cases run by default. Cases that write persistent machine state (R4 writes
  an HKLM scratch key, removed afterwards) need -AllowMachineWrites; cases that need the
  installed exe running with administrator rights need -AllowElevated. -NewBuild says
  the installed build is 2.3.0: the changed-in-2.3.0 cases run instead of skipping and
  the integration-only cases run.

  Non-elevated capture cases use the installed RunAsHelper.exe through Invoke-Console
  with __COMPAT_LAYER=RunAsInvoker and stdout piped (the 2.2.0-supported form), so the
  same case runs on both builds.
#>
param(
    [string]$ExpectedVersion,
    [switch]$NewBuild,
    [switch]$AllowElevated,
    [switch]$AllowMachineWrites,
    [string[]]$Only
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\Elevated.ps1')
. (Join-Path $here 'lib\Fixtures.ps1')
. (Join-Path $here 'lib\EventLog.ps1')

$exe = Get-InstalledExe
$com = Get-InstalledCom
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$cn = Get-ComputerNameLocal
$runStart = Get-Date

$skipTags = @()
if (-not $AllowMachineWrites) { $skipTags += 'machine-write' }
if (-not $AllowElevated) { $skipTags += 'needs-elevated' }
$filter = @{ OnTwoTwoZero = (-not $NewBuild); SkipTags = $skipTags; Integration = [bool]$NewBuild }
if ($Only) { $filter.Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
Reset-Run -Filter $filter

$W = New-WorkDir
$fx = New-Fixtures -WorkDir $W
$scratchKeyWritten = $false
try {
    Invoke-Case -Id 'R1' -Name 'exe --help via RunAsInvoker returns help, exit 0' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match 'TrustedInstaller' $r.Stdout 'help text'
        Assert-True ($r.Lines.Count -ge 100) "help line count $($r.Lines.Count) >= 100"
    }

    Invoke-Case -Id 'R2' -Name 'direct exe --help without compat fails Win32 740' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 15
        Assert-Equal 740 $r.Win32Code 'Win32 error'
    }

    Invoke-Case -Id 'R3' -Name 'capture echo witness as system, warns without timeout' -Test {
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/as:system', 'cmd', '/c', "echo", "WITNESS-$cn")
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match "WITNESS-$cn" $r.Stdout 'child witness'
        Assert-Match '\[warning\] /capture used without /timeout' $r.Stdout 'no-timeout warning'
    }

    Invoke-Case -Id 'R4' -Name '.reg host imports and .cmd host runs' -Tags @('machine-write') -Test {
        # Writes an HKLM scratch key via the regedit host, then reads it back. The key
        # is removed in the finally block below.
        $script:scratchKeyWritten = $true
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @($fx.Reg)
        Assert-ExitCode 0 $r.ExitCode
        $ok = Wait-Until -TimeoutSec 10 -Condition {
            (Get-ItemProperty 'HKLM:\SOFTWARE\RunAsHelperSmoke' -Name Marker -ErrorAction SilentlyContinue).Marker -eq $fx.Guid
        }
        Assert-True $ok 'HKLM marker equals the fixture guid'
        $r2 = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', $fx.Cmd)
        Assert-Match "CMDHOST-$cn" $r2.Stdout 'cmd host output'
    }

    Invoke-Case -Id 'R5' -Name 'elevated /jobs lists an in-flight capture' -Tags @('needs-elevated') -Test {
        $job = Start-Job -ScriptBlock {
            param($exe)
            $env:__COMPAT_LAYER = 'RunAsInvoker'
            & $exe /capture /timeout:20 /as:system cmd /c "ping -n 12 127.0.0.1 >nul" | Out-Null
        } -ArgumentList $exe
        try {
            Start-Sleep -Seconds 3
            # Elevated calls go through the console launcher when it exists: the elevated
            # runner cannot rely on waiting for a GUI exe or reading its exit code.
            $client = if (Test-Path $com) { $com } else { $exe }
            $r = Invoke-Elevated -FilePath $client -ArgumentList @('/jobs')
            Assert-ExitCode 0 $r.ExitCode
            Assert-Match 'Slots in use:' $r.Text 'jobs header'
            Assert-Match 'ping -n 12' $r.Text 'job command'
        } finally {
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            Wait-Until -TimeoutSec 20 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        }
    }

    Invoke-Case -Id 'R6' -Name 'Medium /jobs returns 1 with the elevated-required message' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('/jobs') -TimeoutSec 20 -Env $comp
        Assert-ExitCode 1 $r.ExitCode
        # The denial is written to stderr, so match the combined streams.
        Assert-Match 'running elevated' ($r.Stdout + $r.Stderr) 'elevated-required text'
    }

    Invoke-Case -Id 'R7' -Name 'trusted caller: Medium launch allowed, Medium /trusted denied, elevated /trusted lists the SID' -Tags @('needs-elevated', 'changed-in-2.3.0') -Test {
        # 2.3.0 verbs; on 2.2.0 there is no /trusted and this case is skipped.
        $r = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', '/as:system', 'cmd', '/c', 'echo', "WITNESS-$cn")
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match "WITNESS-$cn" $r.Stdout 'a trusted Medium caller may launch'
        $m = Invoke-Console -FilePath $com -ArgumentList @('/trusted') -TimeoutSec 20 -Env $comp
        Assert-ExitCode 1 $m.ExitCode
        $e = Invoke-Elevated -FilePath $com -ArgumentList @('/trusted')
        Assert-ExitCode 0 $e.ExitCode
        Assert-Match ([regex]::Escape((Get-CallerSid))) $e.Text 'caller SID present in the trusted list'
    }

    Invoke-Case -Id 'R8' -Name 'a running tray window carries the version in its title' -Tags @('integration-only') -Test {
        $titles = @(Get-Process RunAsHelper -ErrorAction SilentlyContinue | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ })
        if ($titles.Count -eq 0) { Skip-Case -Reason 'no tray window is open' }
        Assert-Match 'RunAS Helper - v\d+\.\d+\.\d+' $titles[0] 'title format'
        if ($ExpectedVersion) { Assert-Match ([regex]::Escape("RunAS Helper - v$ExpectedVersion")) $titles[0] 'title version' }
    }

    Invoke-Case -Id 'R9' -Name 'HKCU Run entry names the installed exe with --tray' -Tags @('integration-only') -Test {
        $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).RunAsHelper
        Assert-Equal ('"' + $exe + '" --tray') ([string]$v) 'HKCU Run value'
    }

    Invoke-Case -Id 'R10' -Name 'as:ti and as:system both run as SYSTEM; TI group only for ti' -Test {
        # whoami /all carries both the User section (S-1-5-18 for the LocalSystem account)
        # and the Groups section (the TrustedInstaller group, present only for /as:ti).
        $ti = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/timeout:20', '/as:ti', 'whoami', '/all')
        $sys = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/timeout:20', '/as:system', 'whoami', '/all')
        Assert-Match 'S-1-5-18' $ti.Stdout 'ti run runs as the SYSTEM account'
        Assert-Match 'S-1-5-18' $sys.Stdout 'system run runs as the SYSTEM account'
        $tiGroup = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
        Assert-Match $tiGroup $ti.Stdout 'ti run carries the TrustedInstaller group'
        Assert-NotMatch $tiGroup $sys.Stdout 'system run has no TrustedInstaller group'
    }

    Invoke-Case -Id 'R11' -Name 'priority /p:2 yields an Idle child' -Test {
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @(
            '/capture', '/timeout:20', '/p:2', '/as:system', 'powershell', '-NoProfile', '-Command',
            '(Get-Process -Id $PID).PriorityClass')
        Assert-Match 'Idle' $r.Stdout 'child priority class'
    }

    Invoke-Case -Id 'R12' -Name 'three captures run in parallel, not serialized' -Test {
        $jobs = 1..3 | ForEach-Object {
            Start-Job -ScriptBlock {
                param($exe)
                $env:__COMPAT_LAYER = 'RunAsInvoker'
                & $exe /capture /timeout:15 /as:system cmd /c "ping -n 6 127.0.0.1 >nul" | Out-Null
            } -ArgumentList $exe
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $jobs | Wait-Job -Timeout 30 | Out-Null
        $sw.Stop()
        $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
        Assert-InRange $sw.Elapsed.TotalSeconds 0 14 'wall clock for three parallel 6s pings'
    }

    Invoke-Case -Id 'R13' -Name 'elevated /kill ends a job and logs 1006' -Tags @('needs-elevated') -Test {
        $t = Get-Date
        $client = if (Test-Path $com) { $com } else { $exe }
        $job = Start-Job -ScriptBlock {
            param($client)
            $env:__COMPAT_LAYER = 'RunAsInvoker'
            & $client /capture /timeout:60 /as:system cmd /c "ping -n 40 127.0.0.1 >nul" | Out-Null
        } -ArgumentList $client
        try {
            Start-Sleep -Seconds 3
            $jobsOut = Invoke-Elevated -FilePath $client -ArgumentList @('/jobs')
            Assert-ExitCode 0 $jobsOut.ExitCode
            $row = @($jobsOut.Output | Where-Object { $_ -match 'ping -n 40' })
            Assert-True ($row.Count -ge 1) 'the capture appears in /jobs'
            $id = [int](($row[0].Trim() -split '\s+')[0])
            $kill = Invoke-Elevated -FilePath $client -ArgumentList @("/kill:$id")
            Assert-ExitCode 0 $kill.ExitCode
            Assert-Match 'terminated' $kill.Text 'kill confirmation'
            $ev = @(Get-RunAsHelperEvents -Id 1006 -Since $t)
            Assert-True ($ev.Count -ge 1) 'a 1006 event was written'
        } finally {
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            Wait-Until -TimeoutSec 20 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        }
    }

    Invoke-Case -Id 'R14' -Name 'old 2.2.0 client copy stays compatible with the service' -Test {
        $backup = 'D:\.ai-work\_backups\runas-helper-2.2.0-baseline\RunAsHelper.exe'
        if (-not (Test-Path $backup)) { Skip-Case -Reason "no 2.2.0 client backup at $backup" }
        $client = Join-Path $W 'old-client.exe'
        Copy-Item $backup $client -Force
        $r = Invoke-Console -FilePath $client -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/timeout:20', '/as:system', 'cmd', '/c', "echo", "WITNESS-$cn")
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match "WITNESS-$cn" $r.Stdout 'old client witness'
        $j = Invoke-Console -FilePath $client -ArgumentList @('/jobs') -TimeoutSec 20 -Env $comp
        Assert-ExitCode 1 $j.ExitCode
    }

    Invoke-Case -Id 'R15' -Name 'no 1099 crash events during the run and no crash.log' -Test {
        $events = @(Get-RunAsHelperEvents -Id 1099 -Since $runStart)
        Assert-Equal 0 $events.Count '1099 crash events since run start'
        $crash = Join-Path $env:AppData 'RunAsHelper\crash.log'
        Assert-True (-not (Test-Path $crash)) 'crash.log absent'
    }

    Invoke-Case -Id 'R16' -Name 'validate dialog, tray panes, dark mode' -Tags @('gui') -Test {
        # Never a vacuous PASS: with the gui tag removed this still records a SKIP.
        Skip-Case -Reason 'GUI only; manual coverage recorded in BACKLOG NT-07'
    }
} finally {
    if ($scratchKeyWritten) {
        # Remove the R4 scratch key through the service (the key is HKLM).
        Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @(
            '/capture', '/timeout:10', '/as:system', 'reg', 'delete', 'HKLM\SOFTWARE\RunAsHelperSmoke', '/f') | Out-Null
    }
    Remove-Fixtures -WorkDir $W
}

Finish-Run -Title ($(if ($NewBuild) { 'regression on the new build' } else { 'regression 2.2.0' }))
