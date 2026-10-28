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
$variantRoot = Join-Path $env:RUNNER_TEMP "overlay-velopack-variant-$proofId"
if ((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $dataRoot) -or (Test-Path -LiteralPath $variantRoot)) { throw 'Proof directories must be fresh.' }
$originalEnvironment = @{}
foreach ($name in @('OVERLAY_VERSION', 'OVERLAY_BUILD_ID', 'OVERLAY_COMMIT', 'OVERLAY_CHANNEL', 'RELEASE_NOTES')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
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
        try { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith([IO.Path]::GetFullPath($installRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}
function Stop-Owned([Diagnostics.Process]$Process) {
    $path = $Process.Path; $created = $Process.StartTime.ToUniversalTime().Ticks
    $live = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if (-not $live) { $script:lastStopMethod = 'already exited'; return }
    Require ($live.Path -eq $path -and $live.StartTime.ToUniversalTime().Ticks -eq $created) 'Owned process identity changed; cleanup refused.'
    if ($live.CloseMainWindow() -and $live.WaitForExit(5000)) { $script:lastStopMethod = 'WM_CLOSE'; return }
    Stop-Process -InputObject $live -Force
    Require ($live.WaitForExit(10000)) 'Owned proof process did not exit.'
    $script:lastStopMethod = 'exact owned PID terminated'
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
function Restore-BuildEnvironment {
    foreach ($name in $originalEnvironment.Keys) {
        if ($null -eq $originalEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
    }
}
function Package-DllHash([string]$Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = @($archive.Entries | Where-Object FullName -CEQ 'lib/app/ApolloOverlay.dll')
        Require ($entry.Count -eq 1 -and $entry[0].Length -le 16777216) 'Proof package lacks its exact app assembly.'
        $assemblyStream = $entry[0].Open(); $hash = [Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToHexString($hash.ComputeHash($assemblyStream)) } finally { $assemblyStream.Dispose(); $hash.Dispose() }
    } finally { $archive.Dispose() }
}
function Verify-History($Selected) {
    $directory = Join-Path (Join-Path $dataRoot 'rollback-packages') $Selected.Build.buildId
    $record = Get-Content -LiteralPath (Join-Path $directory 'release.json') -Raw | ConvertFrom-Json
    Require ($record.build.buildId -ceq $Selected.Build.buildId -and $record.build.version -ceq $Selected.Build.version -and
        $record.build.commit -ceq $Selected.Build.commit -and $null -ne $record.installedAt) 'Installed build was not retained in local rollback history.'
    Require ([IO.Path]::GetFileName($record.asset.fileName) -ceq $record.asset.fileName) 'Invalid history package filename.'
    Require ((Get-FileHash -LiteralPath (Join-Path $directory $record.asset.fileName) -Algorithm SHA256).Hash -ieq $Selected.Asset.SHA256) 'Retained rollback package differs from its official feed.'
}
function Verify-PackagedCli($Selected, [Diagnostics.Process]$App, $Ready, [string]$Label) {
    $cliPath = Join-Path $installRoot 'current/ApolloOverlay.Cli.exe'
    Require ((Test-Path -LiteralPath $cliPath) -and (Test-Path -LiteralPath (Join-Path $installRoot 'current/gso.cmd'))) 'Installed package lacks its CLI or gso wrapper.'
    $watch = [Diagnostics.Stopwatch]::StartNew(); $lastReply = $null
    do {
        $start = [Diagnostics.ProcessStartInfo]::new($cliPath); $start.UseShellExecute = $false; $start.WorkingDirectory = Split-Path -Parent $cliPath
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.ArgumentList.Add('version')
        $client = [Diagnostics.Process]::Start($start)
        try {
            $clientSession = $client.SessionId
            $stdout = $client.StandardOutput.ReadToEndAsync(); $stderr = $client.StandardError.ReadToEndAsync()
            if (-not $client.WaitForExit(12000)) { Stop-Owned $client; throw 'Installed CLI version query timed out.' }
            $output = $stdout.GetAwaiter().GetResult(); $errorOutput = $stderr.GetAwaiter().GetResult()
            Require ($output.Length -le 2097152) 'Installed CLI response exceeded the proof bound.'
            if ($errorOutput) { [IO.File]::WriteAllText((Join-Path $evidence "cli-$Label-stderr.log"), $errorOutput.Substring(0, [Math]::Min(16384, $errorOutput.Length))) }
            if ($output) {
                [IO.File]::WriteAllText((Join-Path $evidence "cli-$Label-version.json"), $output)
                $lastReply = $output | ConvertFrom-Json
                if ($lastReply.Outcome -eq 'Completed') {
                    Require ($client.ExitCode -eq 0 -and $lastReply.Pid -eq $App.Id -and $lastReply.Started -eq $Ready.started -and
                        $lastReply.Session -eq $App.SessionId -and $clientSession -eq $App.SessionId -and -not [string]::IsNullOrWhiteSpace($lastReply.Instance)) 'Installed CLI receipt does not identify the exact live WPF instance and session.'
                    Require ($lastReply.Update.Version -ceq $Selected.Build.version -and $lastReply.Update.BuildId -ceq $Selected.Build.buildId -and
                        $lastReply.Update.Commit -ceq $Selected.Build.commit -and $lastReply.Update.Channel -ceq $Selected.Build.channel -and
                        $lastReply.Update.Installed -eq $true) 'Installed CLI reported a different running build or installation.'
                    return $lastReply
                }
            }
        } finally { $client.Dispose() }
        Start-Sleep -Milliseconds 200
    } while ($watch.Elapsed.TotalSeconds -lt 30)
    throw "Installed CLI did not return a fresh Completed version receipt: $($lastReply.Outcome) $($lastReply.Reason)"
}
function Start-VerifiedApp($Selected, [string]$ExpectedDllHash, [string]$Label, [bool]$StartStub = $true) {
    $readyPath = Join-Path $dataRoot 'ready.json'
    if ($StartStub) {
        if (Test-Path -LiteralPath $readyPath) { Remove-Item -LiteralPath $readyPath -Force }
        Require ((Run-Owned (Join-Path $installRoot 'Game Session Overlay.exe') @('--velopack-proof', $dataRoot, '--show') 30) -eq 0) 'Official execution stub returned failure.'
    }
    Await { Test-Path -LiteralPath $readyPath } 45 'Published installed app did not confirm initialized startup.'
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    $apps = Owned-Apps
    Require ($apps.Count -eq 1) 'Installed execution did not leave exactly one app process.'
    $app = $apps[0]
    Require ($app.Path -eq $expectedApp -and $ready.pid -eq $app.Id -and $ready.started -eq $app.StartTime.ToUniversalTime().Ticks -and $ready.installed -eq $true -and
        $ready.version -ceq $Selected.Build.version -and $ready.buildId -ceq $Selected.Build.buildId -and $ready.commit -ceq $Selected.Build.commit -and
        [IO.Path]::GetFullPath($ready.root).TrimEnd('\', '/') -eq [IO.Path]::GetFullPath($installRoot).TrimEnd('\', '/')) 'Ready proof does not identify the exact installed application.'
    [xml]$spec = Get-Content -LiteralPath (Join-Path $installRoot 'current/sq.version') -Raw
    Require ($spec.package.metadata.version -ceq $Selected.PackageVersion) 'Installed SDK specification has the wrong version.'
    Require ((Get-FileHash -LiteralPath (Join-Path $installRoot 'current/ApolloOverlay.dll') -Algorithm SHA256).Hash -eq $ExpectedDllHash) 'Installed executable payload differs from its official package.'
    Verify-History $Selected
    $cli = Verify-PackagedCli $Selected $app $ready $Label
    Copy-Item -LiteralPath $readyPath -Destination (Join-Path $evidence "ready-$Label.json")
    return [pscustomobject]@{ Process = $app; Ready = $ready; DllHash = $ExpectedDllHash; Cli = $cli }
}
function Apply-NativePackage($Selected, [Diagnostics.Process]$Running, [string]$PriorDllHash, [string]$Label) {
    $cache = Join-Path $installRoot 'packages'
    $target = Join-Path $cache "GameSessionOverlay-$($Selected.PackageVersion)-full.nupkg"
    Copy-Item -LiteralPath $Selected.PackagePath -Destination $target -Force
    Require ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $Selected.Asset.SHA256) 'Official package was not staged intact in the SDK cache.'
    $oldPid = $Running.Id; $oldCreated = $Running.StartTime.ToUniversalTime().Ticks
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $installRoot 'Update.exe')); $start.UseShellExecute = $false; $start.WorkingDirectory = $installRoot
    foreach ($argument in @('apply', '--package', $target, '--rootDir', $installRoot, '--waitPid', [string]$oldPid, '--silent', '--norestart', '--log', (Join-Path $evidence "$Label.log"))) { $start.ArgumentList.Add($argument) }
    $updater = [Diagnostics.Process]::Start($start)
    try {
        Require (-not $updater.WaitForExit(600)) 'Official updater exited before the exact running app was closed.'
        $stillRunning = Get-Process -Id $oldPid -ErrorAction Stop
        Require ($stillRunning.Path -eq $Running.Path -and $stillRunning.StartTime.ToUniversalTime().Ticks -eq $oldCreated -and
            (Get-FileHash -LiteralPath (Join-Path $installRoot 'current/ApolloOverlay.dll') -Algorithm SHA256).Hash -eq $PriorDllHash) 'App identity or payload changed before the wait target exited.'
        Stop-Owned $stillRunning
        $closeMethod = $script:lastStopMethod
        if (-not $updater.WaitForExit(60000)) { Stop-Owned $updater; throw 'Official update application timed out.' }
        Require ($updater.ExitCode -eq 0) 'Official updater returned failure.'
        Require ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $Selected.Asset.SHA256) 'Applied package is missing or altered in the SDK cache.'
        return [pscustomobject]@{ WaitedForPid = $oldPid; WaitedForStarted = $oldCreated; CloseMethod = $closeMethod; CachePath = $target }
    } finally { $updater.Dispose() }
}
function Rollback-PackagedCli($Selected, $Running) {
    $readyPath = Join-Path $dataRoot 'ready.json'
    if (Test-Path -LiteralPath $readyPath) { Remove-Item -LiteralPath $readyPath -Force }
    $cliPath = Join-Path $installRoot 'current/ApolloOverlay.Cli.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($cliPath); $start.UseShellExecute = $false; $start.WorkingDirectory = Split-Path -Parent $cliPath
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('rollback', $Selected.Build.buildId, '--timeout', '90')) { $start.ArgumentList.Add($argument) }
    $client = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $client.StandardOutput.ReadToEndAsync(); $stderr = $client.StandardError.ReadToEndAsync()
        if (-not $client.WaitForExit(100000)) { Stop-Owned $client; throw 'Installed CLI rollback timed out; the mutation will not be repeated.' }
        $output = $stdout.GetAwaiter().GetResult(); $errorOutput = $stderr.GetAwaiter().GetResult()
        Require ($output.Length -le 2097152) 'Installed CLI rollback response exceeded the proof bound.'
        [IO.File]::WriteAllText((Join-Path $evidence 'cli-rollback-B-to-A.json'), $output)
        if ($errorOutput) { [IO.File]::WriteAllText((Join-Path $evidence 'cli-rollback-stderr.log'), $errorOutput.Substring(0, [Math]::Min(16384, $errorOutput.Length))) }
        $reply = $output | ConvertFrom-Json
        Require ($client.ExitCode -eq 0 -and $reply.Outcome -ceq 'Requested' -and $reply.Terminal -eq $false) "Installed CLI did not confirm a rollback request: $($reply.Outcome) $($reply.Reason)"
        Require ($reply.Pid -eq $Running.Process.Id -and $reply.Started -eq $Running.Ready.started -and $reply.Session -eq $Running.Cli.Session -and
            $reply.Update.TargetBuildId -ceq $Selected.Build.buildId -and $reply.Update.TargetVersion -ceq $Selected.Build.version) 'Rollback receipt does not identify the exact old instance and requested A build.'
        $oldInstance = Get-Process -Id $Running.Process.Id -ErrorAction SilentlyContinue
        Require (-not $oldInstance -or $oldInstance.StartTime.ToUniversalTime().Ticks -ne $Running.Ready.started) 'CLI returned Requested before the exact old GUI exited.'
        return $reply
    } finally { $client.Dispose() }
}
$failure = $null
try {
    Require ((Run-Owned $release.SetupPath @('--silent', '--installto', $installRoot, '--log', (Join-Path $evidence 'setup.log'))) -eq 0) 'Official Setup.exe returned failure.'
    Require ((Test-Path -LiteralPath $expectedApp) -and (Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe'))) 'Official installer did not create the installed app and updater.'
    Require (Test-Path -LiteralPath $uninstallKey) 'Official installer did not register an uninstall entry.'
    $cases.Add(@{ name = 'official silent installer creates application and uninstall registration'; passed = $true })

    $hashA = Package-DllHash $release.PackagePath
    $initial = Start-VerifiedApp $release $hashA 'A-initial'
    $app = $initial.Process
    $cases.Add(@{ name = 'official stable stub starts exact installed WPF app with isolated ready proof'; passed = $true; pid = $app.Id; started = $initial.Ready.started; path = $app.Path; dllSha256 = $hashA })

    $settings = Join-Path $dataRoot 'settings.json'
    $settingsBytes = '{"Version":3,"UiScalePercent":115,"SoundFeedbackEnabled":true}'
    [IO.File]::WriteAllText($settings, $settingsBytes)
    $settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash

    # This is a separate real WPF build of the same source with distinct compiled metadata.
    # Its runtime and all output packages stay in RUNNER_TEMP, never in the published artifacts.
    Require ($env:GITHUB_RUN_NUMBER -match '^[0-9]+$' -and $env:GITHUB_RUN_ATTEMPT -match '^[0-9]+$') 'Missing CI identity for the temporary proof version.'
    $majorA = [int]$release.PackageVersion.Split('.')[0]
    Require ($majorA -lt 2147483646) 'Version major exceeds the proof bound.'
    $versionB = "$($majorA + 1).0.0-proof.$($env:GITHUB_RUN_NUMBER).$($env:GITHUB_RUN_ATTEMPT)+$($release.Build.commit.Substring(0, 7))"
    $buildB = "proof-$proofId"
    $publishB = Join-Path $variantRoot 'publish'
    $releasesB = Join-Path $variantRoot 'releases'
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    try {
        $env:OVERLAY_VERSION = $versionB; $env:OVERLAY_BUILD_ID = $buildB
        $env:OVERLAY_COMMIT = $release.Build.commit; $env:OVERLAY_CHANNEL = $release.Build.channel
        $env:RELEASE_NOTES = 'CI-only WPF update/rollback proof. Never publish this temporary fixture.'
        & $dotnet publish (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/ApolloOverlay/ApolloOverlay.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $publishB 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'variant-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Temporary self-contained WPF build failed.' }
        & (Join-Path $PSScriptRoot 'verify.ps1') -Stage CliPackage -CliDestination $publishB |
            Tee-Object -FilePath (Join-Path $evidence 'variant-cli-package.log')
        if ($LASTEXITCODE -ne 0) { throw 'Temporary CLI packaging failed.' }
        & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publishB -OutputDirectory $releasesB |
            Tee-Object -FilePath (Join-Path $evidence 'variant-package.log')
        $variant = & (Join-Path $PSScriptRoot 'get-validated-velopack-release.ps1') -ArtifactDirectory $releasesB -ExpectedVersion $versionB -ExpectedCommit $release.Build.commit
    } finally { Restore-BuildEnvironment }
    $hashB = Package-DllHash $variant.PackagePath
    Require ($hashB -ne $hashA -and $variant.Build.buildId -cne $release.Build.buildId -and $variant.PackageVersion -cne $release.PackageVersion) 'Proof variant is not a distinct executable payload and package version.'
    Require ((Get-FileHash -LiteralPath (Join-Path $installRoot 'current/ApolloOverlay.dll') -Algorithm SHA256).Hash -eq $hashA) 'Compiling the temporary variant altered the installed A payload.'
    $cases.Add(@{ name = 'temporary self-contained WPF B has distinct compiled identity and payload'; passed = $true; version = $variant.Build.version; buildId = $variant.Build.buildId; dllSha256 = $hashB })

    $upgrade = Apply-NativePackage $variant $app $hashA 'upgrade-A-to-B'
    $upgraded = Start-VerifiedApp $variant $hashB 'B-upgraded'
    Require (-not ($upgraded.Process.Id -eq $app.Id -and $upgraded.Ready.started -eq $initial.Ready.started)) 'Upgrade retained the old process identity.'
    Verify-History $release
    Require ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'Upgrade altered user settings.'
    $cases.Add(@{ name = 'official native updater waits for exact A PID and applies B; stable stub starts verified B'; passed = $true; pid = $upgraded.Process.Id; started = $upgraded.Ready.started; wait = $upgrade; version = $variant.Build.version; dllSha256 = $hashB; settingsSha256 = $settingsHash })

    $rollback = Rollback-PackagedCli $release $upgraded
    $restored = Start-VerifiedApp $release $hashA 'A-restored' $false
    Require (-not ($restored.Process.Id -eq $upgraded.Process.Id -and $restored.Ready.started -eq $upgraded.Ready.started)) 'Rollback retained the B process identity.'
    Verify-History $variant
    Require ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'CLI rollback altered user settings.'
    Require ((Get-FileHash -LiteralPath $release.PackagePath -Algorithm SHA256).Hash -ieq $release.Asset.SHA256) 'Proof changed the actual published A package.'
    $cases.Add(@{ name = 'packaged CLI rollback requests exact A; application SDK flow applies and restarts verified A with history and settings preserved'; passed = $true; pid = $restored.Process.Id; started = $restored.Ready.started; requestedOutcome = $rollback.Outcome; requestedBuildId = $rollback.Update.TargetBuildId; version = $release.Build.version; dllSha256 = $hashA; settingsSha256 = $settingsHash })
    $cases.Add(@{ name = 'packaged CLI version obtains fresh exact WPF identity before upgrade and after both replacements'; passed = $true;
        initial = @{ pid = $initial.Cli.Pid; started = $initial.Cli.Started; session = $initial.Cli.Session; instance = $initial.Cli.Instance; version = $initial.Cli.Update.Version; buildId = $initial.Cli.Update.BuildId };
        upgraded = @{ pid = $upgraded.Cli.Pid; started = $upgraded.Cli.Started; session = $upgraded.Cli.Session; instance = $upgraded.Cli.Instance; version = $upgraded.Cli.Update.Version; buildId = $upgraded.Cli.Update.BuildId };
        restored = @{ pid = $restored.Cli.Pid; started = $restored.Cli.Started; session = $restored.Cli.Session; instance = $restored.Cli.Instance; version = $restored.Cli.Update.Version; buildId = $restored.Cli.Update.BuildId } })
    Stop-Owned $restored.Process
    Require ((Run-Owned (Join-Path $installRoot 'Update.exe') @('uninstall', '--silent', '--rootDir', $installRoot, '--log', (Join-Path $evidence 'uninstall.log')) 60) -eq 0) 'Official uninstaller returned failure.'
    Await { -not (Test-Path -LiteralPath $expectedApp) -and -not (Test-Path -LiteralPath $uninstallKey) } 30 'Official uninstall did not remove application files and registration.'
    Require ((Test-Path -LiteralPath $settings) -and (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'Uninstall modified isolated user settings.'
    $cases.Add(@{ name = 'official uninstaller removes application while retaining user settings'; passed = $true })
    Require ((Read-Value $runPath 'Bezoro.GameSessionOverlay') -eq $priorRun -and (Read-Value $settingsKey 'StartupConfigured') -eq $priorMarker) 'Proof modified user startup registration.'
    $cases.Add(@{ name = 'proof preserves hidden-startup user registration'; passed = $true })
} catch { $failure = $_.Exception.Message; Write-Error -Message $_ -ErrorAction Continue }
finally {
    Restore-BuildEnvironment
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
    foreach ($directory in @($installRoot, $dataRoot, $variantRoot)) { if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue } }
}
if ($failure) { exit 1 }
Write-Output "VELOPACK INSTALL SMOKE $($cases.Count)/$($cases.Count) passed"
exit 0
