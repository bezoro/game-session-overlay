[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [string]$ExpectedCommit,
    [string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $ArtifactDirectory).FullName
$identity = Get-Content -LiteralPath (Join-Path $root 'build-info.json') -Raw | ConvertFrom-Json
if ($identity.schemaVersion -ne 1 -or $identity.channel -notin @('testing', 'stable') -or
    $identity.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
    $identity.buildId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$' -or $identity.buildId.Contains('..') -or
    ($identity.commit -ne 'unknown' -and $identity.commit -notmatch '^[a-fA-F0-9]{40}$')) { throw 'Invalid application build identity.' }
if ($ExpectedCommit -and $identity.commit -cne $ExpectedCommit) { throw 'Package source commit differs from the validated commit.' }
if ($ExpectedVersion -and $identity.version -cne $ExpectedVersion) { throw 'Package version differs from the requested version.' }
$packageVersion = $identity.version.Split('+')[0]
$feedPath = Join-Path $root "releases.$($identity.channel).json"
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$assets = @($feed.Assets)
if ($assets.Count -ne 1) { throw 'This pipeline expects exactly one official full package and no deltas.' }
$asset = $assets[0]
$notes = ([string]$asset.NotesMarkdown) -split '\r?\n'
foreach ($field in @(@('Version', $identity.version), @('Build', $identity.buildId), @('Commit', $identity.commit), @('Channel', $identity.channel))) {
    $lines = @($notes | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith($field[0] + ':', [StringComparison]::Ordinal) })
    if ($lines.Count -ne 1 -or $lines[0] -cne ($field[0] + ': ' + $field[1])) { throw "Official release notes do not identify the expected $($field[0])." }
}
$packageName = "GameSessionOverlay-$packageVersion-$($identity.channel)-full.nupkg"
if ($asset.PackageId -cne 'GameSessionOverlay' -or $asset.Version -cne $packageVersion -or
    ([string]$asset.Type) -notin @('Full', '1') -or $asset.FileName -cne $packageName -or
    $asset.SHA256 -notmatch '^[a-fA-F0-9]{64}$' -or $asset.SHA1 -notmatch '^[a-fA-F0-9]{40}$') { throw 'Official Velopack feed does not match the expected package identity.' }
$package = Get-Item -LiteralPath (Join-Path $root $packageName)
if ($package.PSIsContainer -or $package.Length -le 0 -or $package.Length -ne $asset.Size -or
    (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash -ine $asset.SHA256 -or
    (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA1).Hash -ine $asset.SHA1) { throw 'Official Velopack package hash or size failed validation.' }
$setupName = "GameSessionOverlay-$($identity.channel)-Setup.exe"
$setup = Get-Item -LiteralPath (Join-Path $root $setupName)
if ($setup.PSIsContainer -or $setup.Length -le 0) { throw 'Official Velopack installer is missing.' }
$buildAssets = @(Get-Content -LiteralPath (Join-Path $root "assets.$($identity.channel).json") -Raw | ConvertFrom-Json)
if ($buildAssets.Count -ne 2 -or @($buildAssets | Where-Object { $_.RelativeFileName -ceq $packageName -and ([string]$_.Type) -in @('Full', '1') }).Count -ne 1 -or
    @($buildAssets | Where-Object { $_.RelativeFileName -ceq $setupName -and ([string]$_.Type) -in @('Installer', '4') }).Count -ne 1) { throw 'Official Velopack upload inventory is inconsistent.' }

$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
$dllTemporary = [IO.Path]::GetTempFileName()
function Read-PackageEntry([string]$Name, [long]$Maximum) {
    $entries = @($archive.Entries | Where-Object FullName -CEQ $Name)
    if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt $Maximum) { throw "Package entry is missing, duplicated or too large: $Name" }
    $source = $entries[0].Open(); $output = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(8192)
        while (($count = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($output.Length + $count -gt $Maximum) { throw "Package entry exceeded its bound: $Name" }
            $output.Write($buffer, 0, $count)
        }
        return ,$output.ToArray()
    } finally { $source.Dispose(); $output.Dispose() }
}
try {
    $inside = [Text.Encoding]::UTF8.GetString((Read-PackageEntry 'lib/app/build-info.json' 8192)) | ConvertFrom-Json
    foreach ($field in @('version', 'buildId', 'commit', 'channel')) {
        if ($inside.$field -cne $identity.$field) { throw "Packaged provenance differs: $field" }
    }
    $specBytes = Read-PackageEntry 'GameSessionOverlay.nuspec' 131072
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $xmlReader = [Xml.XmlReader]::Create([IO.StringReader]::new([Text.Encoding]::UTF8.GetString($specBytes)), $settings)
    $spec = [Xml.XmlDocument]::new(); $spec.XmlResolver = $null
    try { $spec.Load($xmlReader) } finally { $xmlReader.Dispose() }
    if ($spec.package.metadata.id -cne 'GameSessionOverlay' -or $spec.package.metadata.version -cne $packageVersion -or
        $spec.package.metadata.channel -cne $identity.channel -or $spec.package.metadata.mainExe -cne 'ApolloOverlay.exe') { throw 'Official package specification differs from the expected app, version or channel.' }
    [IO.File]::WriteAllBytes($dllTemporary, (Read-PackageEntry 'lib/app/ApolloOverlay.dll' 16777216))
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dllTemporary).ProductVersion -cne $identity.version) { throw 'Packaged assembly version differs from its provenance.' }
    $stream = [IO.File]::OpenRead($dllTemporary); $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe); $fields = @{}
        foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
            if ($metadata.GetString($type.Namespace) -ne 'System.Reflection' -or $metadata.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }
            $blob = $metadata.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata encoding.' }
            $key = $blob.ReadSerializedString(); $fields[$key] = $blob.ReadSerializedString()
        }
        if ($fields.OverlayBuildId -cne $identity.buildId -or $fields.OverlayCommit -cne $identity.commit -or $fields.OverlayChannel -cne $identity.channel) { throw 'Packaged compiled build identity differs from the feed.' }
    } finally { $pe.Dispose(); $stream.Dispose() }
    foreach ($required in @('lib/app/ApolloOverlay.exe', 'lib/app/Velopack.dll', 'lib/app/Squirrel.exe')) {
        if (@($archive.Entries | Where-Object { $_.FullName -ceq $required -and $_.Length -gt 0 }).Count -ne 1) { throw "Required Velopack payload is missing: $required" }
    }
} finally { $archive.Dispose(); Remove-Item -LiteralPath $dllTemporary -Force -ErrorAction SilentlyContinue }
[pscustomobject]@{ Build = $identity; PackageVersion = $packageVersion; PackagePath = $package.FullName; SetupPath = $setup.FullName; FeedPath = $feedPath; Asset = $asset; Tag = 'v' + $identity.version }
