#Requires -Version 7
<#
.SYNOPSIS
  Post-release audit measurements: service handle soak, launch-slot saturation,
  --help latency, event consistency and an orphan scan. Each yields a number or a
  DECLINED-MEASURED note, never a bare claim.
.DESCRIPTION
  The soak and saturation probes drive many launches and belong to the Phase 4 audit;
  -DryRun prints the plan. -HelpLatency is read-only and safe to run any time: it
  times the installed exe's --help over N runs and prints the median.
#>
param(
    [switch]$DryRun,
    [switch]$HelpLatency,
    [int]$Runs = 10
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')

$plan = @(
    "Handle soak: (Get-Process RunAsHelper.Service).HandleCount before/after 100 captures + 100 fire-and-forget + 50 denials, twice; growth that repeats between rounds is a leak",
    "Slot saturation: 10 concurrent /capture launches complete and the 11th gets 'busy' within 30 s",
    "--help latency: median over $Runs runs of the .com (WinForms init runs for CLI paths too)",
    "Event consistency: 1001/1002/1003/1006 counts match the launches driven; no 1099",
    "Orphan scan: Get-ServiceChildren is empty in a clean finally"
)

if ($HelpLatency) {
    $exe = Get-InstalledExe
    $comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
    $times = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
        $times += $r.DurationMs
    }
    $sorted = @($times | Sort-Object)
    $median = $sorted[[int]([Math]::Floor($sorted.Count / 2))]
    Write-Host ("--help latency over {0} runs (installed 2.2.0 exe, piped): median {1} ms, min {2} ms, max {3} ms" -f `
            $Runs, $median, $sorted[0], $sorted[-1])
    exit 0
}

if ($DryRun) {
    Write-Host "DRY RUN Invoke-AuditProbes (Phase 4; no launches driven here):"
    $i = 1
    foreach ($s in $plan) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    Write-Host "Run -HelpLatency for the one read-only measurement available in Phase 1."
    exit 0
}

throw "Invoke-AuditProbes soak/saturation drive many launches and belong to the Phase 4 audit. Use -DryRun or -HelpLatency in Phase 1."
