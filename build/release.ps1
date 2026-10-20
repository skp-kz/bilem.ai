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

# --- Web-установщик .NET Framework 4.8 (Microsoft, ~1,4 МБ) — внутрь MSI; берём с сайта Microsoft и проверяем подпись
$redist = Join-Path $root 'installer/redist/ndp48-web.exe'
if (-not (Test-Path $redist)) {
    New-Item -ItemType Directory -Force (Split-Path $redist) | Out-Null
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?LinkId=2085155' -OutFile $redist -UseBasicParsing
}
$sig = Get-AuthenticodeSignature $redist
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item $redist -Force
    throw "ndp48-web.exe: подпись Microsoft не подтверждена ($($sig.Status))"
}

# --- Часть J: свой Apache (когда модуль 1С не работает в IIS) и Visual C++ 2015–2022 для него — внутрь MSI.
# Apache — копия сборки Apache Lounge в служебном релизе build-deps нашего репо (Apache Lounge убирает старые версии),
# хеш закреплён и совпадает с опубликованным на apachelounge.com. VC++ — с официального адреса Microsoft, проверяется подпись.
$apacheZips = @{
    x64 = @{ Name = 'httpd-2.4.66-251206-Win64-VS17.zip'; Sha256 = '2CD1F349B6705E43E784E876A233EEB1A859FA6B8ABC693A91F87B35A368F7F9' }
    x86 = @{ Name = 'httpd-2.4.66-251206-win32-vs17.zip'; Sha256 = '798B6BD7A4FCBB66AE3436B86BBC2E29F3E6165B970D471C1589DD782FFF2B12' }
}
foreach ($arch in 'x64', 'x86') {
    $dest = Join-Path $root "installer/redist/apache/$arch"
    if (Test-Path (Join-Path $dest 'bin/httpd.exe')) { continue }
    $z = $apacheZips[$arch]
    $zip = Join-Path $env:TEMP $z.Name
    Invoke-WebRequest -Uri "https://github.com/skp-kz/bilem.ai/releases/download/build-deps/$($z.Name)" -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $z.Sha256) { Remove-Item $zip -Force; throw "$($z.Name): SHA-256 $hash не совпадает с закреплённым" }
    $tmp = Join-Path $env:TEMP "bilem-apache-$arch"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip $tmp
    $src = Join-Path $tmp 'Apache24'
    New-Item -ItemType Directory -Force (Join-Path $dest 'conf') | Out-Null
    # Только то, что нужно для работы: программы, модули, типы файлов, лицензия. Свой httpd.conf пишет агент (C:\ProgramData\Bilem\apache)
    Copy-Item (Join-Path $src 'bin') $dest -Recurse
    Copy-Item (Join-Path $src 'modules') $dest -Recurse
    Copy-Item (Join-Path $src 'conf/mime.types') (Join-Path $dest 'conf')
    foreach ($f in 'LICENSE.txt', 'NOTICE.txt', 'ABOUT_APACHE.txt') { if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $dest } }
    Remove-Item $tmp -Recurse -Force
    Remove-Item $zip -Force
}
foreach ($arch in 'x64', 'x86') {
    $vc = Join-Path $root "installer/redist/vc_redist.$arch.exe"
    if (-not (Test-Path $vc)) { Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.$arch.exe" -OutFile $vc -UseBasicParsing }
    $s = Get-AuthenticodeSignature $vc
    if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        Remove-Item $vc -Force
        throw "vc_redist.$arch.exe: подпись Microsoft не подтверждена ($($s.Status))"
    }
}

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
