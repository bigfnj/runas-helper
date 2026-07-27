<#
.SYNOPSIS
  Create (or reuse) a self-signed code-signing certificate for RunAS Helper and
  export its PUBLIC certificate for machine trust.

.DESCRIPTION
  The certificate is created in the CURRENT user's personal store
  (Cert:\CurrentUser\My) with an exportable key, so signtool — run as the same
  (non-elevated) user — can sign with it by thumbprint. Only the *public*
  certificate is written to disk (serenity-software.cer); the private key never
  leaves the user's certificate store.

  A self-signed signature is trusted only where its public certificate has been
  imported into the machine's Trusted Root store. Pass -TrustMachine (elevated)
  to import it into LocalMachine\Root + LocalMachine\TrustedPublisher on this box,
  which makes Windows show "Serenity Software" as a verified publisher with no
  "unknown publisher" warning. Importing a self-signed root is a deliberate trust
  change — do it only on machines you control.

.OUTPUTS
  The certificate SHA-1 thumbprint (last line of stdout), for Build-Signed.ps1.
#>
[CmdletBinding()]
param(
    [string]$Subject      = 'Serenity Software',
    [string]$FriendlyName = 'Serenity Software Code Signing',
    [int]   $YearsValid   = 10,
    [string]$CerOut       = "$PSScriptRoot\serenity-software.cer",
    # Import the public cert into LocalMachine Root + TrustedPublisher (needs elevation).
    [switch]$TrustMachine
)
$ErrorActionPreference = 'Stop'

$dn         = "CN=$Subject"
$codeEku    = '1.3.6.1.5.5.7.3.3'   # Code Signing EKU

# Reuse an existing code-signing cert with this subject if one is already present.
$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $dn -and ($_.EnhancedKeyUsageList.ObjectId -contains $codeEku) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "Reusing existing code-signing certificate ($($cert.Thumbprint), expires $($cert.NotAfter.ToString('yyyy-MM-dd')))."
}
else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $dn `
        -FriendlyName $FriendlyName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -KeySpec Signature `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears($YearsValid)
    Write-Host "Created code-signing certificate ($($cert.Thumbprint), expires $($cert.NotAfter.ToString('yyyy-MM-dd')))."
}

# Export the PUBLIC certificate (no private key) — safe to commit and to bundle.
Export-Certificate -Cert $cert -FilePath $CerOut -Force | Out-Null
Write-Host "Exported public certificate to $CerOut"

if ($TrustMachine) {
    foreach ($store in 'Root', 'TrustedPublisher') {
        Import-Certificate -FilePath $CerOut -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        Write-Host "Imported into LocalMachine\$store."
    }
}

# Last line: the thumbprint, for the build wrapper to capture.
$cert.Thumbprint
