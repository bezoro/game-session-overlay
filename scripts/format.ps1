[CmdletBinding()]
param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceFiles = @(
    foreach ($directory in @('src', 'tools', 'tests')) {
        Get-ChildItem -LiteralPath (Join-Path $root $directory) -Filter '*.cs' -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName }
    }
)
$arguments = @('format', 'whitespace', $root, '--folder', '--include') + $sourceFiles
if ($Verify) { $arguments += '--verify-no-changes' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'C# formatting failed. Run ./scripts/format.ps1 to apply the repository style.' }

$xmlFiles = @(
    Get-Item -LiteralPath (Join-Path $root 'Directory.Build.props')
    foreach ($directory in @('src', 'tools', 'tests')) {
        Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File |
            Where-Object { $_.Extension -in @('.csproj', '.xaml', '.manifest') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
)
$invalid = @()
foreach ($file in $xmlFiles) {
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    $document.Load($file.FullName)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = "`n"
    $settings.NewLineOnAttributes = $true
    $settings.OmitXmlDeclaration = $true
    $stream = [System.IO.MemoryStream]::new()
    try {
        $writer = [System.Xml.XmlWriter]::Create($stream, $settings)
        try { $document.Save($writer) } finally { $writer.Dispose() }
        $formatted = [System.Text.Encoding]::UTF8.GetString($stream.ToArray()).TrimEnd() + "`n"
    } finally { $stream.Dispose() }
    if ([System.IO.File]::ReadAllText($file.FullName) -cne $formatted) {
        if ($Verify) { $invalid += $file.FullName }
        else { [System.IO.File]::WriteAllText($file.FullName, $formatted, [System.Text.UTF8Encoding]::new($false)) }
    }
}
if ($invalid.Count -gt 0) { throw "XML formatting differs. Run ./scripts/format.ps1:`n$($invalid -join "`n")" }
Write-Host "Formatting verified: $($sourceFiles.Count) C# files and $($xmlFiles.Count) XML files."
