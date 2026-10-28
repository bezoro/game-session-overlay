param([ValidatePattern('^native-v[0-9]+$')][string]$OutputVersion='native-v1',[ValidateSet('All','Build','Behavior','PublishedBehavior','Capture','Native','Package','CliPackage')][string]$Stage='All',[string]$CliDestination)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$taskRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$checks=Join-Path $taskRoot "artifacts/check-$OutputVersion"
$publish=Join-Path $taskRoot "artifacts/$OutputVersion/publish"
New-Item -ItemType Directory -Path $checks -Force | Out-Null
function Invoke-Checked([string[]]$Arguments){ & $dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet exit $LASTEXITCODE"} }
function Publish-PackagedCli([string]$Destination) {
 $destinationRoot=(Get-Item -LiteralPath $Destination -ErrorAction Stop).FullName
 foreach($required in @('ApolloOverlay.exe','ApolloOverlay.dll','ApolloOverlay.runtimeconfig.json','coreclr.dll','System.Private.CoreLib.dll','hostfxr.dll','hostpolicy.dll')){
  if(!(Test-Path -LiteralPath (Join-Path $destinationRoot $required) -PathType Leaf)){throw "CLI requires the existing self-contained GUI payload: $required"}
 }
 $protected=@{}
 foreach($name in @('ApolloOverlay.exe','ApolloOverlay.dll','coreclr.dll','System.Private.CoreLib.dll','hostfxr.dll','hostpolicy.dll')){$protected[$name]=(Get-FileHash -LiteralPath (Join-Path $destinationRoot $name) -Algorithm SHA256).Hash}
 $cliOutput=Join-Path ([IO.Path]::GetTempPath()) ('overlay-cli-publish-'+[Guid]::NewGuid().ToString('N'))
 try{
  Invoke-Checked @('publish','tools/ApolloOverlay.Cli/ApolloOverlay.Cli.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',$cliOutput)
  foreach($required in @('ApolloOverlay.Cli.exe','ApolloOverlay.Cli.dll','ApolloOverlay.Cli.deps.json','ApolloOverlay.Cli.runtimeconfig.json')){
   if(!(Test-Path -LiteralPath (Join-Path $cliOutput $required) -PathType Leaf)){throw "Published CLI is missing: $required"}
  }
  $guiConfig=Get-Content -LiteralPath (Join-Path $destinationRoot 'ApolloOverlay.runtimeconfig.json') -Raw | ConvertFrom-Json
  $cliConfig=Get-Content -LiteralPath (Join-Path $cliOutput 'ApolloOverlay.Cli.runtimeconfig.json') -Raw | ConvertFrom-Json
  $guiCore=@($guiConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App')
  $cliCore=@($cliConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App')
  if($guiCore.Count -ne 1 -or $cliCore.Count -ne 1 -or $guiCore[0].version -cne $cliCore[0].version){throw 'GUI and CLI require different self-contained .NET runtimes.'}
  if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $destinationRoot 'ApolloOverlay.dll')).ProductVersion -cne [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $cliOutput 'ApolloOverlay.Cli.dll')).ProductVersion){throw 'GUI and CLI were not compiled with the same application version.'}
  Get-ChildItem -LiteralPath $cliOutput -File | Where-Object Name -Like 'ApolloOverlay.Cli.*' | Copy-Item -Destination $destinationRoot
  Copy-Item -LiteralPath (Join-Path $taskRoot 'gso.cmd') -Destination $destinationRoot
  foreach($name in $protected.Keys){if((Get-FileHash -LiteralPath (Join-Path $destinationRoot $name) -Algorithm SHA256).Hash -cne $protected[$name]){throw "CLI packaging overwrote a GUI or framework file: $name"}}
 }finally{if(Test-Path -LiteralPath $cliOutput){Remove-Item -LiteralPath $cliOutput -Recurse -Force}}
}
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
 if($Stage -eq 'CliPackage'){
  if(!$CliDestination){throw 'CliDestination is required for the isolated CLI packaging stage.'}
  Publish-PackagedCli $CliDestination
 }
 if($Stage -in @('All','Package')){
  if(Test-Path -LiteralPath $publish){throw 'Publish directory exists; use a fresh native-vN.'}
  Invoke-Checked @('tool','restore')
  Invoke-Checked @('publish','src/ApolloOverlay/ApolloOverlay.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',$publish)
  Publish-PackagedCli $publish
  Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'Guia de uso.md'),(Join-Path $taskRoot 'Abrir overlay.cmd'),(Join-Path $taskRoot 'Iniciar-previa.cmd') -Destination $publish
  Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts/startup-task.ps1') -Destination $publish
  Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/pilot-checklist.md') -Destination (Join-Path $publish 'compatibilidade.md')
  Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/third-party-icons.md') -Destination (Join-Path $publish 'third-party-icons.md')
  Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $publish -Recurse
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
 if($Stage -in @('All','Package')){ & (Join-Path $PSScriptRoot 'build-release.ps1') -PublishDirectory $publish -OutputDirectory (Join-Path $taskRoot 'artifacts/velopack') }
 Write-Output "VERIFY SUCCESS stage=$Stage; output=$OutputVersion; source/publish/results remain separate review artifacts."
 exit 0
} catch {Write-Error -Message $_ -ErrorAction Continue;exit 1}

