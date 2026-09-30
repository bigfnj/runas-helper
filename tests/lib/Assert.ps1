#Requires -Version 7
# Assertion and case-runner primitives. One line per case: PASS <id> <name>,
# FAIL <id> <name>: <detail>, or SKIP <id> <name>: <reason>. Finish-Run prints
# RESULT: n pass / m fail / k skip and exits 1 when m > 0 OR when nothing ran
# (a suite of only skips means the harness executed nothing, which is a failure).
# State lives in $global:RahRun because these functions are dot-sourced into each
# runner, and every runner is its own fresh pwsh process, so a global cannot leak
# between suites.
Set-StrictMode -Version Latest

function Reset-Run {
    param([hashtable]$Filter = @{})
    # Filter keys the runners set: OnTwoTwoZero (regression on 2.2.0 skips
    # changed-in-2.3.0 cases), SkipTags (extra tags to skip), Only (id allow-list).
    $global:RahRun = [ordered]@{ Pass = 0; Fail = 0; Skip = 0; Rows = @(); Filter = $Filter }
}

function Get-CaseSkipReason {
    param([string]$Id, [string[]]$Tags)
    $f = $global:RahRun.Filter
    if ($f.ContainsKey('Only') -and @($f.Only) -notcontains $Id) { return "not in -Only set" }
    if ($Tags -contains 'gui') { return "GUI-only, not automatable headless" }
    # Integration-only cases run when the runner says the installed build under test is
    # the new one (Filter.Integration); on a 2.2.0 box they skip.
    $integration = $f.ContainsKey('Integration') -and $f.Integration
    if (($Tags -contains 'integration-only') -and -not $integration) { return "integration-only (needs the installed 2.3.0 build or a tray)" }
    if ($f.ContainsKey('OnTwoTwoZero') -and $f.OnTwoTwoZero -and ($Tags -contains 'changed-in-2.3.0')) {
        return "changed-in-2.3.0 (behavior deliberately differs from 2.2.0)"
    }
    if ($f.ContainsKey('SkipTags')) {
        foreach ($t in @($f.SkipTags)) { if ($Tags -contains $t) { return "skipped by runner tag '$t'" } }
    }
    return $null
}

function Write-CaseLine {
    param([string]$Verdict, [string]$Id, [string]$Name, [string]$Detail = '')
    $line = if ($Detail) { "$Verdict $Id ${Name}: $Detail" } else { "$Verdict $Id $Name" }
    Write-Host $line
    $global:RahRun.Rows += [pscustomobject]@{ Verdict = $Verdict; Id = $Id; Name = $Name; Detail = $Detail }
}

function New-AssertFailure { param([string]$Message) [pscustomobject]@{ RahKind = 'assert'; Message = $Message } }

function Skip-Case { param([Parameter(Mandatory)][string]$Reason) throw ([pscustomobject]@{ RahKind = 'skip'; Reason = $Reason }) }

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [string]$Detail = 'condition was false')
    if (-not $Condition) { throw (New-AssertFailure $Detail) }
}

function Assert-Equal {
    param([Parameter(Mandatory)]$Expected, $Actual, [string]$What = 'value')
    if ($Expected -ne $Actual) { throw (New-AssertFailure "$What expected [$Expected] got [$Actual]") }
}

function Assert-Match {
    param([Parameter(Mandatory)][string]$Pattern, [AllowNull()][AllowEmptyString()][string]$Text, [string]$What = 'text')
    if ($null -eq $Text -or $Text -notmatch $Pattern) {
        $show = if ($null -eq $Text) { '<null>' } else { $Text.Substring(0, [Math]::Min(160, $Text.Length)) }
        throw (New-AssertFailure "$What did not match /$Pattern/; got [$show]")
    }
}

function Assert-NotMatch {
    param([Parameter(Mandatory)][string]$Pattern, [AllowNull()][AllowEmptyString()][string]$Text, [string]$What = 'text')
    if ($null -ne $Text -and $Text -match $Pattern) {
        throw (New-AssertFailure "$What unexpectedly matched /$Pattern/")
    }
}

function Assert-ExitCode {
    param([Parameter(Mandatory)][int]$Expected, [int]$Actual)
    if ($Expected -ne $Actual) { throw (New-AssertFailure "exit code expected $Expected got $Actual") }
}

function Assert-InRange {
    param([Parameter(Mandatory)][double]$Value, [double]$Min, [double]$Max, [string]$What = 'value')
    if ($Value -lt $Min -or $Value -gt $Max) { throw (New-AssertFailure "$What $Value not in [$Min..$Max]") }
}

function Invoke-Case {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name,
        [string[]]$Tags = @(),
        [Parameter(Mandatory)][scriptblock]$Test
    )
    $reason = Get-CaseSkipReason -Id $Id -Tags $Tags
    if ($reason) { Write-CaseLine 'SKIP' $Id $Name $reason; $global:RahRun.Skip++; return }
    try {
        & $Test
        Write-CaseLine 'PASS' $Id $Name
        $global:RahRun.Pass++
    } catch {
        $o = $_.TargetObject
        if ($o -and ($o.PSObject.Properties.Name -contains 'RahKind') -and $o.RahKind -eq 'skip') {
            Write-CaseLine 'SKIP' $Id $Name $o.Reason; $global:RahRun.Skip++
        } elseif ($o -and ($o.PSObject.Properties.Name -contains 'RahKind') -and $o.RahKind -eq 'assert') {
            Write-CaseLine 'FAIL' $Id $Name $o.Message; $global:RahRun.Fail++
        } else {
            Write-CaseLine 'FAIL' $Id $Name ("harness error: " + $_.Exception.Message); $global:RahRun.Fail++
        }
    }
}

function Finish-Run {
    param([string]$Title = 'run')
    $r = $global:RahRun
    Write-Host ("RESULT [{0}]: {1} pass / {2} fail / {3} skip" -f $Title, $r.Pass, $r.Fail, $r.Skip)
    $ran = $r.Pass + $r.Fail
    if ($r.Fail -gt 0) { exit 1 }
    if ($ran -eq 0) { Write-Host "RESULT: nothing ran (only skips) -> failing"; exit 1 }
    exit 0
}
