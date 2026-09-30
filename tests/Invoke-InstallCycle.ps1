#Requires -Version 7
<#
.SYNOPSIS
  Install / verify / (optionally) uninstall / reinstall an MSI on this box, checking
  PATH, versions, AllowedCallerSids, HKCU Run and settings.json across the cycle.
.DESCRIPTION
  This mutates machine state and MUST NOT run in Phase 1. Run -DryRun there: it prints
  the exact steps it would take and exits 0 without touching anything. The real cycle
  is driven only at integration, from the lead's serial run.
#>
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [string]$ExpectedVersion,
    [switch]$Cycle,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Elevated.ps1')
. (Join-Path $here 'lib\Msi.ps1')

$steps = @(
    "Snapshot: PATH entries, AllowedCallerSids, HKCU Run, settings.json hash, service status, tray count",
    "Stop tray: elevated taskkill /IM RunAsHelper.exe /F /T (a running tray locks the exe -> msiexec 3010)",
    "Install: elevated msiexec /i `"$MsiPath`" /qn /norestart /l*v install.log  (exit 0 required; 3010 and 1603 are failures)",
    "Verify: service Running, Wait-ForPipe, FileVersion of .com/.exe/.Service.exe = $ExpectedVersion, install folder on machine PATH, AllowedCallerSids unchanged, HKCU Run unchanged, settings.json hash unchanged, newest 1005 event",
    "Start tray: elevated Start-Process RunAsHelper.exe (no args) for the R8 title check"
)
if ($Cycle) {
    $steps += @(
        "Uninstall: elevated msiexec /x {ProductCode} /qn /norestart /l*v uninstall.log",
        "Verify uninstall: PATH entry gone, other PATH entries intact, service absent, install folder empty, AllowedCallerSids still present",
        "Reinstall and re-verify"
    )
}

if ($DryRun) {
    Write-Host "DRY RUN Invoke-InstallCycle (no machine state changed):"
    $i = 1
    foreach ($s in $steps) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    Write-Host "DRY RUN complete. Run without -DryRun only at integration."
    exit 0
}

throw "Invoke-InstallCycle without -DryRun changes machine state and must run only at integration, from the lead's serial cycle. Refusing in this context."
