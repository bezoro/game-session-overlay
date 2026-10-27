param([ValidatePattern('^native-v[0-9]+$')][string]$OutputVersion='native-v1',[ValidateSet('All','Build','Behavior','PublishedBehavior','Capture','Native','Package')][string]$Stage='All')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$taskRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$dotnet='C:/Program Files/dotnet/dotnet.exe'
$checks=Join-Path $taskRoot "artifacts/check-$OutputVersion"
$publish=Join-Path $taskRoot "artifacts/$OutputVersion/publish"
New-Item -ItemType Directory -Path $checks -Force | Out-Null
function Invoke-Checked([string[]]$Arguments){ & $dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet exit $LASTEXITCODE"} }
try {
 if($Stage -in @('All','Build')){Invoke-Checked @('build','ApolloOverlay.sln','-c','Release')}
 if($Stage -in @('All','Behavior')){
  Invoke-Checked @('tests/ApolloOverlay.Tests/bin/Release/net10.0-windows/ApolloOverlay.Tests.dll',(Join-Path $checks 'behavior-report.json'))
  $cliTests=Join-Path $taskRoot 'tests/ApolloOverlay.Cli.Tests/bin/Release/net10.0-windows/ApolloOverlay.Cli.Tests.exe'
  & $cliTests
  if($LASTEXITCODE -ne 0){throw "CLI tests exit $LASTEXITCODE"}
  & $cliTests '--roundtrip' (Join-Path $taskRoot 'tools/ApolloOverlay.Cli/bin/Release/net10.0-windows/ApolloOverlay.Cli.exe')
  if($LASTEXITCODE -ne 0){throw "CLI roundtrip exit $LASTEXITCODE"}
 }
 if($Stage -in @('All','Package')){
  if(Test-Path -LiteralPath $publish){throw 'Publish directory exists; use a fresh native-vN.'}
  Invoke-Checked @('publish','src/ApolloOverlay/ApolloOverlay.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',$publish)
  Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'Abrir overlay.cmd'),(Join-Path $taskRoot 'Iniciar-previa.cmd') -Destination $publish
  Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts/startup-task.ps1') -Destination $publish
  Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/pilot-checklist.md') -Destination (Join-Path $publish 'compatibilidade.md')
  Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/third-party-icons.md') -Destination (Join-Path $publish 'third-party-icons.md')
 }
 if($Stage -in @('All','PublishedBehavior')){
  $isolated=Join-Path $checks 'published-test-host'
  if(Test-Path -LiteralPath $isolated){throw 'Isolated test directory already exists.'}
  Copy-Item -LiteralPath $publish -Destination $isolated -Recurse
  $binding=@()
  foreach($file in Get-ChildItem -LiteralPath $publish -Recurse -File){$relative=[IO.Path]::GetRelativePath($publish,$file.FullName);$copy=Join-Path $isolated $relative;$expected=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash;$actual=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash;if($expected -ne $actual){throw "Payload copy differs: $relative"};$binding+=@{relativePath=$relative;sha256=$actual}}
  Get-ChildItem -LiteralPath (Join-Path $taskRoot 'tests/ApolloOverlay.Tests/bin/Release/net10.0-windows') -File | Where-Object Name -Like 'ApolloOverlay.Tests.*' | Copy-Item -Destination $isolated
  $trace=Join-Path $checks 'dotnet-host-trace.txt'
  $oldTrace=$env:DOTNET_HOST_TRACE;$oldFile=$env:DOTNET_HOST_TRACEFILE;$oldVerbosity=$env:DOTNET_HOST_TRACE_VERBOSITY
  try{$env:DOTNET_HOST_TRACE='1';$env:DOTNET_HOST_TRACEFILE=$trace;$env:DOTNET_HOST_TRACE_VERBOSITY='4'; & $dotnet (Join-Path $isolated 'ApolloOverlay.Tests.dll') (Join-Path $checks 'published-behavior-report.json');$testExit=$LASTEXITCODE}
  finally{$env:DOTNET_HOST_TRACE=$oldTrace;$env:DOTNET_HOST_TRACEFILE=$oldFile;$env:DOTNET_HOST_TRACE_VERBOSITY=$oldVerbosity}
  $resolved=@(Get-Content -LiteralPath $trace | Where-Object {$_ -match 'Adding tpa entry:.*[\\/]ApolloOverlay\.dll'})
  if($resolved.Count -ne 1 -or !$resolved[0].Contains((Join-Path $isolated 'ApolloOverlay.dll'))){throw 'Host trace does not establish exact isolated ApolloOverlay.dll resolution.'}
  @{method='Official .NET10 DOTNET_HOST_TRACE; byte-identical full publish isolation; only test-named files added';publishRoot=$publish;isolatedRoot=$isolated;appDllSha256=(Get-FileHash -LiteralPath (Join-Path $isolated 'ApolloOverlay.dll') -Algorithm SHA256).Hash;resolved=$resolved;files=$binding;exitCode=$testExit} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checks 'test-publish-binding.json') -Encoding utf8
  if($testExit -ne 0){throw "Published behavior failed: $testExit"}
 }
 $app=Join-Path $publish 'ApolloOverlay.exe'
 if($Stage -in @('All','Capture')){ & $app '--capture-native' (Join-Path $checks 'capture');if($LASTEXITCODE -ne 0){throw 'Native capture failed'} }
 if($Stage -in @('All','Native')){ & $app '--native-check' (Join-Path $checks 'native');if($LASTEXITCODE -ne 0){throw 'Native checks failed'} }
 if($Stage -in @('All','Package')){[System.IO.Compression.ZipFile]::CreateFromDirectory($publish,(Join-Path $taskRoot "artifacts/ApolloOverlay-$OutputVersion.zip"))}
 Write-Output "VERIFY SUCCESS stage=$Stage; output=$OutputVersion; source/publish/results remain separate review artifacts."
 exit 0
} catch {Write-Error -Message $_ -ErrorAction Continue;exit 1}

