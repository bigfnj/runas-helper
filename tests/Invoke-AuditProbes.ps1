#Requires -Version 7
<#
.SYNOPSIS
  Audit measurements against the installed build: service handle-count soak, launch-slot
  saturation, --help latency through the launcher and the exe, event-count consistency,
  and a leftover-process scan. Each probe prints its numbers and asserts a threshold, so
  a regression fails the run.
.DESCRIPTION
  Drives many launches, so run it from the integration owner's serial cycle against the
  installed 2.3.0 build (this account must be a trusted command-line caller). -DryRun
  prints the plan. -HelpLatency alone is read-only and safe any time.
#>
param(
    [switch]$DryRun,
    [switch]$HelpLatency,
    [int]$Runs = 10,
    [int]$SoakLaunches = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\EventLog.ps1')

$exe = Get-InstalledExe
$com = Get-InstalledCom
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$cn = Get-ComputerNameLocal
$client = if (Test-Path $com) { $com } else { $exe }

$plan = @(
    "Handle soak: service HandleCount before/after $SoakLaunches captures + $SoakLaunches fire-and-forget launches + 30 denied /jobs calls, two rounds; growth that repeats in round two is a leak",
    "Slot saturation: 10 concurrent captures hold every slot; an 11th request is told the service is busy and exits 1 after 25-45 s",
    "--help latency: median over $Runs runs through the .com and through the exe (RunAsInvoker, piped); every run must print the help; launcher overhead under 150 ms",
    "Event consistency: five fresh captures write five 1001 and five 1002 events; no 1099 since the probes started",
    "Leftover scan: no service child processes remain once the soak has finished"
)

function Measure-HelpLatency {
    param([string]$Path, [hashtable]$Env)
    $times = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Invoke-Console -FilePath $Path -ArgumentList @('--help') -TimeoutSec 30 -Env $Env
        # A launcher that fails fast, misroutes --help or is missing would otherwise post a
        # short time and pass the overhead check on nothing. COMMAND LINE is a help heading.
        if ($r.ExitCode -ne 0 -or $r.Stdout -notmatch 'COMMAND LINE') { throw "--help via $Path failed (exit $($r.ExitCode))" }
        $times += $r.DurationMs
    }
    $sorted = @($times | Sort-Object)
    [pscustomobject]@{ Median = $sorted[[int][Math]::Floor($sorted.Count / 2)]; Min = $sorted[0]; Max = $sorted[-1] }
}

if ($HelpLatency) {
    $viaCom = if (Test-Path $com) { Measure-HelpLatency -Path $com -Env $comp } else { $null }
    $viaExe = Measure-HelpLatency -Path $exe -Env $comp
    if ($viaCom) { Write-Host ("--help via RunAsHelper.com: median {0} ms (min {1}, max {2}) over {3} runs" -f $viaCom.Median, $viaCom.Min, $viaCom.Max, $Runs) }
    Write-Host ("--help via RunAsHelper.exe (RunAsInvoker, piped): median {0} ms (min {1}, max {2}) over {3} runs" -f $viaExe.Median, $viaExe.Min, $viaExe.Max, $Runs)
    if ($viaCom) { Write-Host ("launcher overhead: {0} ms" -f ($viaCom.Median - $viaExe.Median)) }
    exit 0
}

if ($DryRun) {
    Write-Host "DRY RUN Invoke-AuditProbes (no launches driven):"
    $i = 1
    foreach ($s in $plan) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    exit 0
}

function Get-ServiceHandleCount {
    $p = Get-Process RunAsHelper.Service -ErrorAction SilentlyContinue
    if ($null -eq $p) { throw 'service process not found' }
    $p.Refresh()
    $p.HandleCount
}

function Invoke-SoakRound {
    # Returns the number of launches and denials driven.
    $launched = 0
    $denied = 0
    for ($i = 0; $i -lt $SoakLaunches; $i++) {
        $r = Invoke-Console -FilePath $client -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', '/as:system', 'cmd', '/c', 'echo', "WITNESS-$cn")
        if ($r.ExitCode -eq 0 -and $r.Stdout -match "WITNESS-$cn") { $launched++ }
        $f = Invoke-Console -FilePath $client -TimeoutSec 30 -Env $comp -ArgumentList @('/as:system', 'cmd', '/c', 'exit', '0')
        if ($f.ExitCode -eq 0) { $launched++ }
    }
    for ($i = 0; $i -lt 30; $i++) {
        $d = Invoke-Console -FilePath $client -TimeoutSec 20 -Env $comp -ArgumentList @('/jobs')
        if ($d.ExitCode -eq 1) { $denied++ }
    }
    [pscustomobject]@{ Launched = $launched; Denied = $denied }
}

Reset-Run
Write-Host "Audit probes against $client"
$runStart = Get-Date

Invoke-Case -Id 'P1' -Name 'handle soak: no repeating growth between two rounds' -Test {
    Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
    $h0 = Get-ServiceHandleCount
    $r1 = Invoke-SoakRound
    Start-Sleep -Seconds 5
    $h1 = Get-ServiceHandleCount
    $r2 = Invoke-SoakRound
    Start-Sleep -Seconds 5
    $h2 = Get-ServiceHandleCount
    $g1 = $h1 - $h0
    $g2 = $h2 - $h1
    Write-Host ("  handles: start {0}, after round 1 {1} (+{2}), after round 2 {3} (+{4}); launches {5}+{6}, denials {7}+{8}" -f $h0, $h1, $g1, $h2, $g2, $r1.Launched, $r2.Launched, $r1.Denied, $r2.Denied)
    Assert-Equal (2 * $SoakLaunches) $r1.Launched 'round 1 launches accepted'
    Assert-Equal (2 * $SoakLaunches) $r2.Launched 'round 2 launches accepted'
    Assert-Equal 30 $r1.Denied 'round 1 denials'
    # Round one may warm caches and pools; a leak shows as growth that repeats in round
    # two at a rate comparable to the work driven. Allow slack for pool jitter.
    Assert-True ($g2 -lt 40) "round 2 handle growth $g2 is below 40"
}

Invoke-Case -Id 'P2' -Name 'slot saturation: 10 captures hold every slot, the 11th is told busy' -Test {
    $jobs = 1..10 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($client)
            $env:__COMPAT_LAYER = 'RunAsInvoker'
            & $client /capture /timeout:50 /as:system cmd /c "ping -n 45 127.0.0.1 >nul" | Out-Null
        } -ArgumentList $client
    }
    try {
        $full = Wait-Until -TimeoutSec 30 -Condition { @(Get-ServiceChildren).Count -ge 10 }
        Assert-True $full 'ten service children (one per slot) within 30 s'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Console -FilePath $client -TimeoutSec 60 -Env $comp -ArgumentList @('/capture', '/timeout:5', '/as:system', 'cmd', '/c', 'echo', "WITNESS-$cn")
        $sw.Stop()
        Write-Host ("  11th request: exit {0} after {1:n1} s" -f $r.ExitCode, $sw.Elapsed.TotalSeconds)
        Assert-ExitCode 1 $r.ExitCode
        Assert-Match 'busy' $r.Stdout 'busy message'
        Assert-InRange $sw.Elapsed.TotalSeconds 25 45 'busy wait (the service waits 30 s for a slot)'
    } finally {
        $jobs | Stop-Job -ErrorAction SilentlyContinue
        $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
        Wait-Until -TimeoutSec 90 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
    }
}

Invoke-Case -Id 'P3' -Name '--help latency through the launcher and the exe' -Test {
    $viaCom = Measure-HelpLatency -Path $com -Env $comp
    $viaExe = Measure-HelpLatency -Path $exe -Env $comp
    Write-Host ("  .com median {0} ms; .exe median {1} ms; overhead {2} ms over {3} runs" -f $viaCom.Median, $viaExe.Median, ($viaCom.Median - $viaExe.Median), $Runs)
    Assert-True (($viaCom.Median - $viaExe.Median) -lt 150) 'launcher overhead under 150 ms'
}

Invoke-Case -Id 'P4' -Name 'event consistency: 1001 count matches the launches driven, no 1099' -Test {
    $t = Get-Date
    $n = 5
    for ($i = 0; $i -lt $n; $i++) {
        Invoke-Console -FilePath $client -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', '/as:system', 'cmd', '/c', 'echo', "WITNESS-$cn") | Out-Null
    }
    Start-Sleep -Seconds 2
    $e1001 = @(Get-RunAsHelperEvents -Id 1001 -Since $t -Max 200)
    $e1002 = @(Get-RunAsHelperEvents -Id 1002 -Since $t -Max 200)
    Write-Host ("  1001={0} 1002={1} for {2} launches" -f $e1001.Count, $e1002.Count, $n)
    Assert-Equal $n $e1001.Count '1001 events'
    Assert-Equal $n $e1002.Count '1002 events'
    Assert-Equal 0 @(Get-RunAsHelperEvents -Id 1099 -Since $runStart).Count '1099 events during the probes'
}

Invoke-Case -Id 'P5' -Name 'leftover scan: no service children remain' -Test {
    $ok = Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 }
    Assert-True $ok 'no child processes of the service remain'
}

Finish-Run -Title 'audit probes'
