#Requires -Version 7
# Invoke-Elevated: run a program elevated and get its output and exit code back.
# -Verb RunAs cannot combine with -RedirectStandardOutput, so the elevated child
# writes its own out.txt / rc.txt and this waits for them. On this box UAC
# auto-consents (ConsentPromptBehaviorAdmin=0), so the elevation is silent.
Set-StrictMode -Version Latest

function Invoke-Elevated {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSec = 180
    )
    $dir = New-WorkDir
    $argsFile = Join-Path $dir 'args.json'
    $outFile = Join-Path $dir 'out.txt'
    $rcFile = Join-Path $dir 'rc.txt'
    $runner = Join-Path $dir 'runner.ps1'

    [pscustomobject]@{ FilePath = $FilePath; Args = @($ArgumentList) } |
        ConvertTo-Json -Depth 5 | Set-Content -Path $argsFile -Encoding UTF8

    # The runner captures 2>&1 so both streams reach out.txt, and records the exit
    # code of the child. & waits for a console child and sets $LASTEXITCODE; a GUI exe
    # records no exit code (measured), so callers pass the console launcher.
    $runnerBody = @'
$ErrorActionPreference = 'Continue'
$a = Get-Content -Raw -LiteralPath "__ARGS__" | ConvertFrom-Json
$out = & $a.FilePath @($a.Args) 2>&1
[IO.File]::WriteAllLines("__OUT__", @($out | ForEach-Object { "$_" }))
[IO.File]::WriteAllText("__RC__", "$LASTEXITCODE")
'@
    $runnerBody = $runnerBody.Replace('__ARGS__', $argsFile).Replace('__OUT__', $outFile).Replace('__RC__', $rcFile)
    Set-Content -Path $runner -Value $runnerBody -Encoding UTF8

    # Start-Process joins -ArgumentList with plain spaces, so the runner path (under %TEMP%,
    # which can hold a space) is quoted here or pwsh gets half a path and exits 64.
    $proc = Start-Process pwsh -Verb RunAs -WindowStyle Hidden -PassThru `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $runner + '"')
    $exited = $proc.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) {
        try { $proc.Kill() } catch { }
        try { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue } catch { }
        throw "elevated run timed out after $TimeoutSec s"
    }

    # $LASTEXITCODE is set only for a console target that & waited on. A GUI-subsystem
    # target (the installed RunAsHelper.exe) or one that failed to start leaves rc.txt
    # empty, and then the target's exit code is unknown: throw rather than return a number.
    # The runner's own exit code only says the runner ran, so returning it would let an
    # "exit 0" assertion pass without measuring the target. Read with ReadAllText: [string]
    # of Get-Content -Raw on an empty file is $null in pwsh 7, not ''.
    $rcText = if (Test-Path $rcFile) { [IO.File]::ReadAllText($rcFile).Trim() } else { '' }
    $output = if (Test-Path $outFile) { @(Get-Content $outFile) } else { @() }
    try { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue } catch { }
    if ($rcText -notmatch '^-?\d+$') {
        throw "elevated run of $FilePath recorded no exit code (runner exit $($proc.ExitCode)); a GUI-subsystem target is not waited on, so call the console launcher"
    }
    [pscustomobject]@{ ExitCode = [int]$rcText; Output = $output; Text = ($output -join "`n") }
}
