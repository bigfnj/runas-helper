#Requires -Version 7
<#
.SYNOPSIS
  Verify a published GitHub release: the workflow run, the single MSI asset, its
  Authenticode signature and thumbprint, the File and Environment tables, the three
  binaries' signatures from an admin image, then install-over and re-run the suites.
.DESCRIPTION
  Downloads and installs, so it MUST NOT run in Phase 1. Run -DryRun there to print
  the steps. The real verification is driven at release time.
#>
param(
    [Parameter(Mandatory)][string]$Tag,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = 'bigfnj/runas-helper'
$thumb = '0EEBB64DCE430D98D2CA19DC3DC715DB9999BAD5'
$ver = $Tag.TrimStart('v')

$steps = @(
    "gh run list --repo $repo --workflow=release.yml --limit 1 --json databaseId,conclusion,headBranch -> conclusion success, headBranch $Tag",
    "gh release view $Tag --json assets -> exactly one asset RunAsHelper-Setup-$ver.msi; gh release download; record Get-FileHash",
    "Get-AuthenticodeSignature -> Valid, thumbprint $thumb, TimeStamperCertificate non-null",
    "Invoke-MsiContent.ps1 -ExpectedVersion $ver -> .com present, three binaries at $ver.0, Environment PATH row",
    "Admin image: elevated msiexec /a <msi> /qn TARGETDIR=<adm>; Get-AuthenticodeSignature on the extracted .com/.exe/.Service.exe -> all Valid and timestamped",
    "Invoke-InstallCycle.ps1 -MsiPath <released> -ExpectedVersion $ver (upgrade over 2.2.9N)",
    "Invoke-Smoke.ps1 -Phase All -ExpectedVersion $ver; Invoke-Regression.ps1 -ExpectedVersion $ver"
)

if ($DryRun) {
    Write-Host "DRY RUN Invoke-ReleaseVerify for $Tag (no download, no install):"
    $i = 1
    foreach ($s in $steps) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    Write-Host "DRY RUN complete. Run without -DryRun only at release time."
    exit 0
}

throw "Invoke-ReleaseVerify without -DryRun downloads and installs a release. Run it only at release time, not in Phase 1."
