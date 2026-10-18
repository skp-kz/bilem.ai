# SHA256SUMS по файлам релиза и подпись Ed25519 (SHA256SUMS.sig) — после Authenticode-подписи MSI, перед выкладкой.
# Запуск: ./build/sign-release.ps1 -Dir artifacts/release -KeyFile key.pem
# Ключ — PEM Ed25519 (openssl genpkey -algorithm ed25519); проверка — открытым ключом, зашитым в агент (ReleaseVerifier).
param(
    [string]$Dir = 'artifacts/release',
    [Parameter(Mandatory = $true)][string]$KeyFile
)
$ErrorActionPreference = 'Stop'
$files = Get-ChildItem $Dir -File | Where-Object { $_.Name -notin 'SHA256SUMS', 'SHA256SUMS.sig' } | Sort-Object Name
if ($files.Count -eq 0) { throw "в $Dir нет файлов" }
$lines = foreach ($f in $files) { "$((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($f.Name)" }
$sums = Join-Path $Dir 'SHA256SUMS'
[IO.File]::WriteAllText($sums, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))

# openssl 3 (есть в Git for Windows и на раннерах GitHub): подпись Ed25519 «как есть» (-rawin)
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) { $openssl = Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe' }
& $openssl pkeyutl -sign -inkey $KeyFile -rawin -in $sums -out (Join-Path $Dir 'SHA256SUMS.sig')
if ($LASTEXITCODE -ne 0) { throw "подпись не удалась" }
# Самопроверка: открытый ключ из того же PEM
$pub = Join-Path ([IO.Path]::GetTempPath()) ("bilem-pub-" + [guid]::NewGuid().ToString('N') + ".pem")
& $openssl pkey -in $KeyFile -pubout -out $pub
& $openssl pkeyutl -verify -pubin -inkey $pub -rawin -in $sums -sigfile (Join-Path $Dir 'SHA256SUMS.sig')
$ok = $LASTEXITCODE -eq 0
Remove-Item $pub -Force
if (-not $ok) { throw "подпись не проверилась" }
Get-Content $sums
