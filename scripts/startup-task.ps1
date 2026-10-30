[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action,
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell session. UAC confirmation is required.'
}

$taskName = 'Bezoro Game Session Overlay - ' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsPath = 'HKCU:\Software\Bezoro\GameSessionOverlay'
$valueName = 'Bezoro.GameSessionOverlay'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name

if ($Action -eq 'Install') {
    if (-not $ExecutablePath) {
        $candidate = @(
            (Join-Path $PSScriptRoot 'Game Session Overlay.exe'),
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'Game Session Overlay.exe'),
            (Join-Path $PSScriptRoot 'ApolloOverlay.exe'),
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'ApolloOverlay.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $candidate) {
            throw 'The launcher was not found. Specify ExecutablePath.'
        }
        $ExecutablePath = $candidate
    }
    $item = Get-Item -LiteralPath $ExecutablePath
    if ($item.PSIsContainer -or $item.Name -inotmatch '^(ApolloOverlay|Game Session Overlay)\.exe$') {
        throw 'ExecutablePath must identify the installed Game Session Overlay.exe launcher.'
    }

    # Run only in the installing user's interactive session. Never store a password.
    $action = New-ScheduledTaskAction -Execute $item.FullName -Argument '--startup' -WorkingDirectory $item.DirectoryName
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null

    # Mark before removing the old Run entry so application startup cannot recreate it.
    New-Item -Path $settingsPath -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupConfigured -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupEnabled -PropertyType DWord -Value 1 -Force | Out-Null
    if ((Get-ItemProperty -LiteralPath $runPath -Name $valueName -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $runPath -Name $valueName
    }
    Write-Output "Startup task installed for $identity. The overlay will launch hidden at the next sign-in."
    Write-Output 'Verify execution and Guide behavior in the actual session after signing in again.'
}
else {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    New-Item -Path $settingsPath -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupConfigured -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupEnabled -PropertyType DWord -Value 0 -Force | Out-Null
    if ((Get-ItemProperty -LiteralPath $runPath -Name $valueName -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $runPath -Name $valueName
    }
    Write-Output 'The user startup task and Run registration were removed.'
}
