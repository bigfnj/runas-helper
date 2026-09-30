#Requires -Version 7
# Writes the small target scripts a case launches, into a work dir with absolute
# paths (the child's cwd is System32 when WorkingDirectory is empty, so a relative
# path would not resolve). Assert only on values the CHILD computes: the service
# echoes the command line back in its "Args detected" line, so a literal marker in
# the command would match that echo, not prove the child ran.
Set-StrictMode -Version Latest

function New-Fixtures {
    param([Parameter(Mandatory)][string]$WorkDir)
    $f = @{}

    $f.Version = Join-Path $WorkDir 't-version.ps1'
    Set-Content -LiteralPath $f.Version -Encoding ASCII -Value '"PSV=$($PSVersionTable.PSVersion.Major)"'

    $f.Requires7 = Join-Path $WorkDir 't-requires7.ps1'
    Set-Content -LiteralPath $f.Requires7 -Encoding ASCII -Value @(
        '#Requires -Version 7'
        '"PSV=$($PSVersionTable.PSVersion.Major)"'
    )

    $f.Desktop = Join-Path $WorkDir 't-desktop.ps1'
    Set-Content -LiteralPath $f.Desktop -Encoding ASCII -Value @(
        '#Requires -PSEdition Desktop'
        '"PSV=$($PSVersionTable.PSVersion.Major)"'
    )

    $f.Args = Join-Path $WorkDir 't-args.ps1'
    Set-Content -LiteralPath $f.Args -Encoding ASCII -Value @(
        '"ARGC=$($args.Count)"'
        '$args | ForEach-Object { "ARG=[$_]" }'
    )

    $f.Cmd = Join-Path $WorkDir 't.cmd'
    Set-Content -LiteralPath $f.Cmd -Encoding ASCII -Value '@echo CMDHOST-%COMPUTERNAME%'

    $f.Guid = [Guid]::NewGuid().ToString()
    $f.Reg = Join-Path $WorkDir 'marker.reg'
    Set-Content -LiteralPath $f.Reg -Encoding ASCII -Value @(
        'Windows Registry Editor Version 5.00'
        ''
        '[HKEY_LOCAL_MACHINE\SOFTWARE\RunAsHelperSmoke]'
        ('"Marker"="' + $f.Guid + '"')
    )

    $f.Big = Join-Path $WorkDir 'big.cmd'
    Set-Content -LiteralPath $f.Big -Encoding ASCII -Value '@for /l %%i in (1,1,100000) do @echo L%%i'

    $f.WorkDir = $WorkDir
    [pscustomobject]$f
}

function Remove-Fixtures {
    param([Parameter(Mandatory)][string]$WorkDir)
    try { Remove-Item -Recurse -Force -LiteralPath $WorkDir -ErrorAction SilentlyContinue } catch { }
}
