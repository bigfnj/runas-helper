#Requires -Version 7
# Environment and machine-state helpers. Never hard-code this box's SID, computer
# name or user name: derive them at run time.
Set-StrictMode -Version Latest

# Where the product is installed. Precedence: RAH_INSTALL_DIR in the environment (a
# staged folder holding a built RunAsHelper.exe next to a copy of RunAsHelper.com, for a
# dev smoke against the installed service), then the InstallFolder value the MSI writes
# (HKLM\SOFTWARE\RunAsHelper, Package.wxs; 2.2.0 wrote none), then the MSI's default
# folder, [ProgramFiles6432Folder]RunAsHelper, which is not always C:\Program Files.
$script:InstallDir = $null

function Get-InstallDir {
    if ($null -eq $script:InstallDir) {
        $fromEnv = $env:RAH_INSTALL_DIR
        # The key can exist without the value (AllowedCallerSids survives an uninstall while
        # InstallFolder is removed), and under StrictMode reading a missing property throws,
        # which took the whole harness down on a box with nothing installed.
        $props = Get-ItemProperty 'HKLM:\SOFTWARE\RunAsHelper' -ErrorAction SilentlyContinue
        $fromReg = if ($props -and ($props.PSObject.Properties.Name -contains 'InstallFolder')) { $props.InstallFolder } else { $null }
        $script:InstallDir = if ($fromEnv) { $fromEnv.TrimEnd('\') }
                             elseif ($fromReg) { ([string]$fromReg).TrimEnd('\') }
                             else { Join-Path ($env:ProgramW6432 ?? $env:ProgramFiles) 'RunAsHelper' }
    }
    $script:InstallDir
}
function Get-InstalledExe { Join-Path (Get-InstallDir) 'RunAsHelper.exe' }
function Get-InstalledCom { Join-Path (Get-InstallDir) 'RunAsHelper.com' }
function Get-InstalledService { Join-Path (Get-InstallDir) 'RunAsHelper.Service.exe' }

function Get-CallerSid { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }

function Get-AllowedCallerSids {
    # The trusted command-line users the service reads (HKLM\SOFTWARE\RunAsHelper,
    # AllowedCallerSids); an empty list when the key or the value is absent.
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\RunAsHelper' -ErrorAction SilentlyContinue
    @(if ($reg -and ($reg.PSObject.Properties.Name -contains 'AllowedCallerSids')) { $reg.AllowedCallerSids })
}
function Get-ComputerNameLocal { $env:COMPUTERNAME }

function Get-MachinePath { [Environment]::GetEnvironmentVariable('Path', 'Machine') }
function Get-UserPath { [Environment]::GetEnvironmentVariable('Path', 'User') }

function Get-MachinePathEntries {
    # Parenthesize the call: "Get-MachinePath -split ';'" would hand -split to the
    # function as an argument and return the whole PATH as one entry.
    @((Get-MachinePath) -split ';' | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') })
}

function Get-MachinePathValueKind {
    # The machine PATH must stay REG_EXPAND_SZ ('ExpandString'): entries such as
    # %SystemRoot%\system32 stop resolving if an installer rewrites it as REG_SZ.
    (Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').GetValueKind('Path').ToString()
}

function Get-RefreshedPath {
    # The MSI broadcasts WM_SETTINGCHANGE, but already-running shells (including the
    # agent host) keep their launch-time PATH. Rebuild it from the registry so a
    # child sees the machine + user PATH the installer wrote.
    ((Get-MachinePath) + ';' + (Get-UserPath))
}

function Update-SessionPath {
    $env:Path = Get-RefreshedPath
}

function Get-RefreshedPathEnv {
    # A child-environment hashtable that carries the registry PATH plus RunAsInvoker,
    # for cases that launch a shell which must resolve the freshly installed .com.
    @{ Path = (Get-RefreshedPath); __COMPAT_LAYER = 'RunAsInvoker' }
}

function Wait-Until {
    param([Parameter(Mandatory)][scriptblock]$Condition, [int]$TimeoutSec = 30, [int]$PollMs = 250)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $PollMs
    }
    return $false
}

function Wait-ForPipe {
    param([int]$TimeoutSec = 30)
    Wait-Until -TimeoutSec $TimeoutSec -Condition {
        @([IO.Directory]::GetFiles('\\.\pipe\')) -contains '\\.\pipe\RunAsHelper'
    }
}

function Test-PipePresent {
    @([IO.Directory]::GetFiles('\\.\pipe\')) -contains '\\.\pipe\RunAsHelper'
}

function New-WorkDir {
    param([string]$Root = $env:TEMP)
    $p = Join-Path $Root ("rah-tests-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $p -Force | Out-Null
    $p
}

function Get-PeSubsystem {
    # Returns 2 for the Windows GUI subsystem, 3 for the console (CUI) subsystem.
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $peOff = [BitConverter]::ToInt32($bytes, 0x3C)          # e_lfanew
    if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff + 1] -ne 0x45) { throw "not a PE: $Path" }
    # PE signature (4) + COFF header (20) = start of the optional header; Subsystem
    # sits at optional-header offset 0x44.
    $subsystemOff = $peOff + 4 + 20 + 0x44
    [BitConverter]::ToUInt16($bytes, $subsystemOff)
}

function Get-FileVersionOf {
    param([Parameter(Mandatory)][string]$Path)
    (Get-Item $Path).VersionInfo.FileVersion
}
