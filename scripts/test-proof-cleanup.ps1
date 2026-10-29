$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'proof-cleanup.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('overlay-cleanup-fixtures-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$cases = 0
try {
    Remove-ProofDirectory -LiteralPath (Join-Path $root 'missing')
    $cases++
    $directory = Join-Path $root 'normal'
    New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'file'), 'owned fixture')
    Remove-ProofDirectory -LiteralPath $directory
    if (Test-Path -LiteralPath $directory) {
        throw 'Normal cleanup left its directory.'
    }
    $cases++

    # Substitute only the filesystem command at the race boundary. Real temporary
    # directories are removed, then the same exception as a concurrent remover is raised.
    $script:cleanupScenario = 'root'
    $script:removeCalls = 0
    function Remove-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
        $script:removeCalls++
        if ($script:cleanupScenario -eq 'denied') {
            throw [UnauthorizedAccessException]::new('Fixture access refusal')
        }
        if ($script:removeCalls -eq 1) {
            if ($script:cleanupScenario -eq 'root') {
                [IO.Directory]::Delete($LiteralPath, $true)
            }
            else {
                [IO.File]::Delete((Join-Path $LiteralPath 'file'))
            }
            throw [IO.DirectoryNotFoundException]::new('Fixture concurrent removal')
        }
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse -Force -ErrorAction Stop
    }
    foreach ($scenario in @('root', 'child')) {
        $script:cleanupScenario = $scenario
        $script:removeCalls = 0
        $directory = Join-Path $root $scenario
        New-Item -ItemType Directory -Path $directory | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory 'file'), 'owned fixture')
        Remove-ProofDirectory -LiteralPath $directory
        if ((Test-Path -LiteralPath $directory) -or $removeCalls -ne $(if ($scenario -eq 'root') {
                    1
                }
                else {
                    2
                })) {
            throw "Concurrent $scenario cleanup was not idempotent."
        }
        $cases++
    }
    $script:cleanupScenario = 'denied'
    $directory = Join-Path $root 'denied'
    New-Item -ItemType Directory -Path $directory | Out-Null
    $refused = $false
    try {
        Remove-ProofDirectory -LiteralPath $directory
    }
    catch [UnauthorizedAccessException] {
        $refused = $true
    }
    if (!$refused -or !(Test-Path -LiteralPath $directory)) {
        throw 'Cleanup suppressed a real access failure.'
    }
    $cases++
    Write-Host "PROOF CLEANUP FIXTURES $cases/$cases passed"
}
finally {
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) {
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $root -Recurse -Force
    }
}
