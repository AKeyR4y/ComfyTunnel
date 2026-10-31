param([switch]$Integration)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$dotnetPath=Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnetPath)) { $dotnetPath='dotnet' }
$env:DOTNET_CLI_HOME=Join-Path $repoRoot '.tools/cli'
$env:NUGET_PACKAGES=Join-Path $repoRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $repoRoot
try {
 $resultDir=Join-Path $repoRoot ('test-results/' + [guid]::NewGuid().ToString('N'))
 $testArgs=@($resultDir)
 if ($Integration) { $testArgs+='--integration' }
 & $dotnetPath run --project tests/Tunnel.Tests/Tunnel.Tests.csproj -c Release -- @testArgs
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
