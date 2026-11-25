$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$tempBin = Join-Path $root 'bin\PublishBuild'
$csproj = Join-Path $root 'MiniRec.csproj'

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:BaseOutputPath="$tempBin\" `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $dist

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-ChildItem $dist | Select-Object Name, @{N='SizeKB';E={[math]::Round($_.Length/1KB, 1)}}, @{N='SizeMB';E={[math]::Round($_.Length/1MB, 2)}}
