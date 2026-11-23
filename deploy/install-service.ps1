<#
.SYNOPSIS
    Installs OpenCodeGoProxy.exe as an automatic (delayed start) Windows service.

.DESCRIPTION
    Copies the self-contained publish output to the install folder, creates the data
    and log folders with the right ACLs, registers the Application event log source,
    creates the 'OpenCodeGoProxy' service, writes the service-scoped environment
    variables, applies failure/restart actions and finally starts the service and
    (optionally) probes the /healthz endpoint.

    Run this from an elevated PowerShell session (Run as administrator).
    Use -WhatIf to see the planned actions without changing anything.

.PARAMETER SourcePath
    Folder that contains the published OpenCodeGoProxy.exe. Relative paths are
    resolved against this script's folder. Default:
    ..\src\OpenCodeGoProxy\bin\Release\net10.0\win-x64\publish

.PARAMETER InstallPath
    Destination folder for the program files. Default: C:\Program Files\OpenCodeGoProxy

.PARAMETER DataPath
    Folder for the session store (session.json) and logs. Default: C:\ProgramData\OpenCodeGoProxy

.PARAMETER ApiKey
    Optional OpenCode Go API key. Stored as the OPENCODE_GO_API_KEY service
    environment variable under the service registry key. When omitted, configure
    the key in appsettings.Production.json in the install folder instead
    ("Proxy": { "ApiKey": "..." } or root "OPENCODE_GO_API_KEY": "...").

.PARAMETER ServiceAccount
    Account the service runs as: LocalSystem (default), LocalService or NetworkService.

.PARAMETER Force
    Replace an existing service: stop it, delete it and wait until it is gone.

.PARAMETER SkipHealthCheck
    Do not probe http://127.0.0.1:8765/healthz after starting the service.

.EXAMPLE
    .\install-service.ps1

    Installs using the default publish output, install path and data path.

.EXAMPLE
    .\install-service.ps1 -ApiKey 'sk-...' -ServiceAccount LocalService

    Installs and runs the service as NT AUTHORITY\LocalService, storing the key in
    the service environment.

.EXAMPLE
    .\install-service.ps1 -Force -WhatIf

    Shows which actions a forced reinstall would take. Nothing is changed.

.NOTES
    Requires elevation. The script never deletes appsettings.Production.json that
    already exists in the install folder.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourcePath = '..\src\OpenCodeGoProxy\bin\Release\net10.0\win-x64\publish',

    [string]$InstallPath = 'C:\Program Files\OpenCodeGoProxy',

    [string]$DataPath = 'C:\ProgramData\OpenCodeGoProxy',

    [string]$ApiKey,

    [ValidateSet('LocalSystem', 'LocalService', 'NetworkService')]
    [string]$ServiceAccount = 'LocalSystem',

    [switch]$Force,

    [switch]$SkipHealthCheck
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'OpenCodeGoProxy'
$DisplayName = 'OpenCode Go Proxy'
$EventSourceName = 'OpenCodeGoProxy'
$HealthUrl = 'http://127.0.0.1:8765/healthz'
$ServiceDescription = 'Forwards OpenAI-compatible requests to the OpenCode Go inference endpoint on http://127.0.0.1:8765.'

$script:ServiceCreated = $false
$script:LogPath = $null
$script:DataPath = $DataPath
$script:InstallPath = $InstallPath

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

function Invoke-Sc {
    param([string[]]$Arguments)
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("sc.exe {0} failed with exit code {1}: {2}" -f ($Arguments -join ' '), $LASTEXITCODE, (($output | Out-String).Trim()))
    }
    return $output
}

function Invoke-Icacls {
    param([string[]]$Arguments)
    $output = & icacls.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("icacls.exe {0} failed with exit code {1}: {2}" -f ($Arguments -join ' '), $LASTEXITCODE, (($output | Out-String).Trim()))
    }
}

function Write-ShowDiagnostics {
    Write-Host ''
    Write-Host 'Diagnostics:' -ForegroundColor Yellow
    Write-Host "    Get-Service $ServiceName | Format-List Name,Status,StartType"
    Write-Host "    sc.exe qc $ServiceName"
    Write-Host "    Get-EventLog -LogName Application -Source $EventSourceName -Newest 20 | Format-List TimeGenerated,EntryType,Message"
    Write-Host "    Get-ChildItem '$($script:LogPath)' -Filter *.log | ForEach-Object { Get-Content `$_.FullName -Tail 100 }"
    Write-Host "    Test-NetConnection -ComputerName 127.0.0.1 -Port 8765"
    Write-Host ''
    Write-Host 'To remove the service and start over (elevated):'
    Write-Host '    .\deploy\uninstall-service.ps1'
    Write-Host ''
}

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
if (-not [System.IO.Path]::IsPathRooted($SourcePath)) {
    $SourcePath = Join-Path $PSScriptRoot $SourcePath
}
$SourcePath = [System.IO.Path]::GetFullPath($SourcePath)
$InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
$DataPath = [System.IO.Path]::GetFullPath($DataPath)

$script:InstallPath = $InstallPath
$script:DataPath = $DataPath
$script:LogPath = Join-Path $DataPath 'logs'
$logPath = $script:LogPath

$sourceExe = Join-Path $SourcePath 'OpenCodeGoProxy.exe'
$installExe = Join-Path $InstallPath 'OpenCodeGoProxy.exe'
$serviceRegKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

# ---------------------------------------------------------------------------
# Validate inputs
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    Write-Host "ERROR: Publish output not found: $sourceExe" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Build the self-contained publish output first, for example:'
    Write-Host '    dotnet publish .\src\OpenCodeGoProxy\OpenCodeGoProxy.csproj -c Release -r win-x64 --self-contained true'
    Write-Host ''
    Write-Host 'Then pass the folder with -SourcePath if it is not the default location.'
    exit 1
}

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
        Write-Host "    .\deploy\install-service.ps1"
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------
Write-Step "Installing $DisplayName"
Write-Note "Source path  : $SourcePath"
Write-Note "Install path : $InstallPath"
Write-Note "Data path    : $DataPath"
Write-Note "Log path     : $logPath"
Write-Note "Service acct : $ServiceAccount"
if ($Force) { Write-Note 'Force        : replace an existing service if present' }

$hasApiKey = -not [string]::IsNullOrWhiteSpace($ApiKey)
if (-not $hasApiKey) {
    Write-Host ''
    Write-WarningLine 'No -ApiKey was supplied, so OPENCODE_GO_API_KEY will not be written to the service environment.'
    Write-Note 'Configure the key in one of these ways instead:'
    Write-Note ("  - {0}\appsettings.Production.json with `"Proxy`": {{ `"ApiKey`": `"<key>`" }}" -f $InstallPath)
    Write-Note '    (a root "OPENCODE_GO_API_KEY" value works too and takes precedence)'
    Write-Note '  - re-run this script with -ApiKey <key>'
    Write-Note '  - or: -ApiKey $env:OPENCODE_GO_API_KEY, if this elevated shell inherited your user variable'
    Write-Note 'A key in your USER environment is never visible to a service; only the service environment counts.'
    Write-Note 'Note: the Environment value is rewritten as a whole, so a key set by a previous run is dropped unless -ApiKey is passed again.'
}

try {
    # -----------------------------------------------------------------------
    # Existing service
    # -----------------------------------------------------------------------
    if (Test-ServiceExists -Name $ServiceName) {
        $existing = Get-Service -Name $ServiceName
        if (-not $Force) {
            Write-Host ''
            Write-Host "ERROR: Service '$ServiceName' already exists (status: $($existing.Status))." -ForegroundColor Red
            Write-Host 'Re-run with -Force to stop and replace it, or uninstall it first:'
            Write-Host '    .\deploy\uninstall-service.ps1'
            exit 1
        }

        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete the existing service')) {
            Stop-ProxyService -Name $ServiceName
            Remove-ProxyService -Name $ServiceName
        }
        elseif (-not $WhatIfPreference) {
            Write-Host 'Aborted: the existing service was not removed.' -ForegroundColor Red
            exit 1
        }
    }

    # -----------------------------------------------------------------------
    # Program files
    # -----------------------------------------------------------------------
    Write-Step 'Copying program files'
    if ($PSCmdlet.ShouldProcess($InstallPath, 'Create the install folder and copy the publish output')) {
        if (-not (Test-Path -LiteralPath $InstallPath)) {
            New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
            Write-Note "Created $InstallPath"
        }

        $productionConfig = Join-Path $InstallPath 'appsettings.Production.json'
        $configBackup = $null
        if (Test-Path -LiteralPath $productionConfig -PathType Leaf) {
            $configBackup = Join-Path ([System.IO.Path]::GetTempPath()) ('OpenCodeGoProxy.appsettings.Production.{0}.json' -f ([Guid]::NewGuid().ToString('N')))
            Copy-Item -LiteralPath $productionConfig -Destination $configBackup -Force
            Write-Note "Existing appsettings.Production.json found; it will be preserved."
        }

        Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
        Write-Note "Copied $SourcePath -> $InstallPath"

        if ($configBackup) {
            Copy-Item -LiteralPath $configBackup -Destination $productionConfig -Force
            Remove-Item -LiteralPath $configBackup -Force
            Write-Note 'Kept the existing appsettings.Production.json (not overwritten by the publish output).'
        }
    }

    # -----------------------------------------------------------------------
    # Data and log folders
    # -----------------------------------------------------------------------
    Write-Step 'Creating data and log folders'
    if ($PSCmdlet.ShouldProcess($DataPath, 'Create the data folder and its logs subfolder')) {
        New-Item -ItemType Directory -Path $DataPath -Force | Out-Null
        New-Item -ItemType Directory -Path $logPath -Force | Out-Null
        Write-Note "Created $DataPath"
        Write-Note "Created $logPath"
    }

    # -----------------------------------------------------------------------
    # ACLs for non-SYSTEM service accounts (SIDs so this works on any locale)
    # -----------------------------------------------------------------------
    $accountSid = $null
    $accountName = $null
    switch ($ServiceAccount) {
        'LocalService' { $accountSid = '*S-1-5-19'; $accountName = 'NT AUTHORITY\LOCAL SERVICE' }
        'NetworkService' { $accountSid = '*S-1-5-20'; $accountName = 'NT AUTHORITY\NETWORK SERVICE' }
        default { }
    }

    if ($accountSid) {
        Write-Step "Granting file system access to $accountName"
        if ($PSCmdlet.ShouldProcess($DataPath, "Grant Modify to $accountName")) {
            Invoke-Icacls -Arguments @($DataPath, '/grant', ('{0}:(OI)(CI)M' -f $accountSid))
            Write-Note "Granted Modify on $DataPath"
        }
        if ($PSCmdlet.ShouldProcess($InstallPath, "Grant Read,ReadAndExecute to $accountName")) {
            Invoke-Icacls -Arguments @($InstallPath, '/grant', ('{0}:(OI)(CI)RX' -f $accountSid))
            Write-Note "Granted Read,ReadAndExecute on $InstallPath"
        }
    }
    else {
        Write-Note 'Service runs as LocalSystem; no extra ACLs are needed.'
    }

    # -----------------------------------------------------------------------
    # Event log source
    # -----------------------------------------------------------------------
    Write-Step 'Registering the Application event log source'
    if ($PSCmdlet.ShouldProcess($EventSourceName, 'Create the Application event log source')) {
        try {
            $sourceExists = $false
            try { $sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventSourceName) } catch { $sourceExists = $false }

            if ($sourceExists) {
                Write-Note "Event log source '$EventSourceName' already exists."
            }
            else {
                New-EventLog -LogName Application -Source $EventSourceName -ErrorAction SilentlyContinue
                if ([System.Diagnostics.EventLog]::SourceExists($EventSourceName)) {
                    Write-Note "Created event log source '$EventSourceName' in the Application log."
                }
                else {
                    Write-WarningLine "Event log source '$EventSourceName' could not be confirmed; the service can still run."
                }
            }
        }
        catch {
            Write-WarningLine "Could not create event log source '$EventSourceName': $($_.Exception.Message)"
        }
    }

    # -----------------------------------------------------------------------
    # Service creation
    # -----------------------------------------------------------------------
    Write-Step 'Creating the Windows service'
    $binaryPath = '"' + $installExe + '"'
    $serviceCredential = switch ($ServiceAccount) {
        'LocalService' { 'NT AUTHORITY\LocalService' }
        'NetworkService' { 'NT AUTHORITY\NetworkService' }
        default { $null }
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, "Create the service (account: $ServiceAccount)")) {
        New-Service -Name $ServiceName `
            -BinaryPathName $binaryPath `
            -DisplayName $DisplayName `
            -Description $ServiceDescription `
            -StartupType Automatic | Out-Null
        $script:ServiceCreated = $true
        Write-Note "Created service '$ServiceName'."

        if ($serviceCredential) {
            Invoke-Sc -Arguments @('config', $ServiceName, 'obj=', $serviceCredential)
            Write-Note "Service account set to $serviceCredential."
        }
    }

    # -----------------------------------------------------------------------
    # Service-scoped environment variables (REG_MULTI_SZ)
    # -----------------------------------------------------------------------
    Write-Step 'Writing service environment variables'
    $environmentValues = @(
        "Proxy__SessionStorePath=$DataPath",
        "Proxy__LogDirectory=$logPath"
    )
    if ($hasApiKey) { $environmentValues += "OPENCODE_GO_API_KEY=$ApiKey" }

    if ($PSCmdlet.ShouldProcess($serviceRegKey, 'Write the service Environment multi-string value')) {
        if (-not (Test-Path -LiteralPath $serviceRegKey)) {
            throw "Service registry key not found: $serviceRegKey"
        }
        New-ItemProperty -Path $serviceRegKey -Name 'Environment' -PropertyType MultiString -Value $environmentValues -Force | Out-Null

        $shownValues = $environmentValues | ForEach-Object {
            if ($_ -like 'OPENCODE_GO_API_KEY=*') { 'OPENCODE_GO_API_KEY=<redacted>' } else { $_ }
        }
        Write-Note ("Environment: " + ($shownValues -join '; '))
    }

    # -----------------------------------------------------------------------
    # Startup type, description, failure actions
    # -----------------------------------------------------------------------
    Write-Step 'Configuring startup type and failure actions'
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Set delayed automatic start, description and restart-on-failure actions')) {
        Invoke-Sc -Arguments @('config', $ServiceName, 'start=', 'delayed-auto') | Out-Null
        Write-Note 'Startup type: Automatic (Delayed Start).'

        Invoke-Sc -Arguments @('description', $ServiceName, $ServiceDescription) | Out-Null
        Write-Note 'Description set.'

        Invoke-Sc -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/60000') | Out-Null
        Write-Note 'Failure actions: restart after 5s, 30s, 60s (counter reset daily).'
    }

    # -----------------------------------------------------------------------
    # Start
    # -----------------------------------------------------------------------
    Write-Step 'Starting the service'
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start the service')) {
        Start-Service -Name $ServiceName -ErrorAction Stop

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $running = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($running -and $running.Status -eq 'Running') { break }
            if ($running -and $running.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }

        $running = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $running -or $running.Status -ne 'Running') {
            $statusText = 'missing'
            if ($running) { $statusText = $running.Status }
            throw "Service '$ServiceName' did not reach Running within 30 seconds (status: $statusText)."
        }
        Write-Note "Service '$ServiceName' is running."
    }
}
catch {
    Write-Host ''
    Write-Host 'ERROR: Installation failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red

    if ($script:ServiceCreated) {
        Write-Host ''
        Write-WarningLine "The service '$ServiceName' was created but the installation did not finish. It may be stopped or misconfigured."
    }
    Write-ShowDiagnostics
    exit 1
}

# ---------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------
if ($WhatIfPreference) {
    Write-Host ''
    Write-Host 'WhatIf: no changes were made. The planned actions are listed above.' -ForegroundColor Yellow
    exit 0
}

Write-Step 'Service status'
Get-Service -Name $ServiceName | Format-List Name, Status, StartType

Write-Host ''
Write-Note "Install path : $InstallPath"
Write-Note "Data path    : $DataPath"
Write-Note "Session file : $(Join-Path $DataPath 'session.json')"
Write-Note "Log path     : $logPath"

Write-Host ''
Write-Host 'Service configuration (sc.exe qc):'
Invoke-Sc -Arguments @('qc', $ServiceName) | ForEach-Object { Write-Host ("    " + $_) }

if (-not $hasApiKey) {
    Write-Host ''
    Write-WarningLine 'The service environment does not contain OPENCODE_GO_API_KEY.'
    Write-Note "Set the key in $InstallPath\appsettings.Production.json or re-run with -ApiKey."
}

if ($SkipHealthCheck) {
    Write-Host ''
    Write-Note 'Health check skipped (-SkipHealthCheck).'
}
else {
    Write-Step "Health check ($HealthUrl)"
    $health = $null
    $healthOk = $false
    $healthDeadline = (Get-Date).AddSeconds(15)

    while ((Get-Date) -lt $healthDeadline) {
        try {
            $health = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
            $healthOk = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if ($healthOk) {
        Write-Note 'Health check: OK'
        $rendered = (($health | Out-String).Trim())
        if ($rendered.Length -gt 0) {
            Write-Note ("Response: " + ($rendered -replace '\s+', ' '))
        }
    }
    else {
        Write-Host ''
        Write-Host "ERROR: Health check failed: $HealthUrl did not respond successfully within 15 seconds." -ForegroundColor Red
        Write-ShowDiagnostics
        Write-Host 'The service itself was installed. Fix the configuration, then restart it:'
        Write-Host "    Restart-Service $ServiceName"
        exit 1
    }
}

Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
exit 0
