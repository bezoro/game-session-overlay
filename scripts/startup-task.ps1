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
    throw 'Execute este script em um PowerShell aberto como administrador. A confirmação do UAC é necessária.'
}

$taskName = 'Bezoro Game Session Overlay - ' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsPath = 'HKCU:\Software\Bezoro\GameSessionOverlay'
$valueName = 'Bezoro.GameSessionOverlay'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name

if ($Action -eq 'Install') {
    if (-not $ExecutablePath) {
        $candidate = Join-Path $PSScriptRoot 'ApolloOverlay.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $candidate = Join-Path (Split-Path -Parent $PSScriptRoot) 'ApolloOverlay.exe'
        }
        $ExecutablePath = $candidate
    }
    $item = Get-Item -LiteralPath $ExecutablePath
    if ($item.PSIsContainer -or $item.Name -ine 'ApolloOverlay.exe') {
        throw 'ExecutablePath deve indicar o ApolloOverlay.exe do pacote em uma pasta permanente.'
    }

    # Run only in the installing user's interactive session. Never store a password.
    $action = New-ScheduledTaskAction -Execute $item.FullName -Argument '--startup' -WorkingDirectory $item.DirectoryName
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null

    # Mark before removing the old Run entry so application startup cannot recreate it.
    New-Item -Path $settingsPath -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupConfigured -PropertyType DWord -Value 1 -Force | Out-Null
    if ((Get-ItemProperty -LiteralPath $runPath -Name $valueName -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $runPath -Name $valueName
    }
    Write-Output "Tarefa instalada para $identity. A overlay iniciará oculta no próximo logon."
    Write-Output 'Valide no próximo logon se a tarefa foi executada e o Guide funciona na sua sessão.'
} else {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    New-Item -Path $settingsPath -Force | Out-Null
    New-ItemProperty -Path $settingsPath -Name StartupConfigured -PropertyType DWord -Value 1 -Force | Out-Null
    if ((Get-ItemProperty -LiteralPath $runPath -Name $valueName -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $runPath -Name $valueName
    }
    Write-Output 'Tarefa e entrada de inicialização do usuário removidas.'
}
