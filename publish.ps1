# 发布到 S:\local\app\cronlet\content（可通过 -Out 覆盖）
param(
    [string]$Out = 'S:\local\app\cronlet\content'
)

$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $Out -Parent
New-Item -ItemType Directory -Force $Out | Out-Null

dotnet publish (Join-Path $PSScriptRoot 'Cronlet.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $Out

Copy-Item (Join-Path $PSScriptRoot 'app.toml') (Join-Path $appRoot 'app.toml') -Force
Copy-Item (Join-Path $PSScriptRoot 'runner.ico') (Join-Path $appRoot 'app.ico') -Force
Write-Host "published to $Out"
