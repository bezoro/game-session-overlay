[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP -or -not $IsWindows) {
    throw 'This smoke test runs only on an isolated Windows GitHub Actions runner.'
}
$published = (Resolve-Path -LiteralPath $PublishPath).Path
$reportPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$proofId = [Guid]::NewGuid().ToString('N')
$proofRoot = Join-Path $env:RUNNER_TEMP "overlay-install-proof-$proofId"
$candidate = Join-Path $env:RUNNER_TEMP "overlay-update-candidate-$proofId"
if ((Test-Path -LiteralPath $proofRoot) -or (Test-Path -LiteralPath $candidate)) { throw 'Proof directories must be fresh.' }
$priorProof = $env:BEZORO_INSTALL_PROOF_ROOT
$env:BEZORO_INSTALL_PROOF_ROOT = $proofRoot
$exe = Join-Path $published 'ApolloOverlay.exe'
$build = Get-Content -LiteralPath (Join-Path $published 'build-info.json') -Raw | ConvertFrom-Json
$cases = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
$activePid = $null
$activeCreated = $null
$activePath = $null
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsKey = 'HKCU:\Software\Bezoro\GameSessionOverlay'
function Read-RegistryValue([string]$Path, [string]$Name) {
    $value = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($value) { return $value.$Name }; return $null
}
$priorRun = Read-RegistryValue $runPath 'Bezoro.GameSessionOverlay'
$priorMarker = Read-RegistryValue $settingsKey 'StartupConfigured'
function Require([bool]$Condition, [string]$Reason) { if (-not $Condition) { throw $Reason } }
function Read-State {
    $state = Join-Path $proofRoot 'install-state.json'
    if (Test-Path -LiteralPath $state) { return Get-Content -LiteralPath $state -Raw | ConvertFrom-Json }; return $null
}
function Owned-Apps {
    $prefix = (Join-Path $proofRoot 'versions') + [IO.Path]::DirectorySeparatorChar
    return @(Get-Process -Name ApolloOverlay -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}
function Stop-Owned([Diagnostics.Process]$Process) {
    $pidValue = $Process.Id
    $expectedPath = $Process.Path
    $expectedCreation = $Process.StartTime.ToUniversalTime().Ticks
    $live = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($live) {
        Require ($live.Path -eq $expectedPath -and $live.StartTime.ToUniversalTime().Ticks -eq $expectedCreation) 'Owned process identity changed; refusing cleanup.'
        Stop-Process -InputObject $live -Force
        if (-not $live.WaitForExit(10000)) { throw 'Owned child did not exit.' }
    }
}
function Run-Owned([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $Executable
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Owned $process; throw 'Owned command timed out.' }
    $exit = $process.ExitCode
    $process.Dispose()
    return $exit
}
function Await([scriptblock]$Condition, [int]$Seconds, [string]$Reason) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    throw $Reason
}
try {
    New-Item -ItemType Directory -Path (Join-Path $proofRoot 'user-data') -Force | Out-Null
    $settings = Join-Path $proofRoot 'user-data/settings.json'
    $originalSettings = '{"Version":3,"SoundFeedbackEnabled":false,"UiScalePercent":115,"GuideXInputEnabled":false}'
    [IO.File]::WriteAllText($settings, $originalSettings)
    $originalSettingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
    Require ((Run-Owned $exe @('--install')) -eq 0) 'Published --install returned failure.'
    $state = Read-State
    Require ($state.current -eq $build.buildId -and -not $state.pending) 'Initial install did not commit after health.'
    $apps = Owned-Apps
    Require ($apps.Count -eq 1) 'Initial install did not leave exactly one managed child.'
    $activePid = $apps[0].Id; $activeCreated = $apps[0].StartTime.ToUniversalTime().Ticks; $activePath = $apps[0].Path
    Require ($activePath -eq (Join-Path $proofRoot "versions/$($build.buildId)/ApolloOverlay.exe")) 'App did not run from immutable version directory.'
    Require ((Test-Path -LiteralPath (Join-Path $proofRoot 'bootstrap-files.json'))) 'Bootstrap inventory was not finalized.'
    Require ((Test-Path -LiteralPath (Join-Path $proofRoot 'user-data/automation'))) 'Published app diagnostics were not isolated to the proof root.'
    $cases.Add(@{ name = 'published install and actual managed readiness'; passed = $true; pid = $activePid; started = $activeCreated; path = $activePath })

    Require ((Run-Owned (Join-Path $proofRoot 'ApolloOverlay.exe') @('--startup') 15) -eq 0) 'Duplicate bootstrap invocation failed.'
    $same = Get-Process -Id $activePid -ErrorAction Stop
    Require ($same.Path -eq $activePath -and $same.StartTime.ToUniversalTime().Ticks -eq $activeCreated -and (Read-State).current -eq $build.buildId) 'Duplicate startup changed the live process or active pointer.'
    $cases.Add(@{ name = 'second bootstrap keeps exact live instance and active pointer'; passed = $true })

    Stop-Owned $same
    Copy-Item -LiteralPath $published -Destination $candidate -Recurse
    $bad = Get-Content -LiteralPath (Join-Path $candidate 'build-info.json') -Raw | ConvertFrom-Json
    $bad.buildId = $build.buildId + '-rejected'
    $bad.version = $build.version + '.rejected'
    [IO.File]::WriteAllText((Join-Path $candidate 'build-info.json'), ($bad | ConvertTo-Json))
    Require ((Run-Owned $exe @('--managed-proof-apply', $candidate) 20) -eq 0) 'Transition helper was not acknowledged before source exit.'
    Await {
        $state = Read-State
        $plans = @(Get-ChildItem -LiteralPath (Join-Path $proofRoot 'pending') -Filter '*.json' -ErrorAction SilentlyContinue)
        $running = Owned-Apps
        if (-not ($state -and $state.current -eq $build.buildId -and -not $state.pending -and $plans.Count -eq 0 -and $running.Count -eq 1)) { return $false }
        try {
            $released = [IO.File]::Open((Join-Path $proofRoot 'install.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $released.Dispose(); return $true
        } catch [IO.IOException] { return $false }
    } 75 'Failed candidate did not restore the actual prior process and pointer.'
    $state = Read-State
    Require (@($state.builds | Where-Object buildId -eq $bad.buildId).Count -eq 1) 'Rejected version was removed from rollback history/evidence.'
    Require (@($state.builds | Where-Object { $_.buildId -eq $bad.buildId -and $_.lastStartFailed }).Count -eq 1) 'Rejected version was offered as a healthy rollback choice.'
    Require ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $originalSettingsHash) 'Health failure did not preserve exact prior settings bytes.'
    Require ((Get-FileHash -LiteralPath (Join-Path $proofRoot "versions/$($build.buildId)/ApolloOverlay.dll") -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $published 'ApolloOverlay.dll') -Algorithm SHA256).Hash) 'Prior executable payload changed.'
    $restored = (Owned-Apps)[0]
    $cases.Add(@{ name = 'candidate embedded identity rejection restores prior real app and exact settings'; passed = $true; restoredPid = $restored.Id; restoredPath = $restored.Path; rejectedBuild = $bad.buildId })

    Require ((Read-RegistryValue $runPath 'Bezoro.GameSessionOverlay') -eq $priorRun) 'Smoke altered the user startup Run value.'
    Require ((Read-RegistryValue $settingsKey 'StartupConfigured') -eq $priorMarker) 'Smoke altered the user startup marker.'
    $cases.Add(@{ name = 'isolated smoke preserves user startup registration'; passed = $true })
    @{ total = $cases.Count; passed = $cases.Count; failed = 0; started = $started; finished = [DateTimeOffset]::UtcNow; build = $build; root = $proofRoot; cases = $cases } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "INSTALLED UPDATE SMOKE $($cases.Count)/$($cases.Count) passed; actual managed startup and automatic restoration verified."
} catch {
    @{ total = $cases.Count + 1; passed = $cases.Count; failed = 1; error = $_.Exception.Message; root = $proofRoot; cases = $cases } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Error -Message $_ -ErrorAction Continue
    exit 1
} finally {
    foreach ($process in (Owned-Apps)) { Stop-Owned $process }
    $evidence = $reportPath + '.evidence'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    if (Test-Path -LiteralPath $proofRoot) {
        $retained = @(Get-ChildItem -LiteralPath $proofRoot -File -ErrorAction SilentlyContinue | Where-Object Name -In @('install-state.json','install-state.json.previous','last-update.txt','install-proof.log'))
        $data = Join-Path $proofRoot 'user-data'
        if (Test-Path -LiteralPath $data) {
            $retained += @(Get-ChildItem -LiteralPath $data -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.log','.json','.txt') } | Select-Object -First 20)
        }
        foreach ($file in $retained) {
            if ($file.Length -le 2MB -and -not ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                $relative = [IO.Path]::GetRelativePath($proofRoot,$file.FullName)
                $destination = Join-Path $evidence $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $destination
            }
        }
    }
    $env:BEZORO_INSTALL_PROOF_ROOT = $priorProof
    foreach ($directory in @($proofRoot, $candidate)) {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    }
}
exit 0
