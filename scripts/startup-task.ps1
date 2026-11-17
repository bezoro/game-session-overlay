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

if (-not $ExecutablePath) {
    $ExecutablePath = @(
        (Join-Path $PSScriptRoot 'ApolloOverlay.exe'),
        (Join-Path $PSScriptRoot 'current/ApolloOverlay.exe'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'current/ApolloOverlay.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $ExecutablePath) {
        throw 'The installed application was not found. Specify ExecutablePath.'
    }
}

$item = Get-Item -LiteralPath $ExecutablePath
if ($item.PSIsContainer -or $item.Name -inotmatch '^(ApolloOverlay|Game Session Overlay)\.exe$') {
    throw 'ExecutablePath must identify the installed application or stable launcher.'
}
if ($item.Name -ieq 'Game Session Overlay.exe') {
    $item = Get-Item -LiteralPath (Join-Path $item.DirectoryName 'current/ApolloOverlay.exe')
}

# Wait for the actual app, not Velopack's short-lived stable launcher. The application
# verifies the installation, task/user ownership and administrative permission, and
# changes the task and registry together through its existing rollback boundary.
$process = Start-Process -FilePath $item.FullName -ArgumentList @('--configure-startup', $Action) -WorkingDirectory $item.DirectoryName -Wait -PassThru
try {
    if ($process.ExitCode -ne 0) {
        throw "Startup repair was not confirmed (exit $($process.ExitCode)). Existing ownership checks apply. Copy the overlay diagnostics for details."
    }
}
finally {
    $process.Dispose()
}

if ($Action -eq 'Install') {
    Write-Output 'Administrator startup is configured. The overlay will launch hidden at the next sign-in.'
    Write-Output 'Verify execution and Guide behavior in the actual session after signing in again.'
}
else {
    Write-Output 'The owned startup task was removed and hidden startup is disabled.'
}
