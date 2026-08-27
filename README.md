# MedSpravki-REU — реестр медсправок кафедры физкультуры РЭУ

Веб-приложение для преподавателей кафедры физического воспитания РЭУ им. Плеханова: вместо бумажной
стопки справок — реестр студентов со сроком допуска, группой здоровья, физкультурной группой и историей.
Плюс загрузка фото/PDF справки и локальное ИИ-распознавание, чтобы не вбивать поля руками.
Студенческий проект по ТЗ реального заказчика (зав. кафедрой), не продукт.

Если ты ищешь **быстрый запуск** — раздел «Как запустить». Если ищешь, **что тут не так** —
разделы «Два ТЗ» и «Ограничения и грабли»: там всё, обо что уже спотыкались.

---

## Статус на 2026-08-10

**Проект на паузе, но живой и развёрнутый.** Последний коммит с кодом — `e47cac2` от **2026-06-29**
(`ui(medspravki): убраны ручные кнопки ИИ у препода`). После него кода не касались: `be682a2`
(2026-07-05) завёл в git `ONBOARDING.md` и `_source/`, `23e1a28` (2026-07-21) — паспорт
`.proeb/project.json`, `ec44817` и `07a16e3` (2026-08-10) — этот README. Последняя сессия,
где проект был основным, — 2026-07-14. То есть **~6 недель без изменений кода**.

Что проверено прямо сейчас (2026-08-10, на этой машине, .NET SDK 8.0.127):

| Проверка | Команда | Результат |
|---|---|---|
| Сборка | `dotnet build ReuMedCertificates.sln` | ✅ 0 ошибок, 1 предупреждение (`CS0108` в `Pages/Error.cshtml.cs:10`) |
| Юнит-тесты | `dotnet test ReuMedCertificates.sln` | ✅ 14/14 пройдено |

Что работает по коду и по записям в `CLAUDE.md` (проверялось вживую на 2026-06-23, **с тех пор не
перепроверялось**):

- реестр `/registry` с поиском (pg_trgm), фильтрами, светофором статусов, типом справки и группой здоровья;
- карточка студента `/students/{id}` — текущая справка, история, правка
  `/students/{id}/certificates/{certId}/edit`, отзыв допуска;
- экран «Перед парой» `/before-class`;
- личный кабинет студента `/me` — до 5 файлов за раз, несколько фото склеиваются в один PDF
  (`ImageMagickDocumentAssembler`); загрузка преподавателем `/students/{id}/scans` — **по одному файлу**,
  без склейки;
- фоновое ИИ-распознавание (двухэтапное + голосование по дате), ручной зум по полю
  `/students/{id}/scans/{scanId}/zoom`;
- очередь заявок `/review` и `/submissions`, журнал аудита `/journal`, импорт реестра `/import`.

Чего **нет**:

- **CI отсутствует** — ни GitHub Actions, ни pre-commit, ни каких-либо `*.yml`/`Makefile` в проекте.
- **Интеграционных/E2E-тестов нет.** Есть один тест-проект `ReuMedCertificates.UnitTests` (14 тестов:
  статус справки + доменные правила авто-ревью). `Program.cs` заканчивается `public partial class Program;`
  с комментарием «доступна интеграционным тестам» — самих тестов не написали.
- **Импорта/экспорта Excel нет.** `ClosedXML 0.104.2` подключён в `ReuMedCertificates.Infrastructure.csproj:13`,
  но в коде **не используется ни разу** (`grep ClosedXML|XLWorkbook` по `src/` — пусто). Страница `/import`
  тянет реестр не из Excel, а из SQL-таблицы `onec_roster_demo` или 1С OData (`RosterOptions`).
- Каталог `src/ReuMedCertificates.Application/StudentPortal/` — **пустой**.

### Боевой контур (состояние на 2026-06-23, не верифицировано с тех пор)

Живёт **не на ноуте**, а на десктопе `castiel-pc`:

- systemd-сервис `reu-medspravki.service`, `WorkingDirectory=/home/Castiel/reu-medspravki-pub`,
  `ASPNETCORE_URLS=http://0.0.0.0:5080`, окружение **Development**;
- БД — docker-контейнер `reu-pg` (PostgreSQL 16, база `reu_med_certificates`);
- Ollama локально (vision-модель читает справки);
- публичный адрес `https://<ваш-узел>.ts.net` через **Tailscale Funnel**;
- деплой — `rsync` публикации с ноута + `pg_dump`/restore.

Подробности подключения, SSH и ролей — `ONBOARDING.md`.

---

## Два ТЗ — читать до того, как что-то писать

Это главный источник путаницы в проекте.

**Официальное ТЗ v1** (`_source/inputs/01_Официальное_ТЗ_МедСправки_РЭУ.md`, версия 1.0 от 29.04.2026) —
чисто **ручной** реестр. Раздел 5.2 «В первую версию не входят» прямо перечисляет:

> загрузка справок студентами; хранение сканов и фотографий справок; автоматическое распознавание
> текста, печатей и подписей; мобильное приложение; интеграция с внешними системами университета;
> сложная ролевая модель…

**Код в `app/` реализует v2** — то есть ровно то, что ТЗ v1 исключает: личный кабинет студента `/me`,
хранение сканов, ИИ-распознавание, роли Teacher/HeadOfDepartment/Admin/Student. И наоборот: из ТЗ v1
§5.1 **не сделан импорт/экспорт Excel**.

По записи в `CLAUDE.md` (2026-06-14) официальное **ТЗ v2 так и не пришло** — зав. кафедрой обещал «в
течение недели». Свежее записи об этом в репозитории нет. То есть развёрнутый функционал живёт
**впереди подписанного ТЗ**, и это осознанный риск владельца, а не недосмотр.

Второй документ, `_source/inputs/02_Подробное_техническое_ТЗ_и_архитектура_МедСправки_РЭУ.md` — это
проработка архитектуры, не подписанное ТЗ. Собственный большой план проекта — `docs/PLAN.md` (11 разделов).

---

## Как запустить

Предусловия: **.NET SDK 8** (`global.json` требует `8.0.100`, `rollForward: latestFeature`),
**PostgreSQL 16** (для запуска; для сборки и тестов БД не нужна).

```bash
cd projects/MedSpravki-REU/app

# сборка и тесты — БД не требуется
dotnet build ReuMedCertificates.sln
dotnet test  ReuMedCertificates.sln
```

База (вариант для разработки, из `app/RUNBOOK.md`):

```bash
docker run -d --name reu-pg \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=reu_med_certificates \
  -p 5432:5432 postgres:16
```

Строка подключения — `ConnectionStrings:DefaultConnection` в
`src/ReuMedCertificates.Web/appsettings.json` (дефолт: `Host=localhost;Port=5432;Database=reu_med_certificates`).
Design-time фабрика миграций читает также переменную `REU_DB_CONNECTION`.

Миграции применять вручную **не обязательно** — `Program.cs:108` вызывает `SeedIdentityAsync`, которая
делает `MigrateAsync()` и сидинг при старте. Вручную:

```bash
dotnet ef database update \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web

# новая миграция
dotnet ef migrations add <Name> \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web \
  --output-dir Persistence/Migrations
```

`dotnet-ef 8.0.4` прописан в `app/.config/dotnet-tools.json` → `dotnet tool restore`.

Запуск:

```bash
cd src/ReuMedCertificates.Web
dotnet run --urls http://localhost:5080
```

`Properties/launchSettings.json` в проекте **нет**, поэтому без `--urls` порт будет дефолтный
ASP.NET Core, а не 5080 — все записи в `CLAUDE.md`/`ONBOARDING.md` подразумевают именно 5080.

Публикация под сервис (как на боевом ПК, из `ONBOARDING.md`):

```bash
dotnet publish src/ReuMedCertificates.Web/ReuMedCertificates.Web.csproj \
  -c Release -o /home/Castiel/reu-medspravki-pub --nologo
sudo systemctl restart reu-medspravki
```

**Вход.** Bootstrap-пользователь создаётся, если `BootstrapUser:Enabled = true` (получает роли
`Admin` + `Teacher`). В `appsettings.json` он **выключен**, в `appsettings.Development.json` —
**включён**, логин `teacher`, пароль — только из `BootstrapUser__Password` в окружении.
> Значения пароля в репозитории нет намеренно: конфиг с ним уезжал в прод вместе со сборкой.
`SeedDemoData = false` → **реестр стартует пустым, это норма**: демо-студентов и демо-логины
`head`/`admin`/`student` сознательно убрали по просьбе заказчика. Справочники, которые сидятся
**всегда** (`SeedReferenceDataAsync`, идемпотентно) — 9 высших школ и 76 преподавателей кафедры
физвоспитания (реальный состав с rasp.rea.ru, проверен 2026-06-16). Учебные группы и студенты —
это уже демо-данные, при `SeedDemoData = false` их нет.

**Внешние бинарники** нужны для сканов и распознавания, приложение вызывает их через `Process.Start`:

- `pdftoppm` (poppler) — рендер PDF в изображение;
- `magick` (ImageMagick 7) с фолбэком на `convert` — склейка фото в PDF, кроп, предобработка.

Если их нет, поведение **не единообразно мягкое**. Вызовы `magick`/`convert` в предобработке и кропе идут
через `RunAsync` — тот ловит исключение, пишет warning и возвращает `false`, шаг просто пропускается.
А `pdftoppm` запускается напрямую (`LocalOllamaRecognitionProvider.cs:422`, `RegionRecognizer.cs:76`) и
без poppler бросает: авто-проверка скана падает целиком, исключение глотает только
`ScanProcessingBackgroundService` (`LogError`, «Авто-проверка скана … не удалась»). Склейка фото в PDF без
ImageMagick бросает `InvalidOperationException` (`ImageMagickDocumentAssembler.cs:36`), а
`Pages/Me/Index.cshtml.cs` её не ловит — студент получит 500. Само приложение при этом живо.

---

## Архитектура

Clean Architecture, 4 проекта + тестовый; solution — `app/ReuMedCertificates.sln`.

```
app/src/
  ReuMedCertificates.Domain/          # сущности (Student, MedicalCertificate, CertificateScan,
                                      #   Teacher, Department, StudyGroup, AuditLog, ImportBatch)
                                      #   + enum'ы + GetStatus() по датам
  ReuMedCertificates.Application/     # интерфейсы (IApplicationDbContext, IDocumentRecognitionService,
                                      #   IScanStorage, IRegionRecognizer, IRosterSource…),
                                      #   сервисы Registry/Students/Certificates/Scans/Roster/Audit,
                                      #   RecognitionRules (доменные правила авто-ревью)
  ReuMedCertificates.Infrastructure/  # ApplicationDbContext + Identity + DI + DataSeeder + миграции,
                                      #   LocalOllamaRecognitionProvider, ManualRecognitionProvider,
                                      #   FileScanStorage, ImageMagickDocumentAssembler, RegionRecognizer,
                                      #   SqlRosterSource / OneCODataRosterSource
  ReuMedCertificates.Web/             # Razor Pages (UI), Program.cs, appsettings*, wwwroot
app/tests/
  ReuMedCertificates.UnitTests/       # xunit + FluentAssertions, 14 тестов
```

**Точка входа** — `app/src/ReuMedCertificates.Web/Program.cs`. Там же: авторизационные политики
(`StaffOnly` / `AdminOrHead` / `StudentOnly` + `FallbackPolicy` = deny-by-default), security-заголовки
(CSP, `X-Frame-Options: DENY`, `nosniff`), лимиты загрузки, регистрация фоновой очереди распознавания
и два endpoint'а выдачи файла скана (`/scans/{id}/file` — сотрудникам с записью в аудит;
`/me/scans/{id}/file` — студенту только свой).

**Где состояние:**

| Что | Где |
|---|---|
| Основные данные | PostgreSQL, база `reu_med_certificates` (на проде — docker `reu-pg`) |
| Файлы сканов | на диске **вне wwwroot**, `Scans:StoragePath` (дефолт `App_Data/scans`) |
| Ключи шифрования полей | `App_Data/dpkeys` рядом с бинарником (`AddDataProtection().PersistKeysToFileSystem`, `Program.cs:69`) |
| Аудит действий | таблица `audit_logs` (`AuditEntryFactory`); задуман append-only, но INSERT-only права для роли БД — пока TODO из `RUNBOOK.md` |

**Жизненный цикл справки** (`VerificationStatus`): `Draft → NeedsReview → Verified → (Rejected | Expired | Revoked)`.
Источник черновика — отдельный enum `DraftSource`: `Manual | ExcelImport | StudentUpload | Ocr`.
Смысл разделения: официальным фактом считается только подтверждённое человеком решение, а откуда
пришли данные — ортогонально. Статус по сроку (`CertificateStatus`: Upcoming/Active/ExpiringSoon/EndsToday/Expired)
**не хранится в БД**, считается на лету в `MedicalCertificate.GetStatus()`.

**Конфигурация.** Осторожно: в `appsettings.json` лежат только `ConnectionStrings`, `BootstrapUser`
(`Enabled: false`), `ExpiringSoonThresholdDays`, `Serilog`, `AllowedHosts`. Секции `Scans` и `Recognition`
есть **только** в `appsettings.Development.json`, а секции `Roster` нет ни в одном файле — она целиком
живёт на дефолтах класса `Application/Common/RosterOptions.cs`. Полный набор ключей, которые читает код:

- `ConnectionStrings:DefaultConnection`, `BootstrapUser`, `SeedDemoData`, `ExpiringSoonThresholdDays` (7);
- `Scans` — `StoragePath`, `MaxUploadBytes` (10 МБ), `AllowedContentTypes` (PDF/JPEG/PNG), до 5 файлов в запросе;
- `Recognition` — `Provider` (`Manual` | `LocalOllama`), `OllamaUrl`, `VisionModel` (`qwen2.5vl:7b`),
  `TimeoutSeconds`, `PdfRenderDpi`, `TwoStage`, `VoteCount`, `Preprocess`;
- `Roster` — `Provider` (`Sql` | `OneC`) и параметры источника импорта реестра.

---

## ИИ-распознавание

`LocalOllamaRecognitionProvider` (481 строка) гоняет vision-модель через **локальный Ollama** — никаких
облаков, это требование 152-ФЗ/323-ФЗ, а не предпочтение.

- **Стадия 1** — модель читает справку целиком.
- **Стадия 2** (`Recognition:TwoStage`, дефолт `true`) — модель **сама перечитывает** ключевые поля
  (дата выдачи / номер / группа здоровья) отдельным фокус-запросом по каждой странице.
- **Голосование** по дате выдачи (`VoteCount`, дефолт 3) — несколько чтений с temp > 0, берём большинство.
- **Флаг неуверенности** — если голоса разошлись или стадии 1↔2 не сошлись, в `CertificateScan.AiNotes`
  пишется «⚠ Проверьте (распознано неуверенно): …», справка всё равно создаётся как лучшая догадка.
- **Детерминированные правила** вынесены из модели в `RecognitionRules.cs`: дисамбигуация «дата выдачи vs
  дата рождения» (разрыв ≥ 10 лет), «печать ИЛИ электронная подпись», срок обязателен только для допуска,
  правдоподобность даты (не в будущем, не старше 18 месяцев). Именно они покрыты юнит-тестами.
- Fallback для тяжёлых случаев — **ручной зум**: `/students/{id}/scans/{scanId}/zoom`, препод выделяет
  область мышью, сервер рендерит (`pdftoppm`) + режет с увеличением (`magick`) + узкий запрос к модели.

Вывод, добытый экспериментом (записан в `CLAUDE.md`): **«зум вниманием» надёжнее пиксельного кропа** —
тесный кроп теряет контекст и выдаёт мусор (в тесте дал 18.01.2019 / 05.12.2017), а фокус-запрос по
полной странице стабильно дал верное 22.11.2025. Поэтому авто-стадия 2 — это фокус-запросы **без** кропа.

Провайдер `Manual` (дефолт в `RecognitionOptions`) отключает ИИ полностью — система работоспособна
без GPU и Ollama.

---

## Ограничения и грабли

Всё ниже — реально случившееся, а не гипотетическое.

- **Данные уже теряли.** Работа сессии 2026-06-21 (реальные студенты, распознанные справки) исчезла
  при выключении компа: в `reu-pg` её не оказалось, append-only аудит обрывается на 2026-06-16. Причина —
  в базе не было колонки `Type`, приложение не стартовало против неё после 16-го, а `pg_dump`-бэкапа не было.
  **Перед рисковыми правками БД делай дамп:**
  `docker exec reu-pg pg_dump -U postgres reu_med_certificates | gzip > ~/reu-pg-backup-$(date +%Y%m%d).sql.gz`
- **Медданные на публичном адресе за дефолтным паролем.** Боевой контур — Tailscale **Funnel**, то есть
  открытый интернет, а `BootstrapUser` — `teacher` с паролем из окружения (`BootstrapUser__Password`).
  Это спецкатегория ПДн (152-ФЗ ст. 10) + врачебная тайна (323-ФЗ ст. 13). Пароль надо сменить;
  альтернатива — приватный `tailscale serve` (только внутри тайнета).
- **Прод крутится в окружении `Development`.** `ASPNETCORE_ENVIRONMENT` на сервисе — Development,
  значит `UseExceptionHandler("/Error")` и `UseHsts()` (`Program.cs:101-105`) **не включаются**,
  а конфиг берётся из `appsettings.Development.json`.
- **Публичный URL держится на VPN.** `https://<ваш-узел>.ts.net` работает, только пока на ПК
  подключён HAPP (HubVPN): РФ-DPI душит соединения Tailscale с его серверами, нода отваливается каждые
  ~2 минуты. Диагностика — `ip link show tun0` (должен существовать) и `tailscale funnel status`.
  Внутри тайнета/по SSH сайт доступен всегда. Подробности — `ONBOARDING.md` §7.
- **Рабочая копия на боевом ПК не под git.** `/home/Castiel/app` — без истории и отката (см. `ONBOARDING.md` §8).
  В этом вольте лежит **своя** копия (`app/`, 125 файлов под git) — легко разъехаться. Не путать также
  дев-инстанс на ноуте (свой контейнер `reu-pg` на 5432 + приложение на localhost:5080) с боевым на
  `castiel-pc`.
- **7B-модель ошибается на рукописи** — даты, номер, группа. Из-за этого и появилась кнопка
  «Изменить / поправить»: правка преподавателем = подтверждение человеком (→ `Verified`).
  Подделку модель не ловит — это первый фильтр и автозаполнение, а не проверка подлинности.
  Анти-фрод «подлинности» сознательно вырезан как иллюзия.
- **У справки типа «Бассейн» группа здоровья не подставляется** — там своя шкала (группы А/Б),
  римские I–V к ней не применяются.
- **Авто-ревью различает три исхода**, и их легко перепутать: «не допущен» — это валидная справка с
  `MedicalCertificate.Admitted = false` (напр. аллергия на хлор), а не брак; брак (печать/срок/ФИО) —
  это отказ **скана**, справка не создаётся; «нет справки» — третье. В реестре это статусы
  «Не допущен» / «Заявка отклонена» / «Нет справки».
- **Авто-выставление оценок запрещено** (ФЗ-273) — human-in-the-loop обязателен по замыслу.
- **Скаффолд `_source/scaffold/ReuMedCertificates/` не компилируется** — `Domain`/`Application` пусты,
  а `Program.cs`/DI уже ссылаются на несуществующие типы. Это исторический артефакт, **не трогай его**,
  рабочий код — только в `app/`.
- Ноут ≠ сервер: планировщики и таймеры на ноуте ненадёжны, бэкап БД делать руками или на боевом ПК.

---

## Документы проекта

| Файл | Что внутри |
|---|---|
| `CLAUDE.md` | текущий статус в деталях, что сделано в последних сессиях, ключевые факты, не выводимые из кода |
| `ONBOARDING.md` | онбординг второго разработчика: SSH, пути на боевом ПК, деплой, правила совместной работы |
| `app/RUNBOOK.md` | сборка / миграции / запуск (написан на этапе Фазы 0 — раздел «Что готово» устарел, команды актуальны) |
| `app/README.md` | **устарел**: описывает каркас до реализации UI («Следующие шаги: реализовать CRUD-страницы, импорт Excel»). Ориентируйся на этот файл, не на него |
| `docs/PLAN.md` | большой план, 11 разделов (синтез 9-агентного анализа + второе мнение Codex) |
| `docs/AUDIT-v2-2026-06-15.md` | аудит v2 |
| `docs/SCENARIOS-2026-06-15.md` | пользовательские сценарии |
| `docs/SECURITY-COMPLIANCE-RESEARCH-2026-06-16.md` | разбор 152-ФЗ / 323-ФЗ и модель безопасности (~380 КБ) |
| `_source/inputs/` | оба ТЗ (md) + исходный docx заказчика |
| `_source/mockups/`, `_source/sample-certs/` | фото «Расписания» кафедры, рисованный вайрфрейм, образец справки 086/у |
| `assets/concepts/`, `assets/screenshots/` | 4 фронт-концепта (рекомендован был №1 «Кафедральный синий», но в код он не пошёл — см. ниже) и скриншоты живого UI |

**Фронт — свой CSS без фреймворков.** Единственный файл стилей `app/src/ReuMedCertificates.Web/wwwroot/css/site.css`
описан в шапке как «Тема РЭУ им. Г.В. Плеханова — 1:1 с порталом student.rea.ru»: PT Sans, тёмно-синий
`#0B2D50`, контейнер 980px. **HTMX и Bootstrap в коде отсутствуют** — ни пакета, ни CDN, ни одного
атрибута `hx-*`; во всём `wwwroot/` лежат ровно два файла (`css/site.css`, `images/reu-logo.svg`).
HTMX + Bootstrap 5.3 стоят в стеке архитектурного документа `_source/inputs/02_Подробное_техническое_ТЗ…md`
(строки 68–69) и в `CLAUDE.md` — но в официальном ТЗ v1 их нет, и в коде их нет: это план. Концепт «Кафедральный
синий» был вытеснен темой РЭУ ещё в июне (`2672a8d`, `1a28ba0`, `3095d6c`): из его палитры
`#EAF1F9 #1F5BA8 #0E3A6B #1F2A37 #2E9E5B #C9342E` в `site.css` дожили только `#2e9e5b` (ок) и
`#c9342e` (опасность).
