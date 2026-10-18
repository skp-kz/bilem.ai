# Сборка релиза Bilem Agent: версия из тега → Release-сборка → тесты → MSI (artifacts/release).
# Запуск: ./build/release.ps1 -Tag v0.3.0-beta.1 [-SkipTests] [-Stage all|build|msi]
# Подпись SHA256SUMS — build/sign-release.ps1 (ключ — секрет сборки RELEASE_SIGNING_KEY; в репозиторий и журнал не попадает).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [switch]$SkipTests,
    # Шаги для GitHub Actions: build — тесты и сборка; msi — только MSI (после Authenticode-подписи exe)
    [ValidateSet('all', 'build', 'msi')][string]$Stage = 'all'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# --- Версия: v0.3.0-beta.2 → SemVer 0.3.0-beta.2, файлы 0.3.0.2, MSI 0.3.0.2 (четвёртое число — номер беты; выпуск — .100)
if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$') { throw "тег не SemVer: $Tag" }
$prefix = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$suffix = $Matches[4]
$semver = if ($suffix) { "$prefix-$suffix" } else { $prefix }
$build = if ($suffix -and $suffix -match '(\d+)$') { [int]$Matches[1] } elseif ($suffix) { 0 } else { 100 }
$fileVersion = "$prefix.$build"
"Версия: $semver (файлы и MSI $fileVersion)"

$props = @("-p:VersionPrefix=$prefix", "-p:FileVersion=$fileVersion", "-p:AssemblyVersion=$prefix.0")
if ($suffix) { $props += "-p:VersionSuffix=$suffix" }

if ($Stage -ne 'msi') {
    if (-not $SkipTests) {
        dotnet test tests/Bilem.Agent.Core.Tests -c Release @props
        if ($LASTEXITCODE -ne 0) { throw "тесты не прошли" }
    }
    dotnet build Bilem.Agent.sln -c Release @props
    if ($LASTEXITCODE -ne 0) { throw "сборка не удалась" }
}
if ($Stage -eq 'build') { return }

# --- Authenticode (exe агента и помощников) — шаг в GitHub Actions до MSI; здесь не подписываем

dotnet build installer/Bilem.Agent.Installer.wixproj -c Release "-p:SemVer=$semver" "-p:MsiVersion=$fileVersion"
if ($LASTEXITCODE -ne 0) { throw "MSI не собрался" }

$out = Join-Path $root 'artifacts/release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$msi = Get-ChildItem (Join-Path $root 'artifacts/msi') -Recurse -Filter "BilemAgent-$semver.msi" | Select-Object -First 1
if (-not $msi) { throw "нет BilemAgent-$semver.msi" }
Copy-Item $msi.FullName $out

if ($env:GITHUB_OUTPUT) {
    "version=$semver" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "prerelease=$(if ($suffix) { 'true' } else { 'false' })" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "msi=$(Join-Path $out $msi.Name)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
"MSI: $(Join-Path $out $msi.Name)"
