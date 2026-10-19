<p align="center">
  <img src="assets/readme-banner.png" alt="Bilem.ai — персональный агент для вашей 1С" width="100%">
</p>

# Bilem Agent

**Bilem Agent** — программа для Windows, которая подключает базы 1С к ИИ-ассистентам (Claude, ChatGPT, Cursor и другим) через протокол MCP. Директор спрашивает в чате: «сколько денег на счетах?», «кто нам должен?», «пришли акт сверки с ТОО …» — агент отвечает по данным 1С.

> **Программа не передаёт данные в сеть, кроме случаев, когда этого явно попросил пользователь** (письмо с документом, проверка обновлений на GitHub). Данные 1С читаются только на этом компьютере.

Сайт и документация: [bilem.ai](https://bilem.ai) · [docs.bilem.ai](https://docs.bilem.ai) · вопросы — [GitHub Issues](https://github.com/skp-kz/bilem.ai/issues) и [Обсуждения](https://github.com/skp-kz/bilem.ai/discussions).

## Что умеет

- **Отвечает по данным 1С**: деньги на счетах и в кассах, контрагенты, документы — с датой «данные на …» (учёт в 1С отстаёт от жизни, агент говорит об этом честно).
- **Печатает документы, которые уже есть в 1С**: счёт на оплату, доверенность (форма Д-1), акт сверки — PDF с логотипом, печатью и подписью вашей организации.
- **Отправляет их по почте** через ваш почтовый ящик (SMTP): счета и акты — на любой адрес, доверенности — только на ваши адреса.
- **Вы решаете, что видит ИИ**: вкладка «Доступ» — дерево разделов как в меню вашей 1С; зарплата и кадры (персональные данные) закрыты.

**Bilem не меняет ваш бухгалтерский учёт.** Агент читает данные; проведения и удаления документов в программе нет.

## Что программа меняет в системе

| Где | Что |
| --- | --- |
| Папка программы | `C:\Program Files\Bilem Agent` (установщик MSI) |
| Служба Windows | **«Bilem Agent»** (`BilemAgent`): MCP-сервер работает сам после перезагрузки, без входа на сервер. Запуск — автоматически (отложенный), учётная запись LocalSystem, при сбое — перезапуск через минуту |
| Автозапуск | окно в трее при входе любого пользователя (`HKLM\…\Run`) — без прав администратора, только показывает состояние |
| Данные агента | `C:\ProgramData\Bilem` — настройки подключённых баз, токен MCP, печати и подписи, настройки почты, журналы. Доступ — только Администраторы и SYSTEM; пароли — зашифрованы для этого компьютера (DPAPI) |
| Документы | `C:\ProgramData\Bilem\Документы\<база>\…` — PDF счетов, доверенностей, актов сверки (папку можно сменить в «Печатных формах»). Читать — «Пользователи», писать — Администраторы и служба |
| Базы 1С | в каждой подключённой базе — пользователь `bilem` с правами только на чтение (роли подбираются самые узкие) и состав стандартного интерфейса OData |
| IIS | публикация базы `bilem_<имя>` со своим пулом приложений; OData доступен **только с этого компьютера** (127.0.0.1) |
| Сеть | MCP-сервер агента — только `127.0.0.1:8796`, вход по токену |

Расширения в базу 1С агент не ставит, конфигурацию не меняет.

## Требования

- Windows 10 / Server 2012 R2+, **.NET Framework 4.8 (установщик предложит поставить)**, права администратора для установки и настройки;
- платформа 1С:Предприятие 8.3 с компонентой «Модули расширения веб-сервера»;
- IIS (агент включит его сам);
- файловая база 1С и логин администратора 1С. Проверено на «Бухгалтерии для Казахстана» 3.0.

## Установка

1. Скачайте `BilemAgent-<версия>.msi` со страницы [Releases](https://github.com/skp-kz/bilem.ai/releases) и запустите (см. ниже, если Windows предупреждает).
2. Откройте «Bilem Agent» (ярлык на рабочем столе; окно открывается без прав администратора и показывает состояние службы) → **«Настройки…»** (попросит права администратора) → **Базы 1С → Добавить…** — выберите базу, введите логин и пароль администратора 1С (пароль не сохраняется).
3. **MCP → Подключить к Claude Code** (или Claude Desktop, Cursor) — и спрашивайте. MCP работает в службе: после перезагрузки сервера входить на него не нужно.

### Подлинность установщика

Начиная с 0.3.0-beta.9 установщик и программы подписаны сертификатом Microsoft на **SKP.KZ LLC**: «Свойства» файла → «Цифровые подписи». Кроме того, хеш MSI можно сверить с `SHA256SUMS` в том же релизе: `Get-FileHash .\BilemAgent-<версия>.msi`.

Подробно — [docs.bilem.ai](https://docs.bilem.ai). Проверка подлинности: в релизе есть `SHA256SUMS` и подпись `SHA256SUMS.sig` (Ed25519); агент проверяет их сам при обновлении.

## Обновление

«Настройки…» → «Сервис» → «Обновления» → «Проверить сейчас» → «Обновить». Агент скачивает MSI с GitHub, проверяет подпись и хеш, закрывается, ставит новую версию (служба на это время останавливается) и запускается снова; базы, токен MCP, печатные формы и почта остаются. Предыдущая версия хранится — «Откатить на …». Бета-версии — галочка «Получать бета-версии».

## Удаление

1. В агенте для каждой базы — **Базы 1С → Удалить** (убирает публикацию IIS и пул; с галочкой — и пользователя `bilem` в базе). То же из командной строки: `bilem-agent remove --slug <имя> [--drop-user --admin <логин>]`.
2. «Параметры Windows» → «Приложения» → **Bilem Agent → Удалить** — удаляются и служба, и автозапуск окна.
3. Данные `C:\ProgramData\Bilem` при удалении остаются (на случай переустановки). Удалить и их: `msiexec /x BilemAgent-<версия>.msi REMOVE_SETTINGS=1` или удалите папку вручную.

## Безопасность

Как сообщить об уязвимости — [SECURITY.md](SECURITY.md). Лицензия — [MIT](LICENSE).

## Для разработчиков

C#, .NET Framework 4.8 (WinForms + командная строка), сборка `dotnet build Bilem.Agent.sln`, тесты `dotnet test tests/Bilem.Agent.Core.Tests`, MSI — WiX 5 (`installer/`), релиз по тегу `vX.Y.Z` — GitHub Actions (`.github/workflows/release.yml`). Подробности — [docs/development.md](docs/development.md).

---

## English

**Bilem Agent** is a Windows app that connects 1C:Enterprise databases (1C:Accounting for Kazakhstan) to AI assistants (Claude, ChatGPT, Cursor) over MCP. It reads data through the standard OData interface published **for this machine only** (127.0.0.1), prints documents that already exist in 1C (invoice, power of attorney, reconciliation statement) and can e-mail them on request. It never posts, changes or deletes accounting documents.

> **The app does not send data over the network unless the user explicitly asks for it** (e-mailing a document, checking GitHub for updates).

Requirements: Windows 10 / Server 2012 R2+, .NET Framework 4.8 (the installer offers to install it). Install: download `BilemAgent-<version>.msi` from [Releases](https://github.com/skp-kz/bilem.ai/releases). Since 0.3.0-beta.9 the installer and binaries are Authenticode-signed by SKP.KZ LLC. The MCP server runs as the Windows service “Bilem Agent” (`BilemAgent`, LocalSystem, automatic delayed start) — it keeps working after a reboot without anyone signing in; the tray window runs without admin rights and asks for UAC only for settings. Uninstall: remove each database in the app (**Bases → Remove**), then uninstall “Bilem Agent” in Windows Settings; `C:\ProgramData\Bilem` is kept unless you run `msiexec /x … REMOVE_SETTINGS=1`. Releases are signed: `SHA256SUMS.sig` (Ed25519) over `SHA256SUMS`. License: MIT. Security: see [SECURITY.md](SECURITY.md).
