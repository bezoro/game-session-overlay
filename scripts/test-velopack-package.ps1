[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleasePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('overlay-velopack-package-tests-' + [Guid]::NewGuid().ToString('N'))
$cases = [Collections.Generic.List[object]]::new()
$validator = Join-Path $PSScriptRoot 'get-validated-velopack-release.ps1'
function Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $cases.Add(@{ name = $Name
                passed = $true
            })
        Write-Output "PASS Velopack package $Name"
    }
    catch {
        $cases.Add(@{ name = $Name
                passed = $false
                error = $_.Exception.Message
            })
        Write-Error "FAIL Velopack package $Name`: $($_.Exception.Message)" -ErrorAction Continue
    }
}
function MustFail([scriptblock]$Body) {
    $caught = $false
    try {
        & $Body | Out-Null
    }
    catch {
        $caught = $true
    }
    if (-not $caught) {
        throw 'Invalid package was accepted.'
    }
}
try {
    Case 'PowerShell scripts parse' {
        foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
            $tokens = $null
            $errors = $null
            [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors) | Out-Null
            if ($errors.Count -gt 0) {
                throw "$($file.Name): $errors"
            }
        }
    }
    $original = & $validator -ArtifactDirectory $ReleasePath
    Case 'actual official feed, payload and compiled identity agree' {
        if ($original.Tag -cne ('v' + $original.Build.version) -or $original.PackageVersion -cne $original.Build.version.Split('+')[0]) {
            throw 'Tag, application version and SDK package version differ.'
        }
    }
    Case 'expected source commit binds the package' {
        $other = if ($original.Build.commit -ceq ('0' * 40)) {
            '1' * 40
        }
        else {
            '0' * 40
        }
        MustFail { & $validator -ArtifactDirectory $ReleasePath -ExpectedCommit $other }
    }
    Case 'expected application version binds the package' {
        MustFail { & $validator -ArtifactDirectory $ReleasePath -ExpectedVersion '99.99.99-invalid' }
    }
    Case 'authorized publication channel accepts its validated package' {
        & $validator -ArtifactDirectory $ReleasePath -ExpectedChannel $original.Build.channel | Out-Null
    }
    Case 'a package cannot be published to another channel' {
        $other = if ($original.Build.channel -eq 'testing') {
            'stable'
        }
        else {
            'testing'
        }
        MustFail { & $validator -ArtifactDirectory $ReleasePath -ExpectedChannel $other }
    }
    Copy-Item -LiteralPath $ReleasePath -Destination $temporary -Recurse
    $feedPath = Join-Path $temporary "releases.$($original.Build.channel).json"
    $inventoryPath = Join-Path $temporary "assets.$($original.Build.channel).json"
    $originalFeed = Get-Content -LiteralPath $feedPath -Raw
    $originalInventory = Get-Content -LiteralPath $inventoryPath -Raw
    Case 'SHA-256 mismatch is rejected before the package is consumed' {
        try {
            $feed = $originalFeed | ConvertFrom-Json
            $feed.Assets[0].SHA256 = '0' * 64
            $feed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $feedPath -Encoding utf8NoBOM
            MustFail { & $validator -ArtifactDirectory $temporary }
        }
        finally {
            [IO.File]::WriteAllText($feedPath, $originalFeed)
        }
    }
    Case 'another application package cannot enter this feed' {
        try {
            $feed = $originalFeed | ConvertFrom-Json
            $feed.Assets[0].PackageId = 'AnotherApplication'
            $feed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $feedPath -Encoding utf8NoBOM
            MustFail { & $validator -ArtifactDirectory $temporary }
        }
        finally {
            [IO.File]::WriteAllText($feedPath, $originalFeed)
        }
    }
    Case 'package filename cannot escape the release directory' {
        try {
            $feed = $originalFeed | ConvertFrom-Json
            $feed.Assets[0].FileName = '../outside.nupkg'
            $feed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $feedPath -Encoding utf8NoBOM
            MustFail { & $validator -ArtifactDirectory $temporary }
        }
        finally {
            [IO.File]::WriteAllText($feedPath, $originalFeed)
        }
    }
    Case 'official upload inventory cannot reference unrelated files' {
        try {
            $inventory = @($originalInventory | ConvertFrom-Json)
            $inventory[0].RelativeFileName = '../outside.txt'
            $inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inventoryPath -Encoding utf8NoBOM
            MustFail { & $validator -ArtifactDirectory $temporary }
        }
        finally {
            [IO.File]::WriteAllText($inventoryPath, $originalInventory)
        }
    }
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
    $report = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
    $failures = @($cases | Where-Object { -not $_.passed }).Count
    @{ total = $cases.Count
        passed = $cases.Count - $failures
        failed = $failures
        cases = $cases
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
}
if ($failures -gt 0) {
    throw 'Official Velopack package checks failed.'
}
Write-Output "VELOPACK PACKAGE TESTS $($cases.Count)/$($cases.Count) passed"
exit 0
