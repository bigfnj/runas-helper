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
    # code of the child. & waits for a console child; for a GUI exe the caller passes
    # the installed exe and the runner still returns once it exits.
    $runnerBody = @'
$ErrorActionPreference = 'Continue'
$a = Get-Content -Raw -LiteralPath "__ARGS__" | ConvertFrom-Json
$out = & $a.FilePath @($a.Args) 2>&1
[IO.File]::WriteAllLines("__OUT__", @($out | ForEach-Object { "$_" }))
[IO.File]::WriteAllText("__RC__", "$LASTEXITCODE")
'@
    $runnerBody = $runnerBody.Replace('__ARGS__', $argsFile).Replace('__OUT__', $outFile).Replace('__RC__', $rcFile)
    Set-Content -Path $runner -Value $runnerBody -Encoding UTF8

    $proc = Start-Process pwsh -Verb RunAs -WindowStyle Hidden -PassThru `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner
    $exited = $proc.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) { try { $proc.Kill() } catch { } ; throw "elevated run timed out after $TimeoutSec s" }

    # rc.txt is empty when the runner's & did not wait for a GUI target (PowerShell sets
    # no $LASTEXITCODE then), so prefer the console launcher for elevated calls and fall
    # back to the runner's own exit code here rather than failing on a null string.
    $rcText = if (Test-Path $rcFile) { [string](Get-Content -Raw $rcFile) } else { '' }
    $rc = if ($rcText.Trim() -match '^-?\d+$') { [int]$rcText.Trim() } else { $proc.ExitCode }
    $output = if (Test-Path $outFile) { @(Get-Content $outFile) } else { @() }
    try { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue } catch { }
    [pscustomobject]@{ ExitCode = $rc; Output = $output; Text = ($output -join "`n") }
}
