[CmdletBinding()]
param(
    [ValidateSet('stable', 'testing')][string]$Channel = 'testing',
    [ValidateRange(1, [int]::MaxValue)][int]$BuildNumber = 1,
    [ValidateRange(1, [int]::MaxValue)][int]$Attempt = 1,
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$repository = (Get-Item -LiteralPath $RepositoryPath -ErrorAction Stop).FullName
$toolRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $toolRoot
try {
    $raw = & dotnet gitversion $repository /config (Join-Path $repository 'GitVersion.yml') /nofetch /nonormalize /output json /verbosity Quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'GitVersion could not calculate the version. Fetch full history and tags first.'
    }
    $calculated = ($raw -join "`n") | ConvertFrom-Json
    if ($calculated.MajorMinorPatch -notmatch '^\d+\.\d+\.\d+$' -or $calculated.Sha -notmatch '^[a-f0-9]{40}$') {
        throw 'GitVersion returned an invalid semantic version or commit.'
    }
    $head = & git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $calculated.Sha) {
        throw 'Version identity does not match the checked-out commit.'
    }
    $core = $calculated.MajorMinorPatch
    $tags = @(& git -C $repository tag --list "v$core" "v$core+*")
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect published version tags.'
    }
    $versionReserved = $tags.Count -gt 0

    # A tag reserves an immutable version. Retry a failed publication with its
    # original artifact/job rather than rebuilding different bytes for that version.
    # A preview of a reserved stable version must advance its core to remain newer.
    if ($Channel -eq 'testing' -and $versionReserved) {
        $core = "$($calculated.Major).$($calculated.Minor).$([long]$calculated.Patch + 1)"
    }
    $short = $calculated.Sha.Substring(0, 7)
    $version = if ($Channel -eq 'stable') {
        $core
    }
    else {
        "$core-preview.$BuildNumber.$Attempt+$short"
    }
    [pscustomobject]@{
        Version = $version
        CoreVersion = $core
        FileVersion = "$core.0"
        BuildId = "ci-$BuildNumber-a$Attempt-$short"
        Commit = $calculated.Sha
        Channel = $Channel
        ReleaseRequired = $Channel -eq 'testing' -or !$versionReserved
    }
}
finally {
    Pop-Location
}
