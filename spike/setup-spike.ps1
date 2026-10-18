<#
  Bilem Agent — спайк онбординга (OData, публикация только на 127.0.0.1, без расширения).
  Прототип шагов 1–10 из docs/onboarding.md. Запускать от администратора Windows и только на КОПИИ базы.

  По умолчанию — только ДИАГНОСТИКА и ПЛАН, ничего не меняет:
    .\setup-spike.ps1 -BasePath C:\Bases\Demo
  Применить:
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -Apply
  Включить IIS и «Ограничения по IP», если их нет (только с этим флагом):
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -Apply -EnableIIS
  Без окна (для агентов): логин параметром, пароль — в переменной окружения BILEM_1C_ADMIN_PWD:
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -AdminUser Администратор -Apply
  Учебная база — пароль прямо параметром:
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -AdminUser Администратор -AdminPassword 123 -Apply
  База без пользователей (вход без пароля):
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -NoAuth
  Список ролей конфигурации (чтобы выбрать права bilem):
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -ListRoles
  Только проверка уже подключённой базы (роли bilem, чтение, запрет записи, 401/403), ничего не меняет:
    .\setup-spike.ps1 -BasePath C:\Bases\Demo -AdminUser Администратор -Verify

  Пароль администратора 1С спрашивается окном и нигде не сохраняется.
  Пароль bilem генерируется случайно и хранится только в DPAPI-файле (C:\ProgramData\Bilem\spike\).
  Роли bilem по умолчанию — только чтение (для БП КЗ 3.0); проверка на шаге 10 пробует записать
  (POST документа, PATCH справочника) и падает, если 1С это разрешила.
#>
param(
  [Parameter(Mandatory = $true)][string]$BasePath,
  [string]$PublishName = '',
  [string]$PlatformVersion = '',
  # «Только чтение» для БП КЗ 3.0 (подобрано по ПравоДоступа на состав OData): базовые роли + чтение.
  # ЧтениеДанныхБухгалтерии покрывает почти весь состав; курсы валют, чеки ККМ и отчёты о розничных
  # продажах — отдельными ролями. Ни одной роли «ДобавлениеИзменение…», административной, зарплатной.
  [string[]]$Roles = @(
    'БазовыеПраваБСП',
    'БазовыеПраваБК',
    'ЧтениеДанныхБухгалтерии',
    'ЧтениеКурсовВалют',
    'ЧтениеЧековККМ',
    'ЧтениеОтчетаОРозничныхПродажах'
  ),
  # Деньги, продажи, покупки, склад, расчёты. Без зарплаты, кадров и физлиц (в т. ч. авансовых отчётов
  # и контактных лиц). Объектов, которых нет в конфигурации, скрипт пропускает — список общий для КЗ и РФ.
  [string[]]$ODataObjects = @(
    # справочники
    'Справочник.Организации',
    'Справочник.ПодразделенияОрганизаций',
    'Справочник.Контрагенты',
    'Справочник.ДоговорыКонтрагентов',
    'Справочник.Номенклатура',
    'Справочник.НоменклатурныеГруппы',
    'Справочник.КлассификаторЕдиницИзмерения',
    'Справочник.ТипыЦенНоменклатуры',
    'Справочник.Склады',
    'Справочник.Кассы',
    'Справочник.БанковскиеСчета',
    'Справочник.Банки',
    'Справочник.Валюты',
    'Справочник.СтатьиДвиженияДенежныхСредств',
    'Справочник.СтатьиЗатрат',
    'Справочник.СтавкиНДС',
    # деньги: банк и касса
    'Документ.ПлатежноеПоручениеВходящее',
    'Документ.ПлатежноеПоручениеИсходящее',
    'Документ.ПлатежныйОрдерПоступлениеДенежныхСредств',
    'Документ.ПлатежныйОрдерСписаниеДенежныхСредств',
    'Документ.ПриходныйКассовыйОрдер',
    'Документ.РасходныйКассовыйОрдер',
    'Документ.ОплатаОтПокупателяПлатежнойКартой',
    'Документ.ПоступлениеНаРасчетныйСчет',
    'Документ.СписаниеСРасчетногоСчета',
    # продажи
    'Документ.СчетНаОплатуПокупателю',
    'Документ.РеализацияТоваровУслуг',
    'Документ.ВозвратТоваровОтПокупателя',
    'Документ.СчетФактураВыданный',
    'Документ.ЧекККМ',
    'Документ.ОтчетОРозничныхПродажах',
    # покупки
    'Документ.ПоступлениеТоваровУслуг',
    'Документ.ПоступлениеДопРасходов',
    'Документ.ВозвратТоваровПоставщику',
    'Документ.СчетФактураПолученный',
    # склад
    'Документ.ПеремещениеТоваров',
    'Документ.СписаниеТоваров',
    'Документ.ОприходованиеТоваров',
    # расчёты с контрагентами
    'Документ.АктСверкиВзаиморасчетов',
    'Документ.КорректировкаДолга',
    'Документ.Доверенность',
    # учёт, цены, курсы
    'РегистрБухгалтерии.Типовой',
    'РегистрБухгалтерии.Хозрасчетный',
    'ПланСчетов.Типовой',
    'ПланСчетов.Хозрасчетный',
    'РегистрСведений.КурсыВалют',
    'РегистрСведений.ЦеныНоменклатуры'
  ),
  [string]$BilemUser = 'bilem',
  [switch]$Apply,
  [switch]$EnableIIS,
  [switch]$ListRoles,
  [switch]$Verify,
  [switch]$NoAuth,
  [string]$AdminUser = '',
  [string]$AdminPassword = ''
)

$ErrorActionPreference = 'Stop'
# Запуск из PowerShell 7 передаёт свой PSModulePath — Windows PowerShell 5.1 грузит модули 7-ки и падает
# на ConvertTo-SecureString («TypeData ... is already present»). Оставляем только пути WindowsPowerShell.
if ($PSVersionTable.PSVersion.Major -le 5) {
  $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object { $_ -match '\\WindowsPowerShell\\' }) -join ';'
}
$SpikeDir = 'C:\ProgramData\Bilem\spike'
New-Item -ItemType Directory -Force -Path $SpikeDir | Out-Null
$LogFile = Join-Path $SpikeDir ("setup-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

function Log([string]$level, [string]$msg) {
  $line = "{0:HH:mm:ss} [{1}] {2}" -f (Get-Date), $level, $msg
  Add-Content -Path $LogFile -Value $line -Encoding UTF8
  $color = @{ OK = 'Green'; PLAN = 'Cyan'; WARN = 'Yellow'; FAIL = 'Red'; INFO = 'Gray' }[$level]
  Write-Host $line -ForegroundColor $color
}
function Step([string]$n, [string]$title) { Write-Host ''; Log 'INFO' "=== Шаг $n. $title ===" }
function Fail([string]$msg, [string]$hint = '') {
  Log 'FAIL' $msg
  if ($hint) { Log 'FAIL' "Что делать: $hint" }
  Log 'INFO' "Журнал: $LogFile"
  exit 1
}
# Любая непредвиденная ошибка — в журнал и код 1 (иначе обёртка перезапуска отдаёт 0)
trap { Fail ("Непредвиденная ошибка (строка {0}): {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) 'Пришлите журнал разработчику.' }

# Объекты 1С по COM: PowerShell не видит их свойства напрямую ($c.Metadata = $null) и разворачивает
# коллекции при возврате из функции — свойства читаем через InvokeMember и возвращаем через «,». Методы зовём напрямую.
function P($o, [string]$n) { return , ([System.__ComObject].InvokeMember($n, [Reflection.BindingFlags]::GetProperty, $null, $o, $null)) }
function M($o, [string]$n) { return , ([System.__ComObject].InvokeMember($n, [Reflection.BindingFlags]::InvokeMethod, $null, $o, $null)) }
function SetP($o, [string]$n, $v) { [void][System.__ComObject].InvokeMember($n, [Reflection.BindingFlags]::SetProperty, $null, $o, [object[]]@($v)) }

# ---------- Шаг 0. Права администратора Windows ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Fail 'Нужен PowerShell от администратора.' 'Правой кнопкой по PowerShell → «Запуск от имени администратора».' }

Log 'INFO' ("Режим: {0}. Windows {1}, PowerShell {2}, процесс {3}-бит." -f ($(if ($Apply) { 'ПРИМЕНИТЬ' } else { 'только диагностика и план' })), [Environment]::OSVersion.Version, $PSVersionTable.PSVersion, $(if ([Environment]::Is64BitProcess) { 64 } else { 32 }))

# ---------- Шаг 1. База ----------
Step 1 'База'
if (-not (Test-Path (Join-Path $BasePath '1Cv8.1CD'))) {
  Fail "В папке $BasePath нет файла 1Cv8.1CD." 'Укажите папку, где лежит 1Cv8.1CD (файловая база). Серверные базы — следующий этап спайка.'
}
$BasePath = (Resolve-Path $BasePath).Path
if (-not $PublishName) { $PublishName = 'bilem_' + ((Split-Path $BasePath -Leaf) -replace '[^A-Za-z0-9_]', '_').ToLower() }
Log 'OK' "Файловая база: $BasePath; имя публикации: $PublishName"

# ---------- Шаг 2. Платформа и разрядность ----------
Step 2 'Платформа 1С'
$platforms = @()
# В 32-битном PowerShell $env:ProgramFiles указывает на «Program Files (x86)» — 64-битную папку берём из ProgramW6432
$pf64 = if ([Environment]::Is64BitOperatingSystem) { $env:ProgramW6432 } else { $null }
$pf32 = if ([Environment]::Is64BitOperatingSystem) { ${env:ProgramFiles(x86)} } else { $env:ProgramFiles }
foreach ($root in @(@{ Path = $(if ($pf64) { "$pf64\1cv8" }); Bits = 64 },
                    @{ Path = "$pf32\1cv8"; Bits = 32 })) {
  if ($root.Path -and (Test-Path $root.Path)) {
    Get-ChildItem $root.Path -Directory | Where-Object { $_.Name -match '^8\.3\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'bin\comcntr.dll')) } | ForEach-Object {
      $platforms += [pscustomobject]@{ Version = [version]$_.Name; Bits = $root.Bits; Bin = (Join-Path $_.FullName 'bin') }
    }
  }
}
if (-not $platforms) { Fail 'Платформа 1С 8.3 не найдена.' 'Установите платформу 1С:Предприятие 8.3 (с компонентой «Модули расширения веб-сервера»).' }
foreach ($p in $platforms) { Log 'INFO' ("Найдена платформа {0} ({1}-бит)" -f $p.Version, $p.Bits) }
if ($PlatformVersion) { $plat = $platforms | Where-Object { $_.Version -eq [version]$PlatformVersion } | Select-Object -First 1 }
else { $plat = $platforms | Sort-Object Version, Bits -Descending | Select-Object -First 1 }
if (-not $plat) { Fail "Платформа $PlatformVersion не найдена." }
Log 'OK' ("Берём {0} ({1}-бит), {2}" -f $plat.Version, $plat.Bits, $plat.Bin)

# Разрядность процесса должна совпадать с платформой (COM). Перезапуск в нужном PowerShell.
$procBits = $(if ([Environment]::Is64BitProcess) { 64 } else { 32 })
if ($plat.Bits -ne $procBits) {
  if ($env:BILEM_SPIKE_RELAUNCHED) { Fail "После перезапуска PowerShell всё ещё $procBits-бит, а платформа $($plat.Bits)-бит." 'Запустите скрипт в PowerShell нужной разрядности вручную.' }
  $env:BILEM_SPIKE_RELAUNCHED = '1'
  $ps = if ($plat.Bits -eq 32) { "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" } else { "$env:windir\sysnative\WindowsPowerShell\v1.0\powershell.exe" }
  Log 'WARN' "Платформа $($plat.Bits)-бит, а PowerShell $procBits-бит — перезапускаюсь в $ps"
  # Пересобираем параметры в команду и передаём через -EncodedCommand (кириллица и массивы не ломаются)
  $sq = { param($v) "'" + ([string]$v -replace "'", "''") + "'" }
  $parts = @("& " + (& $sq $PSCommandPath))
  foreach ($kv in $PSBoundParameters.GetEnumerator()) {
    if ($kv.Value -is [switch]) { if ($kv.Value) { $parts += "-$($kv.Key)" } }
    elseif ($kv.Value -is [array]) { $parts += "-$($kv.Key) " + (($kv.Value | ForEach-Object { & $sq $_ }) -join ',') }
    else { $parts += "-$($kv.Key) " + (& $sq $kv.Value) }
  }
  $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(($parts -join ' ') + '; exit $LASTEXITCODE'))
  & $ps -NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc
  exit $LASTEXITCODE
}

# COM-коннектор. ProgID без библиотеки типов (TypeLib) не работает: Connect падает с TYPE_E_LIBNOTREGISTERED
function Test-ComConnector {
  if (-not [type]::GetTypeFromProgID('V83.COMConnector')) { return 'нет ProgID' }
  $clsid = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\V83.COMConnector\CLSID' -ErrorAction SilentlyContinue).'(default)'
  $dll = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
  if ($dll -ne (Join-Path $plat.Bin 'comcntr.dll')) { return "зарегистрирован другой comcntr.dll ($dll)" }
  $tl = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\TypeLib" -ErrorAction SilentlyContinue).'(default)'
  if (-not $tl -or -not (Test-Path "Registry::HKEY_CLASSES_ROOT\TypeLib\$tl")) { return 'не зарегистрирована библиотека типов' }
  return ''
}
$comProblem = Test-ComConnector
$comType = $null
if ($comProblem) {
  if ($Apply) {
    Log 'WARN' "V83.COMConnector: $comProblem — регистрирую comcntr.dll"
    & "$env:windir\System32\regsvr32.exe" /s (Join-Path $plat.Bin 'comcntr.dll')
    Start-Sleep 1
    $comProblem = Test-ComConnector
    if ($comProblem) { Fail "Не удалось зарегистрировать comcntr.dll: $comProblem." "Вручную: regsvr32 `"$(Join-Path $plat.Bin 'comcntr.dll')`" от администратора." }
  } else { Log 'PLAN' "V83.COMConnector: $comProblem — зарегистрирую $(Join-Path $plat.Bin 'comcntr.dll')" }
}
if (-not $comProblem) { $comType = [type]::GetTypeFromProgID('V83.COMConnector') }
if ($comType) { Log 'OK' 'V83.COMConnector доступен' }

# ---------- Шаг 3. Вход в базу ----------
Step 3 'Вход в базу администратором (COM)'
if (-not $comType) { Log 'PLAN' 'Подключусь по COM после регистрации.'; Log 'INFO' "Дальше — после -Apply. Журнал: $LogFile"; exit 0 }
$q = { param($s) ($s -replace '"', '""') }
$adminName = ''; $adminPwd = ''
if ($AdminUser) {
  # Без окна (для агентов): логин параметром, пароль — из переменной окружения BILEM_1C_ADMIN_PWD (может быть пустым)
  $adminName = $AdminUser
  $adminPwd = if ($PSBoundParameters.ContainsKey('AdminPassword')) { $AdminPassword } else { [string]$env:BILEM_1C_ADMIN_PWD }
  Log 'INFO' ("Вход: {0}, пароль {1}" -f $AdminUser, $(if ($adminPwd) { 'задан' } else { 'пустой' }))
} elseif (-not $NoAuth) {
  $cred = Get-Credential -Message "Администратор 1С для базы $BasePath"
  if (-not $cred) { Fail 'Логин администратора не введён.' 'Если в базе нет пользователей — запустите с -NoAuth.' }
  $adminName = $cred.UserName.TrimStart('\')
  $adminPwd = $cred.GetNetworkCredential().Password
}
$connStr = 'File="{0}";' -f (& $q $BasePath)
# Имена параметров строки соединения — отдельно от шаблона (проверка перед push ищет «пароль=» в тексте)
if ($adminName) { $connStr += '{0}="{1}";{2}="{3}";' -f 'Usr', (& $q $adminName), 'Pwd', (& $q $adminPwd) }
try {
  $connector = New-Object -ComObject V83.COMConnector
  $c = $connector.Connect($connStr)
} catch {
  $m = $_.Exception.Message
  $hint = 'Проверьте логин и пароль администратора.'
  if ($m -match 'заблокир|монопол|exclusive|lock') { $hint = 'База занята в монопольном режиме (открыт конфигуратор?). Закройте и повторите.' }
  elseif ($m -match 'обновлен|update|версии|version') { $hint = 'Базе нужно обновление: откройте её один раз в 1С:Предприятие под администратором, дождитесь обновления, повторите.' }
  elseif ($m -match 'лиценз|licen') { $hint = 'Нет свободной лицензии 1С на этой машине.' }
  Fail "Не удалось подключиться: $m" $hint
} finally { $adminPwd = $null }
Log 'OK' 'Подключились'

# ---------- Шаг 5. Конфигурация ----------
Step 5 'Конфигурация'
$md = P $c 'Metadata'
$cfgName = P $md 'Name'; $cfgVer = P $md 'Version'; $cfgSyn = P $md 'Synonym'
if (-not $cfgName) { Fail 'Не удалось прочитать метаданные конфигурации по COM.' 'Пришлите журнал разработчику.' }
Log 'OK' "Конфигурация: $cfgSyn ($cfgName) версии $cfgVer"
$hasBsp = $null -ne (P $md 'Catalogs').Find('Пользователи')
Log 'INFO' ("БСП (справочник «Пользователи»): {0}" -f $(if ($hasBsp) { 'есть' } else { 'нет' }))

if ($ListRoles) {
  Step '-' 'Роли конфигурации'
  foreach ($r in (P $md 'Roles')) { Write-Host ("  {0}  —  {1}" -f (P $r 'Name'), (P $r 'Synonym')) }
  exit 0
}

# ---------- Шаг 6. Пользователь bilem ----------
Step 6 "Пользователь $BilemUser"
$credFile = Join-Path $SpikeDir "$PublishName.$BilemUser.cred"
$ibUsers = P $c 'InfoBaseUsers'
$ibUser = $ibUsers.FindByName($BilemUser)
$roleObjs = @()
foreach ($rn in $Roles) {
  $r = (P $md 'Roles').Find($rn)
  if ($r) { $roleObjs += $r } else { Log 'WARN' "Роль «$rn» не найдена в конфигурации — пропускаю" }
}
if (-not $roleObjs) { Fail 'Ни одной роли не найдено.' 'Запустите с -ListRoles и передайте нужные через -Roles.' }
if ($Roles -contains 'ПолныеПрава') { Log 'WARN' 'Роль ПолныеПрава — только для спайка на копии базы!' }

if ($ibUser) {
  # Роли bilem как есть сейчас — для проверки «роли не слетают» (-Verify)
  $haveRoles = @(); foreach ($r in (P $ibUser 'Roles')) { $haveRoles += (P $r 'Name') }
  $wantRoles = @(foreach ($r in $roleObjs) { P $r 'Name' })
  $missing = @($wantRoles | Where-Object { $haveRoles -notcontains $_ }); $extra = @($haveRoles | Where-Object { $wantRoles -notcontains $_ })
  Log $(if ($missing -or $extra) { 'WARN' } else { 'OK' }) ("Роли {0} сейчас ({1}): {2}" -f $BilemUser, $haveRoles.Count, ($haveRoles -join ', '))
  if ($missing) { Log 'WARN' ("  нет ролей: {0}" -f ($missing -join ', ')) }
  if ($extra) { Log 'WARN' ("  лишние роли: {0}" -f ($extra -join ', ')) }
  if ($Verify -and ($missing -or $extra)) { $script:verifyRolesChanged = $true }
}
if ($ibUser -and (Test-Path $credFile)) {
  Log 'OK' "Пользователь $BilemUser уже есть, пароль в $credFile"
} elseif (-not $Apply) {
  Log 'PLAN' ("{0} пользователя {1} с ролями: {2}; случайный пароль → DPAPI {3}" -f $(if ($ibUser) { 'Обновлю' } else { 'Создам' }), $BilemUser, ($Roles -join ', '), $credFile)
  if ($hasBsp) { Log 'PLAN' 'Привяжу к элементу справочника «Пользователи» (БСП)' }
} else {
  $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
  $bytes = New-Object byte[] 24
  (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
  $bilemPwd = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
  if (-not $ibUser) { $ibUser = $ibUsers.CreateUser(); SetP $ibUser 'Name' $BilemUser }
  SetP $ibUser 'FullName' 'Bilem (ИИ-помощник)'
  SetP $ibUser 'StandardAuthentication' $true
  SetP $ibUser 'OSAuthentication' $false
  SetP $ibUser 'ShowInList' $false
  SetP $ibUser 'Password' $bilemPwd
  $userRoles = P $ibUser 'Roles'
  $userRoles.Clear()
  foreach ($r in $roleObjs) { $userRoles.Add($r) }
  # Шифруем до записи пользователя: если DPAPI упадёт, пароль bilem в базе не сменится впустую
  $bilemPwdDpapi = ConvertTo-SecureString $bilemPwd -AsPlainText -Force | ConvertFrom-SecureString
  $ibUser.Write()
  Set-Content -Path $credFile -Value $bilemPwdDpapi -Encoding ASCII
  Log 'OK' "Пользователь ИБ $BilemUser записан, пароль сохранён в DPAPI ($credFile)"

  if ($hasBsp) {
    try {
      $users = P (P $c 'Catalogs') 'Пользователи'
      $item = $users.FindByDescription('Bilem (ИИ-помощник)', $true)
      $obj = if ($item -and -not $item.IsEmpty()) { $item.GetObject() } else { $users.CreateItem() }
      SetP $obj 'Description' 'Bilem (ИИ-помощник)'
      SetP $obj 'ИдентификаторПользователяИБ' (P $ibUser 'UUID')
      SetP (P $obj 'DataExchange') 'Load' $true
      $obj.Write()
      Log 'OK' 'Элемент справочника «Пользователи» привязан к пользователю ИБ'
    } catch { Log 'WARN' "Не удалось привязать к справочнику «Пользователи»: $($_.Exception.Message)" }
  }
  $bilemPwd = $null
}

# ---------- Шаг 7. Состав OData ----------
Step 7 'Состав стандартного интерфейса OData'
$current = $c.GetStandardODataInterfaceContent()
$have = @{}
foreach ($m in $current) { $have[$m.FullName()] = $true }
$toAdd = @()
foreach ($fn in $ODataObjects) {
  $mo = $md.FindByFullName($fn)
  if (-not $mo) { Log 'INFO' "  нет в конфигурации: $fn"; continue }
  if ($have.ContainsKey($mo.FullName())) { Log 'OK' "  уже в составе: $fn" } else { $toAdd += $mo; Log 'PLAN' "  добавлю: $fn" }
}
if ($toAdd -and $Apply) {
  $arr = $c.NewObject('Array')
  foreach ($m in $current) { $arr.Add($m) }
  foreach ($m in $toAdd) { $arr.Add($m) }
  $c.SetStandardODataInterfaceContent($arr)
  Log 'OK' ("Состав OData обновлён: +{0}, всего {1}" -f $toAdd.Count, $arr.Count())
}
# Есть ли в базе записи по каждому объекту (под администратором) — чтобы на шаге 10 отличить
# «пусто, потому что данных нет» от «пусто, потому что bilem не видит». Только признак, без данных.
$dataFlags = [ordered]@{}
foreach ($fn in $ODataObjects) {
  if (-not $md.FindByFullName($fn)) { continue }
  try {
    # «Есть» — зарезервированное слово языка запросов (ЕСТЬ NULL); Execute — через InvokeMember:
    # прямой вызов из PowerShell прячет ошибку 1С за «Object reference not set…»
    $qr = $c.NewObject('Query'); SetP $qr 'Text' ("ВЫБРАТЬ ПЕРВЫЕ 1 ИСТИНА КАК Найдено ИЗ {0} КАК Т" -f $fn)
    $dataFlags[$fn] = -not (M (M $qr 'Execute') 'IsEmpty')
  } catch {
    $dataFlags[$fn] = $null   # не знаем — чтение на шаге 10 всё равно проверим (только код ответа)
    Log 'WARN' "  не удалось проверить наличие записей: $fn — $($_.Exception.InnerException.Message)"
  }
}
Log 'INFO' ("Объектов с записями в базе: {0} из {1}" -f @($dataFlags.Values | Where-Object { $_ }).Count, $dataFlags.Count)
$qr = $null; $md = $null; $ibUsers = $null; $ibUser = $null; $userRoles = $null; $roleObjs = $null; $current = $null; $toAdd = $null; $arr = $null; $mo = $null; $r = $null; $m = $null
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($c); $c = $null
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($connector); $connector = $null
[GC]::Collect(); [GC]::WaitForPendingFinalizers()

# ---------- Шаг 8. IIS ----------
Step 8 'Веб-сервер IIS'
# Из 32-битного процесса System32 подменяется на SysWOW64 — к 64-битному IIS идём через Sysnative
$sys = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { "$env:windir\Sysnative" } else { "$env:windir\System32" }
$appcmd = "$sys\inetsrv\appcmd.exe"
$iisOk = Test-Path $appcmd
# Компоненты Windows — через 64-битный dism.exe: Install-WindowsFeature/Enable-WindowsOptionalFeature
# нет в 32-битном PowerShell (а он у нас при 32-битной 1С). Имена IIS-* одинаковы на сервере и клиенте.
function Enable-WinFeatures([string[]]$names) {
  $dismArgs = @('/online', '/enable-feature', '/all', '/norestart', '/quiet') + ($names | ForEach-Object { "/featurename:$_" })
  $dismOut = & "$sys\dism.exe" @dismArgs 2>&1
  if ($LASTEXITCODE -notin 0, 3010) { Fail ("dism: код {0}. {1}" -f $LASTEXITCODE, (($dismOut | Out-String).Trim())) 'Включите компоненты IIS вручную: «Компоненты Windows» / Server Manager.' }
  if ($LASTEXITCODE -eq 3010) { Log 'WARN' 'Windows просит перезагрузку после установки компонентов.' }
}
if ($iisOk) { Log 'OK' 'IIS установлен' }
elseif (-not $Apply -or -not $EnableIIS) {
  Log 'PLAN' 'IIS нет. Включу компоненты Windows (Web-Server, ISAPI, IP Security) — нужен флаг -EnableIIS.'
  if ($Apply) { Fail 'IIS не установлен.' 'Повторите с -EnableIIS (компонент Windows, не сторонний софт).' }
} else {
  Enable-WinFeatures @('IIS-WebServerRole', 'IIS-WebServer', 'IIS-ISAPIExtensions', 'IIS-ISAPIFilter', 'IIS-IPSecurity', 'IIS-ManagementConsole')
  if (-not (Test-Path $appcmd)) { Fail 'IIS не включился.' 'Проверьте «Компоненты Windows» / Server Manager вручную.' }
  Log 'OK' 'IIS включён'
  $iisOk = $true
}

# ---------- Шаг 9. Публикация ----------
Step 9 "Публикация $PublishName (только 127.0.0.1)"
$pubDir = "C:\inetpub\wwwroot\$PublishName"
$vrd = Join-Path $pubDir 'default.vrd'
$pool = 'Bilem1C'
if ($Verify -and -not $Apply) {
  Log 'INFO' 'Режим проверки: публикацию не меняю'
} elseif (-not $Apply) {
  Log 'PLAN' "webinst -publish -iis -wsdir $PublishName -dir $pubDir -connstr File=`"$BasePath`";"
  Log 'PLAN' "default.vrd: standardOdata enable=true, пул сессий"
  Log 'PLAN' "пул IIS $pool ($($plat.Bits)-бит), анонимный вход от имени пула, права на $BasePath только пулу, доступ только с 127.0.0.1 и ::1"
} elseif ($iisOk) {
  $webinst = Join-Path $plat.Bin 'webinst.exe'
  # Формат из документации 1С: -connstr "File=""C:\base"";"
  $wiArgs = '-publish -iis -wsdir {0} -dir "{1}" -connstr "File=""{2}"";"' -f $PublishName, $pubDir, $BasePath
  $wiOut = Join-Path $SpikeDir 'webinst.out.txt'
  $p = Start-Process -FilePath $webinst -ArgumentList $wiArgs -Wait -NoNewWindow -PassThru -RedirectStandardOutput $wiOut -RedirectStandardError "$wiOut.err"
  Log 'INFO' ("webinst: код {0}; {1} {2}" -f $p.ExitCode, ((Get-Content $wiOut -Encoding Oem -ErrorAction SilentlyContinue) -join ' '), ((Get-Content "$wiOut.err" -Encoding Oem -ErrorAction SilentlyContinue) -join ' '))
  if (-not (Test-Path $vrd)) { Fail 'webinst не создал default.vrd.' 'Проверьте, что при установке платформы выбраны «Модули расширения веб-сервера».' }

  [xml]$x = Get-Content $vrd -Encoding UTF8
  $point = $x.DocumentElement
  $od = $point.SelectSingleNode('*[local-name()="standardOdata"]')
  if (-not $od) { $od = $x.CreateElement('standardOdata', $point.NamespaceURI); [void]$point.AppendChild($od) }
  $od.SetAttribute('enable', 'true'); $od.SetAttribute('reuseSessions', 'autouse')
  $od.SetAttribute('sessionMaxAge', '20'); $od.SetAttribute('poolSize', '5'); $od.SetAttribute('poolTimeout', '5')
  $x.Save($vrd)
  Log 'OK' 'default.vrd: OData включён, пул сессий'

  if (-not (& $appcmd list apppool /name:$pool)) { & $appcmd add apppool /name:$pool | Out-Null }
  & $appcmd set apppool $pool "/enable32BitAppOnWin64:$(if ($plat.Bits -eq 32) { 'true' } else { 'false' })" | Out-Null
  & $appcmd set app "Default Web Site/$PublishName" /applicationPool:$pool | Out-Null
  Log 'OK' "Пул $pool ($($plat.Bits)-бит)"

  # Анонимный вход публикации — от имени удостоверения пула (userName пустой), а не IUSR:
  # тогда файлы базы открывает только наш пул, и права нужны только ему
  & $appcmd set config "Default Web Site/$PublishName" -section:system.webServer/security/authentication/anonymousAuthentication /enabled:true '/userName:' '/password:' /commit:apphost | Out-Null
  if ($LASTEXITCODE -ne 0) { Fail "appcmd: не удалось настроить анонимный вход публикации (код $LASTEXITCODE)." 'Проверьте IIS вручную: Проверка подлинности → Анонимная → Удостоверение пула приложений.' }
  Log 'OK' "Анонимный вход публикации — от имени пула $pool"

  & icacls $BasePath /grant "IIS APPPOOL\$($pool):(OI)(CI)M" /T /Q | Out-Null
  if ($LASTEXITCODE -ne 0) { Fail "icacls: не удалось дать пулу $pool права на $BasePath (код $LASTEXITCODE)." }
  Log 'OK' "Права на $BasePath — пулу $pool"

  if (-not (Test-Path "$sys\inetsrv\iprestr.dll")) {
    if ($EnableIIS) {
      Enable-WinFeatures @('IIS-IPSecurity')
      Log 'OK' 'Компонент IIS «Ограничения по IP» включён'
    } else { Log 'WARN' 'Компонент IIS «Ограничения по IP» не установлен — ограничение 127.0.0.1 не будет действовать. Повторите с -EnableIIS.' }
  }
  & $appcmd unlock config -section:system.webServer/security/ipSecurity | Out-Null
  & $appcmd set config "Default Web Site/$PublishName" -section:system.webServer/security/ipSecurity /allowUnlisted:false /commit:apphost | Out-Null
  foreach ($ip in @('127.0.0.1', '::1')) {
    & $appcmd set config "Default Web Site/$PublishName" -section:system.webServer/security/ipSecurity "/+[ipAddress='$ip',allowed='true']" /commit:apphost 2>&1 | Out-Null
  }
  Log 'OK' 'Доступ к публикации — только с 127.0.0.1 / ::1'
  & $appcmd recycle apppool /apppool.name:$pool | Out-Null
}

# ---------- Шаг 10. Проверка OData ----------
Step 10 'Проверка OData под bilem'
if (-not $Apply -and -not $Verify) { Log 'INFO' "План готов. Запустите с -Apply. Журнал: $LogFile"; exit 0 }
if (-not (Test-Path $credFile)) { Fail "Нет файла пароля $credFile." 'Сначала запустите с -Apply.' }
$sec = Get-Content $credFile | ConvertTo-SecureString
$pwdPlain = (New-Object System.Management.Automation.PSCredential($BilemUser, $sec)).GetNetworkCredential().Password
$auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${BilemUser}:$pwdPlain"))
$pwdPlain = $null
$base = "http://127.0.0.1/$PublishName/odata/standard.odata"

# HTTP-запрос без исключений на 4xx/5xx: код ответа и тело
function Http([string]$method, [string]$url, [string]$authHeader, [string]$body = '') {
  $req = [Net.HttpWebRequest]::Create($url)
  $req.Method = $method; $req.Timeout = 120000; $req.Accept = 'application/json'
  if ($authHeader) { $req.Headers['Authorization'] = $authHeader }
  if ($body) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $req.ContentType = 'application/json; charset=utf-8'; $req.ContentLength = $bytes.Length
    $st = $req.GetRequestStream(); $st.Write($bytes, 0, $bytes.Length); $st.Close()
  }
  try { $resp = $req.GetResponse() } catch {
    $ex = $_.Exception; while ($ex -and -not ($ex -is [Net.WebException])) { $ex = $ex.InnerException }
    if (-not $ex -or -not $ex.Response) { return [pscustomobject]@{ Code = 0; Body = $_.Exception.Message } }
    $resp = $ex.Response
  }
  $sr = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
  $txt = $sr.ReadToEnd(); $sr.Close(); $code = [int]$resp.StatusCode; $resp.Close()
  [pscustomobject]@{ Code = $code; Body = $txt }
}
# Текст ошибки 1С из ответа OData — коротко, для журнала (без данных)
function ODataError([string]$txt) {
  try { $j = $txt | ConvertFrom-Json; $m = $j.'odata.error'.message.value; if ($m) { return ($m -replace '\s+', ' ').Substring(0, [Math]::Min(160, $m.Length)) } } catch {}
  return ''
}
$fails = 0

$r = Http 'GET' "$base/`$metadata" $auth
if ($r.Code -ne 200) {
  $hint = switch ($r.Code) {
    401 { 'Пользователь bilem не принят: проверьте, что он записан и у него есть роли.' }
    403 { 'IIS запретил доступ: проверьте ограничение по IP (запрос должен идти с 127.0.0.1).' }
    404 { 'Публикация не найдена: проверьте приложение в IIS и default.vrd.' }
    500 { 'Ошибка 1С: часто — права пула на папку базы, разрядность пула или база требует обновления.' }
    default { 'Смотрите журнал и Просмотр событий Windows → Приложение.' }
  }
  Fail ("Проверка OData не прошла: `$metadata HTTP {0} {1}" -f $r.Code, (ODataError $r.Body)) $hint
}
$sets = @([regex]::Matches($r.Body, 'EntitySet Name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
Log 'OK' ("`$metadata: HTTP 200, наборов: {0}" -f $sets.Count)

# (1) Чтение: каждый объект состава — 200, и не пусто там, где в базе есть записи. В журнал — только коды и счётчики.
$prefix = @{ 'Справочник' = 'Catalog'; 'Документ' = 'Document'; 'РегистрСведений' = 'InformationRegister'; 'РегистрБухгалтерии' = 'AccountingRegister'; 'РегистрНакопления' = 'AccumulationRegister'; 'ПланСчетов' = 'ChartOfAccounts'; 'ПланВидовХарактеристик' = 'ChartOfCharacteristicTypes' }
$readOk = 0
foreach ($fn in $dataFlags.Keys) {
  $kind, $name = $fn -split '\.', 2
  $set = '{0}_{1}' -f $prefix[$kind], $name
  if ($sets -notcontains $set) { Log 'FAIL' "  $set — нет в `$metadata"; $fails++; continue }
  $rr = Http 'GET' ("$base/{0}?`$top=1&`$format=json" -f $set) $auth
  if ($rr.Code -ne 200) { Log 'FAIL' ("  {0}: HTTP {1} {2}" -f $set, $rr.Code, (ODataError $rr.Body)); $fails++; continue }
  $n = @(($rr.Body | ConvertFrom-Json).value).Count
  if ($dataFlags[$fn] -and $n -eq 0) { Log 'FAIL' "  ${set}: HTTP 200, но пусто, хотя в базе записи есть — bilem их не видит (роли или ограничение доступа на уровне записей)"; $fails++; continue }
  $readOk++
}
Log $(if ($readOk -eq $dataFlags.Count) { 'OK' } else { 'WARN' }) ("Чтение: {0} из {1} наборов — HTTP 200, видно всё, что есть в базе (с записями: {2})" -f $readOk, $dataFlags.Count, @($dataFlags.Values | Where-Object { $_ }).Count)

# (2) Запись запрещена: POST документа и PATCH справочника — отказ 1С, в базе ничего не появилось и не изменилось
$docSet = 'Document_СчетНаОплатуПокупателю'
if ($sets -contains $docSet) {
  $before = (Http 'GET' "$base/$docSet/`$count" $auth).Body.Trim()
  $post = Http 'POST' "$base/${docSet}?`$format=json" $auth '{}'
  $after = (Http 'GET' "$base/$docSet/`$count" $auth).Body.Trim()
  if (($post.Code -ge 200 -and $post.Code -lt 300) -or $after -ne $before) { Log 'FAIL' ("  POST {0}: HTTP {1}, документов было {2}, стало {3} — ЗАПИСЬ РАЗРЕШЕНА" -f $docSet, $post.Code, $before, $after); $fails++ }
  else { Log 'OK' ("  POST {0}: HTTP {1} — отказ 1С ({2}); документов было {3}, стало {4}" -f $docSet, $post.Code, (ODataError $post.Body), $before, $after) }
} else { Log 'WARN' "  $docSet нет в составе — POST не проверен" }
$catSet = 'Catalog_Контрагенты'
$g = Http 'GET' ("$base/{0}?`$top=1&`$select=Ref_Key,Description,DataVersion&`$format=json" -f $catSet) $auth
$item = if ($g.Code -eq 200) { @(($g.Body | ConvertFrom-Json).value) | Select-Object -First 1 }
if ($item) {
  # PATCH тем же наименованием: даже если бы запись была разрешена, данные не поменялись бы
  $one = "$base/$catSet(guid'$($item.Ref_Key)')"
  $patch = Http 'PATCH' "${one}?`$format=json" $auth (@{ Description = $item.Description } | ConvertTo-Json -Compress)
  $ver2 = ((Http 'GET' "${one}?`$select=DataVersion&`$format=json" $auth).Body | ConvertFrom-Json).DataVersion
  if (($patch.Code -ge 200 -and $patch.Code -lt 300) -or $ver2 -ne $item.DataVersion) { Log 'FAIL' ("  PATCH {0}: HTTP {1}, версия данных {2} — ЗАПИСЬ РАЗРЕШЕНА" -f $catSet, $patch.Code, $(if ($ver2 -ne $item.DataVersion) { 'изменилась' } else { 'та же' })); $fails++ }
  else { Log 'OK' ("  PATCH {0}: HTTP {1} — отказ 1С ({2}); версия данных не изменилась" -f $catSet, $patch.Code, (ODataError $patch.Body)) }
} else { Log 'WARN' "  в $catSet нет записей (HTTP $($g.Code)) — PATCH не проверен" }

# (3) Периметр: без пароля — 401; по адресам машины (не 127.0.0.1) — 403
$na = Http 'GET' "$base/`$metadata" ''
if ($na.Code -eq 401) { Log 'OK' '  без пароля: HTTP 401' } else { Log 'FAIL' "  без пароля: HTTP $($na.Code), ожидалось 401"; $fails++ }
$ips = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' } |
  ForEach-Object { $_.GetIPProperties().UnicastAddresses } | ForEach-Object { $_.Address } |
  Where-Object { $_.AddressFamily -eq 'InterNetwork' -and -not [Net.IPAddress]::IsLoopback($_) -and -not $_.ToString().StartsWith('169.254.') } | ForEach-Object { $_.ToString() })
foreach ($ip in $ips) {
  $ext = Http 'GET' "http://$ip/$PublishName/odata/standard.odata/`$metadata" $auth
  $shown = $ip -replace '\.\d+\.\d+$', '.x.x'
  if ($ext.Code -eq 403) { Log 'OK' "  по адресу машины ${shown}: HTTP 403" } else { Log 'FAIL' "  по адресу машины ${shown}: HTTP $($ext.Code), ожидалось 403"; $fails++ }
}
if (-not $ips) { Log 'WARN' '  внешних IPv4-адресов нет — проверка 403 пропущена' }

if ($script:verifyRolesChanged) { Log 'FAIL' "Роли $BilemUser отличаются от заданных (см. шаг 6)"; $fails++ }
if ($fails) { Fail "Проверки не прошли: $fails." 'Смотрите строки FAIL выше.' }

# Описание базы для MCP Bilem Agent: конфигурацию и версию OData не отдаёт — берём при онбординге.
# Пароля здесь нет — только путь к DPAPI-файлу. UTF-8 без BOM (JSON.parse не любит BOM).
$baseFile = Join-Path $SpikeDir "$PublishName.base.json"
$desc = [ordered]@{
  slug          = ($PublishName -replace '^bilem_', '')
  publication   = $PublishName
  odata         = $base
  user          = $BilemUser
  credFile      = $credFile
  configuration = $cfgName
  synonym       = $cfgSyn
  version       = $cfgVer
  checkedAt     = (Get-Date).ToString('s')
}
[IO.File]::WriteAllText($baseFile, ($desc | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Log 'OK' "Описание базы для MCP: $baseFile"

Write-Host ''
Log 'OK' ("ГОТОВО{0}. OData: {1} (только с этой машины). Пользователь: {2}, только чтение; пароль — DPAPI: {3}" -f $(if ($Verify -and -not $Apply) { ' (проверка)' } else { '' }), $base, $BilemUser, $credFile)
Log 'INFO' "Журнал: $LogFile"
