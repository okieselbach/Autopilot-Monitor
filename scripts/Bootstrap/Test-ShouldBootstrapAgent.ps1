# Autopilot Monitor Bootstrap Guard Test
# Dry-run only: does not exit, does not install, only reports what WOULD happen.

param(
    [string]$AgentBinPath = 'C:\ProgramData\AutopilotMonitor\Agent'
)

$ErrorActionPreference = 'SilentlyContinue'

function Write-Step {
    param(
        [string]$Status,
        [string]$Message
    )

    $prefix = switch ($Status) {
        'PASS' { '[PASS]' }
        'SKIP' { '[SKIP]' }
        'WARN' { '[WARN]' }
        'INFO' { '[INFO]' }
        default { '[....]' }
    }

    Write-Host "$prefix $Message"
}

function Get-RegistryValueSafe {
    param(
        [string]$Path,
        [string]$Name
    )

    try {
        return (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name
    }
    catch {
        return $null
    }
}

$AgentExePath = Join-Path $AgentBinPath 'AutopilotMonitor.Agent.exe'
$MaxBootstrapWindowHours = 12

$wouldInstall = $true
$reasons = New-Object System.Collections.Generic.List[string]

Write-Host ''
Write-Host '=== Autopilot Monitor Bootstrap Guard Test (Dry Run) ==='
Write-Host ''

# Guard 1: Agent was already deployed on this device (survives self-destruct)
$deployed = Get-RegistryValueSafe -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed'
if ($deployed) {
    Write-Step -Status 'SKIP' -Message "Guard 1: Agent was previously deployed at '$deployed'."
    $wouldInstall = $false
    $reasons.Add("Previously deployed marker exists: $deployed")
} else {
    Write-Step -Status 'PASS' -Message 'Guard 1: No previous deployment marker found.'
}

# Guard 2: No real user profile should exist yet (primary productive-device guard)
# NOTE: OOBEInProgress is NOT used -- it is unreliable (observed =0 during active enrollment).
$excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users)$'
$wmiProfileQueryFailed = $false

$profileNames = @(
    # WMI/CIM view -- Special flag reliably excludes SYSTEM/LocalService/NetworkService
    try {
        Get-CimInstance Win32_UserProfile -ErrorAction Stop |
            Where-Object {
                -not $_.Special -and
                $_.LocalPath -like 'C:\Users\*'
            } |
            ForEach-Object {
                Split-Path $_.LocalPath -Leaf
            }
    }
    catch {
        $wmiProfileQueryFailed = $true
    }

    # Filesystem view -- catches profiles WMI might miss
    (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).Name
) |
Where-Object {
    $_ -and $_ -notmatch $excludePattern
} |
Select-Object -Unique

if ($wmiProfileQueryFailed) {
    Write-Step -Status 'WARN' -Message 'Guard 2: WMI profile query failed, filesystem check still applied.'
}

if ($profileNames) {
    $names = ($profileNames | Select-Object -First 3) -join ', '
    Write-Step -Status 'SKIP' -Message "Guard 2: Real user profile(s) found ($names). Device appears productive."
    $wouldInstall = $false
    $reasons.Add("Real user profile(s) found: $names")
} else {
    Write-Step -Status 'PASS' -Message 'Guard 2: No real user profiles found (combined WMI/filesystem view).'
}

# Guard 3: LastLoggedOnUser -- during Device ESP no real user has logged on yet
$lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*') {
    Write-Step -Status 'SKIP' -Message "Guard 3: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
    $wouldInstall = $false
    $reasons.Add("LastLoggedOnUser found: $lastLoggedOnUser")
} else {
    Write-Step -Status 'PASS' -Message "Guard 3: No real LastLoggedOnUser (value: '$lastLoggedOnUser')."
}

# Guard 4: explorer.exe running -- during Device ESP no desktop shell is active
$explorerRunning = $null -ne (Get-Process explorer -ErrorAction SilentlyContinue)
if ($explorerRunning) {
    Write-Step -Status 'SKIP' -Message 'Guard 4: explorer.exe is running. Device appears beyond initial enrollment.'
    $wouldInstall = $false
    $reasons.Add('explorer.exe is running')
} else {
    Write-Step -Status 'PASS' -Message 'Guard 4: explorer.exe is not running.'
}

# Guard 5: Bootstrap window check
# NOT "how long may enrollment take" -- agent handles that internally (6h emergency break).
# This is "how old can the OOBE state be before I no longer trust it for initial install".
# 12h bootstrap window vs 6h agent emergency break = consistent layered approach.
# Sleep/standby does NOT reset uptime -- only real boot/restart does.
$lastBoot = $null
$uptimeHours = $null

try {
    $lastBoot = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours

    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        Write-Step -Status 'SKIP' -Message "Guard 5: Device uptime is $([int]$uptimeHours)h. Older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
        $wouldInstall = $false
        $reasons.Add("Uptime exceeds bootstrap window: $([int]$uptimeHours)h > ${MaxBootstrapWindowHours}h")
    } else {
        Write-Step -Status 'PASS' -Message "Guard 5: Device uptime is $([int]$uptimeHours)h and within bootstrap window of ${MaxBootstrapWindowHours}h."
    }
}
catch {
    Write-Step -Status 'WARN' -Message 'Guard 5: Could not determine LastBootUpTime / uptime.'
}

# Guard 6: Agent binary already present
if (Test-Path $AgentExePath) {
    Write-Step -Status 'SKIP' -Message "Guard 6: Agent already installed at '$AgentExePath'."
    $wouldInstall = $false
    $reasons.Add("Agent binary already present: $AgentExePath")
} else {
    Write-Step -Status 'PASS' -Message "Guard 6: Agent binary not present at '$AgentExePath'."
}

Write-Host ''
Write-Host '=== Result ==='
Write-Host ''

if ($wouldInstall) {
    Write-Host '[DECISION] WOULD INSTALL agent on this device.'
} else {
    Write-Host '[DECISION] WOULD SKIP agent installation on this device.'
}

Write-Host ''
Write-Host '=== Summary ==='

if ($reasons.Count -eq 0) {
    Write-Host 'No blocking reasons found.'
} else {
    foreach ($reason in $reasons) {
        Write-Host " - $reason"
    }
}

Write-Host ''
Write-Host '=== Raw Values ==='
Write-Host "Deployed marker           : $deployed"
Write-Host "DetectedProfileNames      : $($profileNames -join ', ')"
Write-Host "LastLoggedOnUser          : $lastLoggedOnUser"
Write-Host "ExplorerRunning           : $explorerRunning"
Write-Host "MaxBootstrapWindowHours   : $MaxBootstrapWindowHours"
Write-Host "LastBootUpTime            : $lastBoot"
Write-Host "UptimeHours               : $([string]$uptimeHours)"
Write-Host "AgentExePath              : $AgentExePath"
Write-Host ''
