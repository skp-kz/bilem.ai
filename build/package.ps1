# Сборка архива bilem-agent-<версия>.zip из artifacts\bin\<конфигурация> (без подписи и MSI — v0.1).
# Запуск: ./build/package.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ver = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix | Select-Object -First 1
$bin = Join-Path $root "artifacts/bin/$Configuration"
$pkgRoot = Join-Path $root 'artifacts/pkg'
$dir = Join-Path $pkgRoot "bilem-agent-$ver"
$zip = Join-Path $pkgRoot "bilem-agent-$ver.zip"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $dir | Out-Null

# Агент, оба помощника COM, библиотеки и их .config — всё в одной папке
$need = 'bilem-agent.exe', 'bilem-com32.exe', 'bilem-com64.exe', 'Bilem.Agent.Core.dll', 'Newtonsoft.Json.dll'
foreach ($f in $need) { if (-not (Test-Path (Join-Path $bin $f))) { throw "нет $f в $bin — сначала dotnet build -c $Configuration" } }
Get-ChildItem $bin -File | Where-Object { $_.Extension -in '.exe', '.dll', '.config' } | Copy-Item -Destination $dir
Copy-Item (Join-Path $root 'README.md') $dir
Copy-Item (Join-Path $root 'docs') $dir -Recurse

Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
"Архив: $zip"
if ($env:GITHUB_OUTPUT) { "version=$ver" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
