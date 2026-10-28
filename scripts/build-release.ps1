[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

$version = if ($env:OVERLAY_VERSION) { $env:OVERLAY_VERSION } else { '0.2.0-dev+local' }
$buildId = if ($env:OVERLAY_BUILD_ID) { $env:OVERLAY_BUILD_ID } else { 'local' }
$commit = if ($env:OVERLAY_COMMIT) { $env:OVERLAY_COMMIT } else { 'unknown' }
$channel = if ($env:OVERLAY_CHANNEL) { $env:OVERLAY_CHANNEL } else { 'testing' }
if ($buildId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$' -or $buildId.Contains('..')) { throw 'Invalid build identity.' }
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Invalid application version.' }
if ($channel -notin @('stable', 'testing')) { throw 'Unknown release channel.' }
if ($commit -ne 'unknown' -and $commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid source commit.' }

$publishRoot = (Get-Item -LiteralPath $PublishDirectory).FullName
$application = Join-Path $publishRoot 'ApolloOverlay.dll'
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw 'Published application is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'ApolloOverlay.exe') -PathType Leaf)) { throw 'Published application host is missing.' }
$compiledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($application).ProductVersion
if ($compiledVersion -ne $version) { throw "Compiled version differs from the package identity: $compiledVersion / $version" }
$stream = [IO.File]::OpenRead($application)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $compiled = @{}
    foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
        $attribute = $metadata.GetCustomAttribute($handle)
        if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
        $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
        if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
        $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
        if ($metadata.GetString($type.Namespace) -ne 'System.Reflection' -or $metadata.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }
        $blob = $metadata.GetBlobReader($attribute.Value)
        if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata encoding.' }
        $key = $blob.ReadSerializedString()
        $compiled[$key] = $blob.ReadSerializedString()
    }
    if ($compiled.OverlayBuildId -cne $buildId -or $compiled.OverlayCommit -cne $commit -or $compiled.OverlayChannel -cne $channel) { throw 'Compiled build, commit or channel differs from the package identity.' }
} finally { $pe.Dispose(); $stream.Dispose() }

$identity = [ordered]@{ schemaVersion = 1; version = $version; buildId = $buildId; commit = $commit; channel = $channel }
$identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishRoot 'build-info.json') -Encoding utf8NoBOM
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) { throw 'Velopack output must be a fresh directory; existing releases are never overwritten.' }
} else { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$notes = ([string]$env:RELEASE_NOTES).Trim()
if ($notes.Length -gt 4000) { $notes = $notes.Substring(0, 4000) }
if (-not $notes) { $notes = 'Atualização validada automaticamente no Windows.' }
$notes = [Regex]::Replace($notes, '(?m)^[ \t]*(Version|Build|Commit|Channel):', '> $1:')
$notes += "`n`nVersion: $version`nBuild: $buildId`nCommit: $commit`nChannel: $channel`n"
$notesPath = Join-Path $OutputDirectory 'release-notes.md'
[IO.File]::WriteAllText($notesPath, $notes)
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$packageVersion = $version.Split('+')[0]
$icon = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/ApolloOverlay/Assets/overlay-controller.ico'
$arguments = @('tool', 'run', 'vpk', '--', '[win]', 'pack', '--packId', 'GameSessionOverlay', '--packVersion', $packageVersion,
    '--packDir', $publishRoot, '--mainExe', 'ApolloOverlay.exe', '--packTitle', 'Game Session Overlay', '--packAuthors', 'Bezoro',
    '--channel', $channel, '--runtime', 'win-x64', '--outputDir', $OutputDirectory, '--icon', $icon, '--shortcuts', 'StartMenuRoot', '--releaseNotes', $notesPath,
    '--noPortable', '--delta', 'None', '--skip-updates')
if ($IsWindows) { $arguments += @('--instLocation', 'PerUser') }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Official Velopack packaging failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $publishRoot 'build-info.json') -Destination (Join-Path $OutputDirectory 'build-info.json')
$verified = & (Join-Path $PSScriptRoot 'get-validated-velopack-release.ps1') -ArtifactDirectory $OutputDirectory -ExpectedVersion $version -ExpectedCommit $commit
Write-Output "Official Velopack package verified: $([IO.Path]::GetFileName($verified.SetupPath)); version=$version; build=$buildId; channel=$channel"
