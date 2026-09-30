#Requires -Version 7
# Invoke-ConPty: run a command line under a real pseudo console and return what it rendered.
# The witness rule applies: a run whose witness text is missing is a harness failure, not a
# product result. Every ConPTY case calls Test-ConPtyWitness first; a missing witness throws
# WITNESS-MISSING, which Invoke-Case records as "FAIL ... harness error" and counts as a fail.
# The witness covers stderr too: with this process's stderr redirected, a console child's
# stderr goes to that redirect instead of the pseudo console, and a case asserting on a
# stderr line would then fail as if the product were wrong.
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
    $r = Invoke-ConPty -CommandLine 'cmd.exe /d /c "echo CONPTY-OK& echo CONPTY-ERR 1>&2"' -TimeoutMs 15000
    if ($r.Text -notmatch 'CONPTY-OK' -or $r.Text -notmatch 'CONPTY-ERR') {
        throw "WITNESS-MISSING: the pseudo console did not render its stdout and stderr witness lines (exit $($r.ExitCode))."
    }
    $true
}
