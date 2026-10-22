param([string]$OutputVersion='v1',[ValidateSet('All','Behavior','Render','Package')][string]$Stage='All')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$taskRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$dotnet='C:/Program Files/dotnet/dotnet.exe'
$output=Join-Path $taskRoot "artifacts/check-$OutputVersion"
New-Item -ItemType Directory -Path $output -Force | Out-Null
function Invoke-Checked([string[]]$Arguments) {
    & $dotnet @Arguments
    $code=$LASTEXITCODE
    if($code -ne 0) { throw "dotnet command failed with exit $code" }
}
try {
    if($Stage -in @('All','Behavior','Render')) {
    Invoke-Checked @('restore','ApolloOverlay.sln','--source',$taskRoot)
    Invoke-Checked @('build','ApolloOverlay.sln','-c','Release','--no-restore','--nologo')
    }
    if($Stage -in @('All','Behavior')) {
    Invoke-Checked @('tests/ApolloOverlay.Tests/bin/Release/net10.0-windows/ApolloOverlay.Tests.dll',(Join-Path $output 'behavior-report.json'))
    $report=Get-Content -LiteralPath (Join-Path $output 'behavior-report.json') -Raw | ConvertFrom-Json
    if($report.Total -lt 40 -or $report.Failed -ne 0 -or $report.Passed -ne $report.Total) {throw 'Incomplete behavioral report'}
    Invoke-Checked @('src/ApolloOverlay/bin/Release/net10.0-windows/ApolloOverlay.dll','--probe-guide')
    }
    if($Stage -in @('All','Render')) {
    Invoke-Checked @('src/ApolloOverlay/bin/Release/net10.0-windows/ApolloOverlay.dll','--render-preview',(Join-Path $output 'renders'))
    }
    if($Stage -in @('All','Package')) {
    Invoke-Checked @('publish','src/ApolloOverlay/ApolloOverlay.csproj','-c','Release','--no-build','--no-restore','--self-contained','false','--output',(Join-Path $taskRoot 'artifacts/publish'))
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'Iniciar-previa.cmd'),(Join-Path $taskRoot 'Iniciar-teste.cmd') -Destination (Join-Path $taskRoot 'artifacts/publish')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/pilot-checklist.md') -Destination (Join-Path $taskRoot 'artifacts/publish/pilot-checklist.md')
    $portableFiles=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts/publish') -File | ForEach-Object FullName)
    Compress-Archive -LiteralPath $portableFiles -DestinationPath (Join-Path $taskRoot "artifacts/ApolloOverlay-$OutputVersion-portable.zip")
    }
    Write-Output "VERIFY SUCCESS: stage=$Stage; output=$OutputVersion; no pilot launched. See captured behavioral/render reports for counts."
    exit 0
} catch { Write-Error -Message $_ -ErrorAction Continue; exit 1 }
