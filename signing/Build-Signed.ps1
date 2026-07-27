<#
.SYNOPSIS
  Build a signed RunAS Helper release (both EXEs + the MSI), Authenticode-signed
  with the Serenity Software code-signing certificate.

.DESCRIPTION
  Resolves signtool.exe (newest Windows SDK), resolves the signing certificate
  thumbprint (creating/reusing it via New-SigningCert.ps1 if not supplied), then
  runs the solution build with the signing MSBuild properties. The installer's
  sign targets sign the two published EXEs before WiX packs them and sign the MSI
  after link. A plain 'dotnet build' (without these properties) stays unsigned.

.EXAMPLE
  .\signing\Build-Signed.ps1 -Version 1.5.3
#>
[CmdletBinding()]
param(
    [string]$Version       = '',
    [string]$Configuration = 'Release',
    [string]$Thumbprint    = '',
    # RFC3161 timestamp server. Set to '' to sign without a timestamp (the
    # signature then expires with the certificate). Auto-cleared if unreachable.
    [string]$TimestampUrl  = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# --- Resolve signtool.exe (newest Windows SDK x64) ---------------------------
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK.' }
Write-Host "signtool: $($signtool.FullName)"

# --- Resolve signing certificate thumbprint ----------------------------------
if (-not $Thumbprint) {
    Write-Host 'No -Thumbprint supplied; creating/reusing the signing certificate...'
    $Thumbprint = (& "$PSScriptRoot\New-SigningCert.ps1")[-1]
}
Write-Host "Signing thumbprint: $Thumbprint"

# --- Timestamp reachability (skip gracefully if offline) ---------------------
if ($TimestampUrl) {
    $tsHost = ([uri]$TimestampUrl).Host
    if (-not (Test-NetConnection $tsHost -Port 80 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
        Write-Warning "Timestamp server '$tsHost' unreachable. Signing WITHOUT a timestamp (signature expires with the certificate)."
        $TimestampUrl = ''
    }
}

# --- Build with signing properties -------------------------------------------
$props = @(
    "-p:SigningCertThumbprint=$Thumbprint",
    "-p:SignToolPath=$($signtool.FullName)",
    "-p:SignTimestampUrl=$TimestampUrl"
)
if ($Version) { $props += "-p:ProductVersion=$Version" }

Write-Host "dotnet build $repo\RunAsHelper.sln -c $Configuration $($props -join ' ')"
& dotnet build "$repo\RunAsHelper.sln" -c $Configuration @props
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$msi = Join-Path $repo "RunAsHelper.Installer\bin\x64\$Configuration\RunAsHelper-Setup.msi"
Write-Host ''
Write-Host "Signed build complete: $msi"