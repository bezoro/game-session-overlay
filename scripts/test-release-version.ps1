$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('overlay-semver-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'GitVersion.yml') -Destination $root

function Invoke-Git([string[]]$Arguments) {
    & git -C $root @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Git fixture failed: $($Arguments[0])"
    }
}
function Commit([string]$Message) {
    Invoke-Git @('commit', '--allow-empty', '-m', $Message)
}
function Identity([string]$Channel = 'stable', [int]$Attempt = 1) {
    & (Join-Path $PSScriptRoot 'get-build-version.ps1') -RepositoryPath $root -Channel $Channel -BuildNumber 100 -Attempt $Attempt
}
function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) {
        throw $Message
    }
    $script:passed++
}

try {
    Invoke-Git @('init', '--initial-branch=main')
    Invoke-Git @('config', 'user.name', 'Version fixtures')
    Invoke-Git @('config', 'user.email', 'tests@example.invalid')
    Invoke-Git @('config', 'commit.gpgsign', 'false')
    Invoke-Git @('config', 'tag.gpgsign', 'false')
    Invoke-Git @('config', 'core.hooksPath', (Join-Path $root 'disabled-hooks'))
    Invoke-Git @('add', 'GitVersion.yml')
    Commit 'chore: initialize version fixtures'
    Invoke-Git @('tag', 'v0.47.1+legacy')
    $initial = Identity
    Require ($initial.CoreVersion -eq '0.47.1' -and !$initial.ReleaseRequired) 'Legacy published version was not preserved.'

    Invoke-Git @('switch', '-c', 'feat/example')
    Commit 'feat(session): add a session feature'
    $preview = Identity 'testing'
    Require ($preview.Version -match '^0\.48\.0-preview\.100\.1\+[a-f0-9]{7}$') 'Feature preview did not advance the minor version.'
    Invoke-Git @('switch', 'main')
    Invoke-Git @('merge', '--no-ff', 'feat/example', '-m', "Merge pull request #1`n`nfeat(session): add a session feature")
    $feature = Identity
    Require ($feature.CoreVersion -eq '0.48.0' -and $feature.ReleaseRequired) 'Feature merge did not produce a new minor release.'
    Invoke-Git @('tag', ('v' + $feature.Version))

    Commit 'docs: clarify the usage guide'
    $docs = Identity
    Require ($docs.CoreVersion -eq '0.48.0' -and !$docs.ReleaseRequired) 'Documentation-only commit created a duplicate release.'
    $nextPreview = Identity 'testing'
    $retry = Identity 'testing' 2
    Require ($nextPreview.CoreVersion -eq '0.48.1' -and $retry.Version -match '-preview\.100\.2\+') 'Preview or retry did not remain newer than the installed stable release.'

    Commit 'fix(input): restore navigation'
    $fix = Identity
    Require ($fix.CoreVersion -eq '0.48.1' -and $fix.ReleaseRequired) 'Fix did not advance the patch version.'
    Invoke-Git @('tag', ('v' + $fix.Version))
    Commit 'refactor(audio): separate the native backend'
    $refactor = Identity
    Require ($refactor.CoreVersion -eq '0.48.2') 'Runtime refactor did not advance the patch version.'
    Invoke-Git @('tag', ('v' + $refactor.Version))
    Commit 'feat(api)!: change the automation contract'
    $breaking = Identity
    Require ($breaking.CoreVersion -eq '1.0.0') 'Breaking change did not advance the major version.'
    Write-Host "SEMANTIC VERSION FIXTURES $passed/$passed passed; actual GitVersion and legacy tags exercised."
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
