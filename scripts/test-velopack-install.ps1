[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleasePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) { throw 'This proof runs only on an isolated Windows GitHub Actions runner.' }
$release = & (Join-Path $PSScriptRoot 'get-validated-velopack-release.ps1') -ArtifactDirectory $ReleasePath
$proofId = [Guid]::NewGuid().ToString('N')
$installRoot = Join-Path $env:RUNNER_TEMP "overlay-velopack-install-$proofId"
$dataRoot = Join-Path $env:RUNNER_TEMP "overlay-velopack-proof-$proofId"
if ((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $dataRoot)) { throw 'Proof directories must be fresh.' }
$report = [IO.Path]::GetFullPath($OutputPath)
$evidence = Join-Path (Split-Path -Parent $report) 'velopack-install-evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$cases = [Collections.Generic.List[object]]::new()
$expectedApp = Join-Path $installRoot 'current/ApolloOverlay.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameSessionOverlay'
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsKey = 'HKCU:\Software\Bezoro\GameSessionOverlay'
function Require([bool]$Value, [string]$Reason) { if (-not $Value) { throw $Reason } }
function Read-Value([string]$Path, [string]$Name) {
    $entry = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($entry) { return $entry.$Name }; return $null
}
$priorRun = Read-Value $runPath 'Bezoro.GameSessionOverlay'
$priorMarker = Read-Value $settingsKey 'StartupConfigured'
Require (-not (Test-Path -LiteralPath $uninstallKey)) 'An installation with this application ID already exists; refusing to overwrite it.'
function Owned-Apps {
    return @(Get-Process -Name ApolloOverlay -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($expectedApp) } catch { $false }
    })
}
function Stop-Owned([Diagnostics.Process]$Process) {
    $path = $Process.Path; $created = $Process.StartTime.ToUniversalTime().Ticks
    $live = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if (-not $live) { return }
    Require ($live.Path -eq $path -and $live.StartTime.ToUniversalTime().Ticks -eq $created) 'Owned process identity changed; cleanup refused.'
    if ($live.CloseMainWindow() -and $live.WaitForExit(5000)) { return }
    Stop-Process -InputObject $live -Force
    Require ($live.WaitForExit(10000)) 'Owned proof process did not exit.'
}
function Run-Owned([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 90) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable); $start.UseShellExecute = $false; $start.WorkingDirectory = Split-Path -Parent $Executable
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Owned $process; throw 'Owned Velopack command timed out.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Await([scriptblock]$Condition, [int]$Seconds, [string]$Reason) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 100 } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    throw $Reason
}
$failure = $null
try {
    Require ((Run-Owned $release.SetupPath @('--silent', '--installto', $installRoot, '--log', (Join-Path $evidence 'setup.log'))) -eq 0) 'Official Setup.exe returned failure.'
    Require ((Test-Path -LiteralPath $expectedApp) -and (Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe'))) 'Official installer did not create the installed app and updater.'
    Require (Test-Path -LiteralPath $uninstallKey) 'Official installer did not register an uninstall entry.'
    $cases.Add(@{ name = 'official silent installer creates application and uninstall registration'; passed = $true })

    $stub = Join-Path $installRoot 'Game Session Overlay.exe'
    Require (Test-Path -LiteralPath $stub) 'Official stable execution stub was not created.'
    Require ((Run-Owned $stub @('--velopack-proof', $dataRoot, '--show') 30) -eq 0) 'Official execution stub returned failure.'
    $readyPath = Join-Path $dataRoot 'ready.json'
    Await { Test-Path -LiteralPath $readyPath } 30 'Published installed app did not confirm initialized startup.'
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    $apps = Owned-Apps
    Require ($apps.Count -eq 1) 'Installed execution did not leave exactly one app process.'
    $app = $apps[0]
    Require ($ready.pid -eq $app.Id -and $ready.started -eq $app.StartTime.ToUniversalTime().Ticks -and $ready.installed -eq $true -and
        $ready.version -ceq $release.Build.version -and $ready.buildId -ceq $release.Build.buildId -and $ready.commit -ceq $release.Build.commit -and
        [IO.Path]::GetFullPath($ready.root).TrimEnd('\', '/') -eq [IO.Path]::GetFullPath($installRoot).TrimEnd('\', '/')) 'Ready proof does not identify the exact installed application.'
    $cases.Add(@{ name = 'official stable stub starts exact installed WPF app with isolated ready proof'; passed = $true; pid = $app.Id; started = $ready.started; path = $app.Path })

    $settings = Join-Path $dataRoot 'settings.json'
    $settingsBytes = '{"Version":3,"UiScalePercent":115,"SoundFeedbackEnabled":true}'
    [IO.File]::WriteAllText($settings, $settingsBytes)
    $settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
    Stop-Owned $app
    Require ((Run-Owned (Join-Path $installRoot 'Update.exe') @('uninstall', '--silent', '--rootDir', $installRoot, '--log', (Join-Path $evidence 'uninstall.log')) 60) -eq 0) 'Official uninstaller returned failure.'
    Await { -not (Test-Path -LiteralPath $expectedApp) -and -not (Test-Path -LiteralPath $uninstallKey) } 30 'Official uninstall did not remove application files and registration.'
    Require ((Test-Path -LiteralPath $settings) -and (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'Uninstall modified isolated user settings.'
    $cases.Add(@{ name = 'official uninstaller removes application while retaining user settings'; passed = $true })
    Require ((Read-Value $runPath 'Bezoro.GameSessionOverlay') -eq $priorRun -and (Read-Value $settingsKey 'StartupConfigured') -eq $priorMarker) 'Proof modified user startup registration.'
    $cases.Add(@{ name = 'proof preserves hidden-startup user registration'; passed = $true })
} catch { $failure = $_.Exception.Message; Write-Error -Message $_ -ErrorAction Continue }
finally {
    foreach ($process in (Owned-Apps)) { Stop-Owned $process }
    if ((Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe')) -and (Test-Path -LiteralPath (Join-Path $installRoot 'current/sq.version'))) {
        try { Run-Owned (Join-Path $installRoot 'Update.exe') @('uninstall', '--silent', '--rootDir', $installRoot, '--log', (Join-Path $evidence 'cleanup.log')) 45 | Out-Null }
        catch { if (-not $failure) { $failure = $_.Exception.Message } }
    }
    if (Test-Path -LiteralPath $dataRoot) {
        $proofFiles = @(Get-ChildItem -LiteralPath $dataRoot -File -Recurse | Where-Object { $_.Length -le 2097152 -and $_.Extension -in @('.json', '.log') } | Select-Object -First 16)
        $index = 0
        foreach ($file in $proofFiles) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence ("proof-$index-" + $file.Name)); $index++ }
    }
    @{ total = $cases.Count + $(if ($failure) { 1 } else { 0 }); passed = $cases.Count; failed = $(if ($failure) { 1 } else { 0 }); error = $failure; version = $release.Build.version; cases = $cases } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    foreach ($directory in @($installRoot, $dataRoot)) { if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue } }
}
if ($failure) { exit 1 }
Write-Output "VELOPACK INSTALL SMOKE $($cases.Count)/$($cases.Count) passed"
exit 0
