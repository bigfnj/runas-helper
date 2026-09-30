#Requires -Version 7
# Invoke-ConPty: run a command line under a real pseudo console and return what it rendered.
# The witness rule applies: a run whose witness text is missing is a harness failure, not a
# product result. Callers check the witness first (see Test-ConPtyWitness).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('ConPty' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ConPty.cs')
}

function Invoke-ConPty {
    param(
        [Parameter(Mandatory = $true)][string]$CommandLine,
        [int]$TimeoutMs = 60000,
        [int]$CtrlCAfterMs = 0,
        [string]$WorkingDirectory = $null
    )
    $r = [ConPty]::Run($CommandLine, $TimeoutMs, $CtrlCAfterMs, $WorkingDirectory)
    [pscustomobject]@{
        Text     = $r.Text
        RawText  = $r.RawText
        ExitCode = $r.ExitCode
        TimedOut = $r.TimedOut
    }
}

function Test-ConPtyWitness {
    $r = Invoke-ConPty -CommandLine 'cmd.exe /d /c echo CONPTY-OK' -TimeoutMs 15000
    if ($r.Text -notmatch 'CONPTY-OK') {
        throw "WITNESS-MISSING: the pseudo console harness did not render its own witness (exit $($r.ExitCode))."
    }
    $true
}
