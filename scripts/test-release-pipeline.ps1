[CmdletBinding()]
param([string]$ApplicationDirectory = 'src/ApolloOverlay/bin/Release/net10.0-windows')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('OverlayReleaseTests-' + [Guid]::NewGuid().ToString('N'))
$failed = 0; $passed = 0
function Require([bool]$Condition, [string]$Reason) { if (-not $Condition) { throw $Reason } }
function Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Output "PASS release $Name" }
    catch { $script:failed++; Write-Error "FAIL release $Name`: $($_.Exception.Message)" -ErrorAction Continue }
}
function MustFail([scriptblock]$Body, [string]$Reason) { $caught = $false; try { & $Body | Out-Null } catch { $caught = $true }; Require $caught $Reason }
$oldCredential = $env:GH_TOKEN
$oldBuildId = $env:OVERLAY_BUILD_ID
$oldVersion = $env:OVERLAY_VERSION
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    Case 'PowerShell sources parse' {
        foreach ($source in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
            $tokens = $null; $errors = $null
            [Management.Automation.Language.Parser]::ParseFile($source.FullName, [ref]$tokens, [ref]$errors) | Out-Null
            Require ($errors.Count -eq 0) "Parse failure in $($source.Name): $errors"
        }
    }
    $publish = Join-Path $temporary 'publish'; $output = Join-Path $temporary 'output'
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    $appRoot = [IO.Path]::GetFullPath($ApplicationDirectory, $taskRoot)
    Copy-Item -LiteralPath (Join-Path $appRoot 'ApolloOverlay.dll') -Destination $publish
    $hostPath = Join-Path $appRoot 'ApolloOverlay.exe'
    if (-not (Test-Path -LiteralPath $hostPath)) { $hostPath = Join-Path $appRoot 'ApolloOverlay' }
    Copy-Item -LiteralPath $hostPath -Destination (Join-Path $publish 'ApolloOverlay.exe')
    Case 'package metadata matches compiled version and archive hash' {
        & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publish -OutputDirectory $output | Out-Null
        $manifest = Get-Content -LiteralPath (Join-Path $output 'overlay-release.json') -Raw | ConvertFrom-Json
        $identity = Get-Content -LiteralPath (Join-Path $publish 'build-info.json') -Raw | ConvertFrom-Json
        foreach ($field in @('version', 'buildId', 'commit', 'channel')) { Require ($manifest.$field -ceq $identity.$field) "Identity mismatch: $field" }
        Require ($manifest.version -ceq [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'ApolloOverlay.dll')).ProductVersion) 'Version is not the compiled version'
        $zip = Join-Path $output $manifest.package.name
        Require ((Get-Item -LiteralPath $zip).Length -eq $manifest.package.size) 'Archive size differs'
        Require ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $manifest.package.sha256) 'Archive digest differs'
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try { Require (@($archive.Entries | Where-Object FullName -CEQ 'build-info.json').Count -eq 1) 'Archive lacks root identity' } finally { $archive.Dispose() }
    }
    Case 'packaging never overwrites an existing build' {
        MustFail { & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publish -OutputDirectory $output } 'Existing build was overwritten'
    }
    Case 'unsafe build identities fail before writing release files' {
        try {
            $env:OVERLAY_BUILD_ID = '../outside'
            MustFail { & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publish -OutputDirectory (Join-Path $temporary 'unsafe') } 'Traversal identity was accepted'
            Require (-not (Test-Path -LiteralPath (Join-Path $temporary 'unsafe'))) 'Unsafe package output was created'
        } finally { $env:OVERLAY_BUILD_ID = $oldBuildId }
    }
    Case 'mismatched compiled identity refuses publication' {
        try {
            $env:OVERLAY_BUILD_ID = 'different-compiled-build'
            MustFail { & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publish -OutputDirectory (Join-Path $temporary 'mismatched') } 'Uncompiled build identity was accepted'
        } finally { $env:OVERLAY_BUILD_ID = $oldBuildId }
    }

    # Publisher tests use a synthetic archive and an in-process REST mock. They never contact GitHub.
    $releaseDirectory = Join-Path $temporary 'release'; $payload = Join-Path $temporary 'payload'
    New-Item -ItemType Directory -Path $releaseDirectory, $payload -Force | Out-Null
    $commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $identity = @{ schemaVersion = 1; version = '0.2.0-preview.10.1+aaaaaaa'; buildId = 'ci-10-a1-aaaaaaa'; commit = $commit; channel = 'testing' }
    $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding utf8NoBOM
    $name = 'GameSessionOverlay-ci-10-a1-aaaaaaa-win-x64.zip'
    $zip = Join-Path $releaseDirectory $name
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
    $manifest = $identity.Clone()
    $manifest.publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $manifest.package = @{ name = $name; size = (Get-Item -LiteralPath $zip).Length; sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseDirectory 'overlay-release.json') -Encoding utf8NoBOM
    $env:GH_TOKEN = 'fixture-credential'
    Get-Command Invoke-RestMethod | Out-Null
    function global:Invoke-RestMethod {
        param($Method, $Uri, $Headers, $ContentType, $Body, $InFile)
        $state = $global:OverlayReleaseMockState
        $state.Calls.Add("$Method $Uri")
        if ($Method -eq 'Get') {
            if ($state.Mode -in @('published', 'mismatch')) {
                $assets = @(Get-ChildItem -LiteralPath $state.Directory -File | ForEach-Object { @{ name = $_.Name; digest = 'sha256:' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
                if ($state.Mode -eq 'mismatch') { $assets[0].digest = 'sha256:bad' }
                return @{ id = 7; draft = $false; assets = $assets }
            }
            $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Fixture missing release', $response)
        }
        if ($Uri.StartsWith('https://uploads.github.com/', [StringComparison]::Ordinal)) {
            $state.Uploads++
            return @{ digest = $(if ($state.Mode -eq 'bad-upload') { 'sha256:bad' } else { 'sha256:' + (Get-FileHash -LiteralPath $InFile -Algorithm SHA256).Hash.ToLowerInvariant() }) }
        }
        if ($Method -eq 'Post') { return @{ id = 7; draft = $true; assets = @() } }
        if ($Method -eq 'Patch') {
            Require ($state.Uploads -eq 2) 'Release became visible before both assets uploaded'
            $state.Published = $true
            return @{ html_url = 'https://github.com/bezoro/apollo-tv-overlay/releases/tag/fixture' }
        }
        throw 'Unexpected mock request'
    }
    foreach ($mode in @('new', 'published', 'mismatch', 'bad-upload')) {
        $global:OverlayReleaseMockState = @{ Mode = $mode; Directory = $releaseDirectory; Calls = [Collections.Generic.List[string]]::new(); Uploads = 0; Published = $false }
        Case "publisher $mode keeps immutable assets and publishes last" {
            $invoke = { & (Join-Path $PSScriptRoot 'publish-release.ps1') -ArtifactDirectory $releaseDirectory -Repository 'bezoro/apollo-tv-overlay' -Commit $commit }
            if ($mode -in @('mismatch', 'bad-upload')) {
                MustFail $invoke 'Mismatched digest was published'
                Require (-not $global:OverlayReleaseMockState.Published) 'Failed upload was made visible'
            } else {
                & $invoke | Out-Null
                Require ($global:OverlayReleaseMockState.Published -eq ($mode -eq 'new')) 'Publication outcome differs'
                if ($mode -eq 'published') { Require ($global:OverlayReleaseMockState.Calls.Count -eq 1) 'Existing assets were mutated' }
            }
        }
    }
    Case 'publisher rejects another repository without making a request' {
        $global:OverlayReleaseMockState.Calls.Clear()
        MustFail { & (Join-Path $PSScriptRoot 'publish-release.ps1') -ArtifactDirectory $releaseDirectory -Repository 'other/repository' -Commit $commit } 'Foreign repository was accepted'
        Require ($global:OverlayReleaseMockState.Calls.Count -eq 0) 'Foreign repository received a request'
    }
} finally {
    Remove-Item -LiteralPath 'Function:\Invoke-RestMethod' -ErrorAction SilentlyContinue
    Remove-Variable -Name OverlayReleaseMockState -Scope Global -ErrorAction SilentlyContinue
    $env:GH_TOKEN = $oldCredential; $env:OVERLAY_BUILD_ID = $oldBuildId; $env:OVERLAY_VERSION = $oldVersion
    Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "RELEASE TESTS $passed passed; $failed failed"
if ($failed -gt 0) { throw 'Release pipeline tests failed.' }
