[CmdletBinding()]
param(

    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)][string]$Repository,

    [Parameter(Mandatory = $true)][string]$Commit

)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Repository -notmatch '^bezoro/[A-Za-z0-9_.-]+$' -or $Commit -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Invalid publication repository or commit.'
}
if (-not $env:GH_TOKEN) {
    throw 'GitHub job credential is missing.'
}
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'GameSessionOverlay-release'
}
$canonical = Invoke-RestMethod -Method Get -Uri 'https://api.github.com/repositories/1430327913' -Headers $headers
if ($canonical.id -ne 1430327913 -or $canonical.owner.login -cne 'bezoro' -or $canonical.full_name -cne $Repository) {
    throw 'Publication target is not the configured repository identity.'
}
$release = & (Join-Path $PSScriptRoot 'get-validated-velopack-release.ps1') -ArtifactDirectory $ArtifactDirectory -ExpectedCommit $Commit
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$arguments = @('tool', 'run', 'vpk', '--', '[win]', 'upload', 'github', '--outputDir', $ArtifactDirectory, '--channel', $release.Build.channel,
    '--repoUrl', "https://github.com/$($canonical.full_name)", '--tag', $release.Tag, '--releaseName', "Game Session Overlay $($release.Build.version)",
    '--targetCommitish', $Commit, '--publish', '--timeout', '5', '--skip-updates')
if ($release.Build.channel -eq 'testing') {
    $arguments += '--pre'
}
$previousToken = $env:VPK_TOKEN
try {
    # Official CLI reads its short-lived job credential from the environment, never command-line arguments.
    $env:VPK_TOKEN = $env:GH_TOKEN
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Official Velopack publication failed with exit code $LASTEXITCODE"
    }
}
finally {
    $env:VPK_TOKEN = $previousToken
}
Write-Output "Official Velopack release published: $($release.Tag); channel=$($release.Build.channel)"
