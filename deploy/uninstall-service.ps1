<#
.SYNOPSIS
    Removes the OpenCodeGoProxy Windows service, its program files and (optionally) its data.

.DESCRIPTION
    Stops and deletes the 'OpenCodeGoProxy' service, waits until Windows has removed
    it, deletes the program folder and - unless -KeepData is used - the data folder
    that holds session.json and the log files. The Application event log source is
    removed unless -KeepEventSource is passed.

    Running the script when nothing is installed is safe: it reports that there was
    nothing to do and exits with code 0.

    Run this from an elevated PowerShell session (Run as administrator).
    Use -WhatIf to see the planned actions without deleting anything.

.PARAMETER InstallPath
    Program folder to remove. Default: C:\Program Files\OpenCodeGoProxy

.PARAMETER DataPath
    Data folder (session.json, logs) to remove unless -KeepData is used.
    Default: C:\ProgramData\OpenCodeGoProxy

.PARAMETER KeepData
    Keep the data folder. The program folder is still removed (it is the
    application, not user data), so a reinstall is required to run the proxy again.

.PARAMETER KeepEventSource
    Keep the 'OpenCodeGoProxy' Application event log source.

.EXAMPLE
    .\uninstall-service.ps1

    Stops and deletes the service, then removes the program files, the data folder
    and the event log source.

.EXAMPLE
    .\uninstall-service.ps1 -KeepData -KeepEventSource

    Removes the service and program files but keeps session.json, the logs and the
    event log source.

.EXAMPLE
    .\uninstall-service.ps1 -WhatIf

    Shows what would be removed. Nothing is changed.

.NOTES
    Requires elevation (except for a pure -WhatIf preview).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallPath = 'C:\Program Files\OpenCodeGoProxy',

    [string]$DataPath = 'C:\ProgramData\OpenCodeGoProxy',

    [switch]$KeepData,

    [switch]$KeepEventSource
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'OpenCodeGoProxy'
$EventSourceName = 'OpenCodeGoProxy'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note {
    param([string]$Message)
    Write-Host "    $Message"
}

function Write-WarningLine {
    param([string]$Message)
    Write-Host "WARNING: $Message" -ForegroundColor Yellow
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ServiceExists {
    param([string]$Name)
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) { return $true }
    return $false
}

# SourceExists throws for non-admin sessions ("some event logs could not be searched").
function Test-EventSourceExists {
    param([string]$Name)
    try {
        return [System.Diagnostics.EventLog]::SourceExists($Name)
    }
    catch {
        return $false
    }
}

function Stop-ProxyService {
    param([string]$Name, [int]$TimeoutSeconds = 30)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    if ($svc.Status -eq 'Stopped') {
        Write-Note "Service '$Name' is already stopped."
        return
    }

    Write-Note "Stopping service '$Name' (current status: $($svc.Status)) ..."
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $current = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $current -or $current.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }

    $current = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($current -and $current.Status -ne 'Stopped') {
        throw "Service '$Name' did not stop within $TimeoutSeconds seconds (status: $($current.Status))."
    }
    Write-Note "Service '$Name' stopped."
}

function Remove-ProxyService {
    param([string]$Name, [int]$TimeoutSeconds = 30)

    Write-Note "Deleting service '$Name' ..."
    $output = & sc.exe delete $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        # 1060: service does not exist (already gone) - treat as success.
        if ($LASTEXITCODE -eq 1060) {
            Write-Note "Service '$Name' does not exist; nothing to delete."
            return
        }
        throw ("sc.exe delete {0} failed with exit code {1}: {2}" -f $Name, $LASTEXITCODE, (($output | Out-String).Trim()))
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-ServiceExists -Name $Name)) {
            Write-Note "Service '$Name' deleted."
            return
        }
        Start-Sleep -Milliseconds 500
    }

    throw "Service '$Name' still exists $TimeoutSeconds seconds after 'sc.exe delete'."
}

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
$InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
$DataPath = [System.IO.Path]::GetFullPath($DataPath)
$logPath = Join-Path $DataPath 'logs'

# ---------------------------------------------------------------------------
# Elevation guard (fails fast, except for a pure -WhatIf preview)
# ---------------------------------------------------------------------------
if (-not (Test-IsElevated)) {
    if ($WhatIfPreference) {
        Write-WarningLine 'Not running elevated. -WhatIf only lists planned actions; nothing will be changed.'
    }
    else {
        Write-Host 'ERROR: This script must run from an elevated PowerShell session.' -ForegroundColor Red
        Write-Host ''
        Write-Host 'Open PowerShell with "Run as administrator" and run:'
        Write-Host '    .\deploy\uninstall-service.ps1'
        exit 1
    }
}

Write-Step 'Uninstalling OpenCode Go Proxy'
Write-Note "Install path : $InstallPath"
Write-Note "Data path    : $DataPath"
Write-Note "Keep data    : $([bool]$KeepData)"
Write-Note "Keep event source: $([bool]$KeepEventSource)"

$serviceExisted = Test-ServiceExists -Name $ServiceName
$removedSomething = $false

try {
    # -----------------------------------------------------------------------
    # Service
    # -----------------------------------------------------------------------
    Write-Step 'Service'
    if ($serviceExisted) {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete the service')) {
            Stop-ProxyService -Name $ServiceName
            Remove-ProxyService -Name $ServiceName
            $removedSomething = $true
        }
    }
    else {
        Write-Note "Service '$ServiceName' is not installed; nothing to remove."
    }

    # -----------------------------------------------------------------------
    # Data
    # -----------------------------------------------------------------------
    Write-Step 'Data folder'
    if (Test-Path -LiteralPath $DataPath) {
        if ($KeepData) {
            Write-Note "-KeepData was specified: keeping $DataPath (session.json and logs are kept)."
        }
        elseif ($PSCmdlet.ShouldProcess($DataPath, 'Delete the data folder (session.json and log files)')) {
            Write-Note "Deleting $DataPath - this removes session.json and all log files."
            Remove-Item -LiteralPath $DataPath -Recurse -Force
            Write-Note 'Data folder deleted.'
            $removedSomething = $true
        }
    }
    else {
        Write-Note "$DataPath does not exist; nothing to remove."
    }

    # -----------------------------------------------------------------------
    # Program files (always removed, even with -KeepData)
    # -----------------------------------------------------------------------
    Write-Step 'Program files'
    if (Test-Path -LiteralPath $InstallPath) {
        if ($PSCmdlet.ShouldProcess($InstallPath, 'Delete the program folder')) {
            Write-Note "Deleting $InstallPath"
            Remove-Item -LiteralPath $InstallPath -Recurse -Force
            Write-Note 'Program folder deleted.'
            $removedSomething = $true
        }
    }
    else {
        Write-Note "$InstallPath does not exist; nothing to remove."
    }
    if ($KeepData) {
        Write-Note 'Note: -KeepData keeps the data folder only; the program files are always removed.'
    }

    # -----------------------------------------------------------------------
    # Event log source
    # -----------------------------------------------------------------------
    Write-Step 'Event log source'
    if ($KeepEventSource) {
        Write-Note "-KeepEventSource was specified: keeping the '$EventSourceName' event log source."
    }
    else {
        if ($PSCmdlet.ShouldProcess($EventSourceName, 'Remove the Application event log source')) {
            try {
                $sourceExists = Test-EventSourceExists -Name $EventSourceName

                if ($sourceExists) {
                    Remove-EventLog -Source $EventSourceName -ErrorAction SilentlyContinue
                    Write-Note "Removed event log source '$EventSourceName'."
                    $removedSomething = $true
                }
                else {
                    Write-Note "Event log source '$EventSourceName' does not exist; nothing to remove."
                }
            }
            catch {
                Write-WarningLine "Could not remove event log source '$EventSourceName': $($_.Exception.Message)"
            }
        }
    }
}
catch {
    Write-Host ''
    Write-Host 'ERROR: Uninstall failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host 'Inspect the current state:'
    Write-Host "    Get-Service $ServiceName -ErrorAction SilentlyContinue | Format-List Name,Status,StartType"
    Write-Host "    sc.exe qc $ServiceName"
    Write-Host "    Test-Path '$InstallPath'; Test-Path '$DataPath'"
    Write-Host ''
    Write-Host 'If the service is marked for deletion, close Services (services.msc) or reboot,'
    Write-Host 'then re-run this script.'
    exit 1
}

# ---------------------------------------------------------------------------
# Final state
# ---------------------------------------------------------------------------
Write-Step 'Final state'
Write-Note ("Service '{0}' installed: {1}" -f $ServiceName, (Test-ServiceExists -Name $ServiceName))
Write-Note ("Install path exists: {0}" -f (Test-Path -LiteralPath $InstallPath))
Write-Note ("Data path exists   : {0}" -f (Test-Path -LiteralPath $DataPath))
if (Test-Path -LiteralPath $DataPath) {
    Write-Note ("Session file       : {0}" -f (Test-Path -LiteralPath (Join-Path $DataPath 'session.json')))
    Write-Note ("Log path exists    : {0}" -f (Test-Path -LiteralPath $logPath))
}
Write-Note ("Event source exists: {0}" -f (Test-EventSourceExists -Name $EventSourceName))

Write-Host ''
if ($WhatIfPreference) {
    Write-Host 'WhatIf: no changes were made. The planned actions are listed above.' -ForegroundColor Yellow
    exit 0
}
if ($removedSomething) {
    Write-Host 'Uninstall complete.' -ForegroundColor Green
}
else {
    Write-Host 'Nothing was installed; nothing had to be removed.' -ForegroundColor Green
}
exit 0
