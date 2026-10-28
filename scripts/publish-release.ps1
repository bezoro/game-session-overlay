[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Commit
)
$ErrorActionPreference = 'Stop'
if ($Repository -cne 'bezoro/apollo-tv-overlay' -or $Commit -notmatch '^[a-f0-9]{40}$') { throw 'Only the configured repository and an exact commit may publish updates.' }
if (-not $env:GH_TOKEN) { throw 'GitHub job credential is missing.' }
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'GameSessionOverlay-release' }
$api = "https://api.github.com/repos/$Repository"
$manifestPath = Join-Path $ArtifactDirectory 'overlay-release.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.commit -cne $Commit -or $manifest.channel -notin @('stable', 'testing') -or
    $manifest.buildId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$' -or $manifest.buildId.Contains('..') -or
    $manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Release identity failed validation.' }
$packageName = "GameSessionOverlay-$($manifest.buildId)-win-x64.zip"
if ($manifest.package.name -cne $packageName -or $manifest.package.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid package identity.' }
$packagePath = Join-Path $ArtifactDirectory $packageName
$package = Get-Item -LiteralPath $packagePath
if ($package.Length -ne $manifest.package.size -or (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.package.sha256) { throw 'Downloaded package differs from its release manifest.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $identityEntries = @($archive.Entries | Where-Object FullName -CEQ 'build-info.json')
    if ($identityEntries.Count -ne 1 -or $identityEntries[0].Length -gt 4096) { throw 'Package identity is missing, duplicated or too large.' }
    $reader = [IO.StreamReader]::new($identityEntries[0].Open())
    try { $identity = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    foreach ($field in @('version', 'buildId', 'commit', 'channel')) {
        if ($identity.$field -cne $manifest.$field) { throw "Package metadata differs from release metadata: $field" }
    }
} finally { $archive.Dispose() }

$tag = "v$($manifest.version)"
$release = $null
try { $release = Invoke-RestMethod -Method Get -Uri "$api/releases/tags/$([Uri]::EscapeDataString($tag))" -Headers $headers }
catch { if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw } }
$files = @($packagePath, $manifestPath)
if ($release -and -not $release.draft) {
    foreach ($file in $files) {
        $name = [IO.Path]::GetFileName($file)
        $asset = @($release.assets | Where-Object name -CEQ $name)
        $expected = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.Count -ne 1 -or $asset[0].digest -cne $expected) { throw 'Published releases are immutable; existing assets differ.' }
    }
    Write-Output "Release already published with identical assets: $tag"
    return
}
if (-not $release) {
    $summary = ([string]$env:RELEASE_NOTES).Trim()
    if ($summary.Length -gt 4000) { $summary = $summary.Substring(0, 4000) }
    if (-not $summary) { $summary = 'Atualização gerada após aprovação das verificações do Windows.' }
    $payload = @{
        tag_name = $tag; target_commitish = $Commit; name = "Game Session Overlay $($manifest.version)";
        draft = $true; prerelease = ($manifest.channel -eq 'testing'); make_latest = 'false';
        body = "$summary`n`nBuild: $($manifest.buildId). Canal: $($manifest.channel). Commit: $Commit.`nVerificações do Windows aprovadas antes da publicação. As versões anteriores continuam disponíveis para retorno."
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -Body $payload
} elseif ($release.target_commitish -cne $Commit) { throw 'Existing draft targets a different source commit.' }

foreach ($file in $files) {
    $name = [IO.Path]::GetFileName($file)
    $expected = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $existing = @($release.assets | Where-Object name -CEQ $name)
    if ($existing.Count -gt 0) {
        if ($existing.Count -ne 1 -or $existing[0].digest -cne $expected) { throw 'Existing draft asset differs; no asset was overwritten.' }
        continue
    }
    $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
    $type = if ($name.EndsWith('.zip', [StringComparison]::Ordinal)) { 'application/zip' } else { 'application/json' }
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $type -InFile $file
    if ($asset.digest -cne $expected) { throw 'GitHub reported a different asset digest; draft remains unpublished.' }
}
$payload = @{ draft = $false; make_latest = $(if ($manifest.channel -eq 'stable') { 'true' } else { 'false' }) } | ConvertTo-Json
$published = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json' -Body $payload
Write-Output "Published immutable update: $($published.html_url)"
