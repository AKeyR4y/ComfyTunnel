param([switch]$Publish, [switch]$ReplaceRunning)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$dotnetPath = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repoRoot
try {
    if ($ReplaceRunning) {
        $previewPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist/win-x64/ComfyTunnel.exe'))
        Get-Process ComfyTunnel -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $previewPath } | Stop-Process
    }
    if ($Publish) {
        & $dotnetPath publish src/ComfyTunnel/ComfyTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o dist/win-x64
    } else {
        & $dotnetPath build src/ComfyTunnel/ComfyTunnel.csproj -c Release
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
