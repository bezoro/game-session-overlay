[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$commit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Cannot identify the source revision for documentation links.'
}
$docs = @('cli', 'controller-isolation', 'diagnostics', 'mcp', 'pilot-checklist', 'third-party-icons',
    'third-party-logging', 'third-party-updates', 'updates')
$files = @('README.md', 'USER_GUIDE.md') + @($docs | ForEach-Object { "docs/$_.md" })
New-Item -ItemType Directory -Path (Join-Path $Destination 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $Destination
foreach ($file in $files) {
    $source = Join-Path $root $file
    $markdown = [IO.File]::ReadAllText($source)
    # Resolve contributor-only references against the source used to build this release.
    $markdown = [regex]::Replace($markdown, '\]\((?!https?://|#)([^)#]+)(#[^)]*)?\)', {
        param($link)
        $path = [IO.Path]::GetFullPath((Join-Path (Split-Path $source -Parent) $link.Groups[1].Value))
        $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
        if ($relative -in $files -or $relative -ceq 'LICENSE') { return $link.Value }
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing documentation target $relative in $file." }
        if ($relative.StartsWith('src/ApolloOverlay/Assets/')) {
            return '](../' + $relative.Substring('src/ApolloOverlay/'.Length) + $link.Groups[2].Value + ')'
        }
        return "](https://github.com/bezoro/game-session-overlay/blob/$commit/$relative" + $link.Groups[2].Value + ')'
    })
    [IO.File]::WriteAllText((Join-Path $Destination $file), $markdown)
}
