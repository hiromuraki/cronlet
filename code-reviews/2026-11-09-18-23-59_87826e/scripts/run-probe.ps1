$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$cronlet = (Resolve-Path (Join-Path $repo 'artifacts/bin/Cronlet/release/Cronlet.dll')).Path
$scratch = Join-Path $repo ('artifacts/review-main/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Probe.cs') -Destination $scratch
$reference = [Security.SecurityElement]::Escape($cronlet)
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0-windows</TargetFramework>
    <AssemblyName>Cronlet.Tests</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Probe.cs" />
    <Reference Include="Cronlet"><HintPath>$reference</HintPath></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Probe.csproj') -Encoding utf8

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet build (Join-Path $scratch 'Probe.csproj') -c Release --disable-build-servers -m:1 --nologo -p:NuGetAudit=false -p:RunAnalyzers=false -o (Join-Path $scratch 'out')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $scratch 'out/Cronlet.Tests.exe')
exit $LASTEXITCODE
