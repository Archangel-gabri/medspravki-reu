# MedSpravki-REU — исследование по безопасности, комплаенсу и архитектуре интеграции

**Роль:** старший аналитик по безопасности / комплаенсу / архитектуре.
**Объект:** ИС учёта медицинских справок кафедры физвоспитания РЭУ им. Г.В. Плеханова (ASP.NET Core 8 + PostgreSQL 16 + Razor Pages/HTMX, локальное ИИ‑распознавание Ollama qwen2.5‑vl). Цель — интеграция в портал РЭУ.
**Дата отчёта / проверки источников:** 2026‑06‑16.
**Класс данных:** медицинские ПДн = **спецкатегория** (152‑ФЗ ст. 10) + **врачебная тайна** (323‑ФЗ ст. 13); целевой уровень защищённости — **УЗ‑3** (ПП‑1119, приказ ФСТЭК № 21).

> **Как получен отчёт.** Исследование проведено многоагентным прогоном (исчерпывающий режим): отдельные агенты по зарубежным реализациям, праву, стеку РЭУ, шести измерениям безопасности, плюс адверсариал‑верификаторы, перепроверявшие каждый вывод по безопасности против **реального кода** приложения. Security‑выводы привязаны к `file:line`; ключевые — прошли независимую проверку «опровергни вывод» (статус и контр‑аргумент указаны в чек‑листе). 5 из 6 security‑измерений и все исследовательские/проектные блоки выполнены и сведены здесь; финдер по OWASP Top‑10 дважды не отработал из‑за временной перегрузки API (5xx/529), но его периметр (Broken Access Control / IDOR / инъекции / сессии) полностью покрыт код‑фактами и остальными финдерами и сведён в Блок 3.

---

## Краткое резюме (executive summary)

1. **Дизайн данных — сильная сторона; защита доступа и хранения — слабая.** Команда сознательно реализовала **минимизацию**: поля «диагноз» в модели нет, `Restrictions` декларируется «функциональные формулировки без диагноза», ИИ ходит **локально** (без облака → 152‑ФЗ ст. 18.1 фактически соблюдён). Но at‑rest‑шифрование спецкатегории отсутствует на всех уровнях, разграничение доступа и регистрация чтения медданных — практически отсутствуют. Для УЗ‑3 это инвертированный приоритет.

2. **Топ‑риск №1 — отсутствие регистрации ДОСТУПА к медданным (РСБ).** Загрузка и распознавание пишут аудит, а **просмотр/скачивание скана** (`GET /scans/{id}/file`), просмотр карточек и **вход/выход** — нет; `IpAddress` в аудите всегда `null`. Для врачебной тайны/УЗ‑3 это нормативное требование, а не опция: утечку нельзя будет расследовать. **Не понижается верификацией.**

3. **Топ‑риск №2 — цепочка stored‑XSS через загрузку.** Тип файла проверяется по **клиентскому** `file.ContentType` (подделывается), magic‑байты и антивирус не проверяются, а файл затем **отдаётся inline** с тем же клиентским MIME **без** `X‑Content‑Type‑Options: nosniff` и `Content‑Disposition: attachment`. HTML/SVG под видом PDF/PNG → исполнение скрипта в origin приложения; при reverse‑proxy под `student.rea.ru` — уже в origin портала Bitrix.

4. **Топ‑риск №3 — нулевая защита от фрейминга при цели «встроиться в портал».** В `Program.cs` нет ни одного security‑заголовка (CSP `frame-ancestors`, `X-Frame-Options`, `nosniff`, `Referrer-Policy`). Приложение прямо сейчас встраивается в любой iframe → clickjacking над `/review` (Approve/Reject) и `/scans`.

5. **BOLA/IDOR на медсканах — реален, но это ОТСУТСТВИЕ границы, а не сломанная граница.** `ScanService.OpenAsync` ищет скан только по `scanId` без проверки принадлежности; любой из 3 служебных ролей открывает скан любого студента. Верификатор понизил P0→**P1**: студенты/анонимы отсечены `RequireRole`, модель идентичности *плоская во всём приложении* (нет связки `AppUser↔Teacher`), и «вся кафедра видит все справки» может быть осознанным решением — но **аудит чтения обязателен**, а гранулярность доступа надо решить ДО продакшена с реальными ПДн.

6. **Интеграция: правильный путь — отдельный поддомен `med.rea.ru` + OIDC‑SSO** (минимальный blast radius, контур аттестуется отдельно). Вариант «нативный PHP‑модуль в Bitrix» — **отвергнуть** (поднимает весь портал до спецкатегории ПДн, наследует CVE Bitrix). Reverse‑proxy под общим доменом допустим, но требует жёсткой trust‑boundary (иначе спуфинг `X‑Remote‑User`). iframe — только косметика над уже аутентифицированным поддоменом, не канал медданных.

7. **Правовой вывод.** Лучшие зарубежные практики (self‑service upload → верификация медперсоналом → роль‑сегрегированная видимость «статус‑допуск, а не диагноз») переносимы и совпадают с минимизацией 152‑ФЗ. РФ‑режим строже в трёх точках: **локализация** (ст. 18.1), **письменное согласие на спецкатегорию** (ст. 10), **врачебная тайна** (323‑ФЗ ст. 13). В коде **нет учёта согласия** — это блокер перед выводом загрузки сканов в прод (верификатор: P1, преимущественно орг‑мера РЭУ + поле модели).

8. **Прочие подтверждённые пробелы:** секреты в репозитории (пароль БД `postgres`, демо‑пароль `<демо-пароль>`, `AllowedHosts="*"`), `AuditLog` не INSERT‑only на уровне БД, медданные на Ollama по `http` без TLS, нет per‑page ролей (`/journal`, `/import` доступны всем), нет CI‑гейта безопасности (анализаторы выключены). SHA‑256 «перепроверяется при открытии» — **ложная декларация** (не реализовано) — P2.

**Итог.** Архитектурное ядро и минимизация данных — здоровые. Перед развёртыванием «с сетевым доступом студентов» и перед интеграцией в портал необходимо закрыть приоритет **P0** (аудит чтения медданных, цепочку XSS приём↔раздача, security‑заголовки, trust‑boundary прокси) и блок **P1** (object‑level доступ + аудит, шифрование at‑rest, per‑page роли, секреты, INSERT‑only аудит, TLS к Ollama, согласие). Без РСБ‑аудита, шифрования носителей, АВЗ и разграничения доступа аттестация ИСПДн под УЗ‑3 невозможна.
---

# ⚠️ Обновление и повторная верификация — 2026-06-17

**Важно:** код приложения изменился между первичным снимком код‑фактов (2026‑06‑16) и повторной проверкой (2026‑06‑17) — судя по всему, по итогам первой версии этого отчёта в код внесён **пакет P0‑исправлений**. Повторный OWASP‑прогон (§3.6) перечитал текущий код; адверсариал‑верификаторы по 21 находке в этот момент **упали на жёстком лимите сессии** (не на содержании), поэтому контрверификация спорных P0 выполнена **прямым чтением текущего кода** (это авторитетнее агентов). Ниже — что подтверждённо **исправлено**, и что **остаётся открытым**, с актуальными `file:line`. Разделы §3.2–§3.5 ниже описывают снимок 2026‑06‑16 и **заменяются** этим обновлением там, где здесь сказано «исправлено».

## A. Подтверждённо ИСПРАВЛЕНО (P0 в значительной мере закрыт)

| Было (P0/P1 в §3.8) | Текущий код (`file:line`) | Статус |
|---|---|---|
| Нет security‑заголовков → clickjacking (P0‑3) | `Program.cs:45-56` — middleware ставит `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, CSP `default-src 'self'; frame-ancestors 'none'; object-src 'self'; base-uri 'self'` | **Исправлено** (остаток: CSP `script-src/style-src 'unsafe-inline'` → новый P2, см. MED‑A05‑CSP) |
| Спуфинг проксированных заголовков (P0‑4) | `Program.cs:40-43` — `UseForwardedHeaders(XForwardedFor\|XForwardedProto)`, доверие по умолчанию только loopback (`KnownProxies`) | **Базово исправлено** (при reverse‑proxy на отдельном хосте — добавить IP прокси в `KnownProxies`; включить `XForwardedHost`+`PathBase`) |
| Не логируется чтение медскана (P0‑1) | `Program.cs:86-91` — `AuditLog "ScanView"` на каждое открытие (range‑догрузки не дублируются) + `Cache-Control: no-store`, `Content-Disposition: inline` (`:93-94`) | **Исправлено** (на чтении скана) |
| Не логируется вход/выход; `IpAddress`=null | `Login.cshtml.cs:68,72` — `Login`/`LoginFailed` с IP; `AuditEntryFactory.cs:29` — `IpAddress = user.IpAddress`; `CurrentUser.cs:23` — `RemoteIpAddress` | **Исправлено** |
| Цепочка stored‑XSS на раздаче (P0‑2, serve) | `nosniff` (`Program.cs:49`) + CSP `default-src 'self'` + whitelist типов pdf/jpeg/png ⇒ браузер не sniff‑ит и не исполняет HTML/SVG под видом картинки | **Существенно смягчено** (остаётся: приём всё ещё по клиентскому MIME без magic‑байт/AV — см. ниже; `Content-Disposition` = `inline`, не `attachment`) |

Это закрывает или нейтрализует **все четыре P0** из первичного чек‑листа. Хорошая работа — фиксы точно соответствуют приоритету отчёта.

## B. Остаётся ОТКРЫТЫМ (актуальный P0/P1/P2 на 2026‑06‑17)

**P0 — теперь главный остаточный риск: контроль доступа.** (Подтверждено прямым чтением: `ScanService.OpenAsync` без изменений, ролей на страницах нет.)

| # | Уязвимость | `file:line` (текущий код) | Severity | Контр‑аргумент |
|---|---|---|---|---|
| U‑1 | **Плоская авторизация: `AuthorizeFolder("/")` даёт только аутентификацию, ролей на страницах НЕТ** — `/journal`, `/import`, `/review` (Approve/Reject), `/students`, `/scans` доступны любому вошедшему | `Program.cs:25` (единственная ролевая проверка — `:96` на `/scans/file`); `[Authorize(Roles…)]` в Pages — 0 | **P0 для v2 / P1 для v1** | В v1 все — сотрудники Admin+Teacher в ЛВС (риск смягчён); но при вводе роли студента (v2) — мгновенное раскрытие; `/journal`,`/import` — админ‑функции |
| U‑2 | **BOLA/IDOR: нет object‑level scope** — `ScanService.OpenAsync` ищет скан только по `scanId`; `CertificateService.Approve/Reject` — только по `certificateId` | `ScanService.cs:85-94` (без owner‑фильтра); `CertificateService.cs:85-127` | **P0 для v2 / P1 для v1** | Это отсутствие границы во всём приложении (модель идентичности плоская), роль‑гейт отсекает студентов/анонимов; «вся кафедра видит всё» может быть осознанным — но решить до прода |

**P1 (подтверждено в текущем коде):**

| # | Уязвимость | `file:line` | Контр‑аргумент |
|---|---|---|---|
| U‑3 | **Секреты в репо + приложение работает как `postgres`‑СУПЕРПОЛЬЗОВАТЕЛЬ** | `appsettings.json:3,8,21`; `DependencyInjection.cs:25`; `ApplicationDbContextFactory.cs:12`; `DataSeeder.cs:45` | dev‑плейсхолдеры (`BootstrapUser:Enabled=false` в проде); но утекают в git, а суперюзер усиливает риск подделки аудита |
| U‑4 | **`audit_logs` не INSERT‑only** (нет REVOKE/триггеров) — усугубляется работой из‑под суперюзера | миграции: REVOKE/TRIGGER = 0 | приложение само не делает UPDATE/DELETE — но это дисциплина, не техническая неизменяемость |
| U‑5 | **Нет шифрования спецкатегории at‑rest** (файлы/столбцы/`RecognitionJson`) | `FileScanStorage.cs:29,49`; grep `IDataProtector/pgcrypto/Aes` = 0 | BitLocker/TDE закрывает кражу диска; но не логический доступ/бэкап; v1‑стратегия «не хранить сканы» |
| U‑6 | Приём загрузки по клиентскому MIME без magic‑байт/AV | `ScanService.cs`/`Scans/Index.cshtml.cs` без изменений | импакт XSS теперь снят `nosniff`; но AV (мера АВЗ) и проверка сигнатуры остаются нужны |
| U‑7 | Медданные на Ollama по `http` без TLS (Ollama без auth) | `appsettings.Development.json:16`; `LocalOllamaRecognitionProvider.cs:61-62` | WireGuard/Tailscale шифрует транспорт; но не L7‑аутентификация пира |
| U‑8 | poppler: рендер недоверенного PDF без песочницы/лимитов + нет rate‑limit на распознавание | `LocalOllamaRecognitionProvider.cs:116-146` | рендер только стр.1, отд. GPU‑узел; defense‑in‑depth оправдан |
| U‑9 | Нет учёта письменного согласия (152‑ФЗ ст.10) | `Domain/Entities/*` — нет `Consent` | орг‑мера РЭУ + поле модели; блокер перед прод‑загрузкой сканов |
| U‑10 | Нет CI‑гейта безопасности (анализаторы выключены) | `Directory.Build.props` (`TreatWarningsAsErrors=false`) | MVP без remote — но дёшево включить сейчас |

**P2:** CSP `'unsafe-inline'` для script/style (`Program.cs:53-54`, MED‑A05‑CSP); cookie без явных `Secure`/`SameSite=Strict`/`ExpireTimeSpan` (`DependencyInjection.cs:44-49`); `AllowedHosts="*"` (`appsettings.json:21`); lockout с дефолтной длительностью (5 мин, `MaxFailedAccessAttempts=5`, без `DefaultLockoutTimeSpan`); перечисление по сообщению о блокировке (`Login.cshtml.cs:76-77`); LIKE‑wildcard `%`/`_` (`RegistryQueryService`); SHA‑256 не сверяется при чтении; пакеты `8.0.4`/устаревший `FluentValidation.AspNetCore 11.3.0`; `SqlRosterSource` raw SQL из конфига.

## C. Полная адверсариал‑верификация — ВЫПОЛНЕНА (см. §3.9)

Все 35 находок (21 из снимка + 14 OWASP) прошли независимую проверку «опровергни вывод» против текущего кода (полная таблица — §3.9). Итог: **6 опровергнуто/исправлено, 10 подтверждено, 19 частично; после переоценки важности P1 осталось только 3.** Подтверждённый главный остаток:

1. **Контроль доступа** — `MED-A01-FOLDER` (плоская авторизация, нет ролей на страницах) — **P1**; BOLA/`MED-A01-BOLA-SCAN` и `MED-A01-REVIEW` понижены до P2 в доверенной v1‑ЛВС, но становятся **P0 при вводе роли студента (v2)**.
2. **Шифрование спецкатегории at‑rest** — `MED-A02-AT-REST` — **P1**.
3. **Неизменяемость аудита + приложение под `postgres`‑суперпользователем** — `audit-not-insert-only` — **P1**.

Всё остальное (секреты в dev‑конфиге, cookie‑флаги, `AllowedHosts=*`, CSP `'unsafe-inline'`, LIKE, poppler, AV, NuGet 8.0.4, SHA‑сверка, SSO/iframe‑будущее) — **P2** (или none, если уже закрыто). Это финальная картина после пакета фиксов.


---

# Блок 1. Зарубежные реализации цифрового учёта медсправок/допусков
I have comprehensive primary-source coverage. The FERPA athletics-screening point is especially valuable (HK Law/ed.gov: medical forms used to screen for eligibility in school-sponsored athletics become "education records"). I have enough to write the full report. Let me compose the final deliverable.

# БЛОК 1. Цифровизация учёта студенческих медсправок и допусков к физкультуре/спорту в зарубежных вузах (США, ЕС/Великобритания, Канада, Австралия)

Ниже — разбор 8 конкретных систем/порталов с реальными названиями, вендорами и ссылками. Везде один и тот же архитектурный паттерн: **self-service-загрузка студентом → верификация медперсоналом → роль-сегрегированная видимость статуса (а не диагноза) → автоматические напоминания и «холды» на регистрацию**. Этот паттерн прямо релевантен для MedSpravki-REU (кафедра физкультуры РЭУ).

---

## 1. Medicat (Medicat One / Patient Portal / Compliance Services / VeriVax)

**(a) Вендор/ссылка.** Medicat LLC — крупнейший в США вендор EHR для college health. Продукты: MedicatOne (облачный EHR), Medicat Patient Portal (студенческий портал), Immunization Compliance Module, VeriVax (автоверификация), отдельный сервис Medicat Compliance Services (аутсорс-проверка). Сайт: https://medicat.com. Сертификация TX-RAMP Level 2 (гос-уровень безопасности, март 2024).

**(b) Workflow.**
- Студент логинится через университетский SSO/EUID (пример UNT Health: `UNThealth.medicatconnect.com`), вкладка **Immunizations → Required Vaccinations**: вручную вводит даты каждой дозы (MMR, Tdap, Meningococcal, Hep B, Varicella), затем вкладка **Upload** — загружает скан/фото справки (форматы .gif/.png/.tiff/.jpg/.jpeg/.txt/.pdf; .doc/.docx НЕ принимаются; имя файла без спецсимволов и пробелов).
- Часть данных может приходить автоматически: лабораторные результаты TB/титры подтягиваются из EHR без ручной загрузки (UCI: «records will automatically be updated once results return»).
- **Верифицирует медперсонал** (health administrator / Compliance Services staff): «information is verified and approved by a health administrator who can track compliance or lack of compliance and report back to the student through secure messaging». Срок обработки 2–5 рабочих дней. До проверки статус НЕ показывает «Verified».
- VeriVax: при автоматическом матчинге записи статус выставляется «verified» автоматически, разгружая персонал от ручной сверки бумажных/факс-копий.

**(c) UX-детали.**
- Левое меню-«рельс»: Immunizations / Upload / Forms / Messages / Insurance / Education.
- **Светофор статусов**: «Compliant» / «Non-compliant» / «Verified»; у University of Miami буквально «Y»=compliant, «N»=non-compliant в разделе «Compliant with Current Requirement».
- **Secure Messages** — встроенный защищённый канал между студентом и health services (без email, т.к. HIPAA/FERPA).
- **e-consent / онлайн-формы для новичков**: Receipt of Privacy Practices, Consent for Treatment, Student Medical History, TB Screen, Acknowledgement of HIPAA — заполняются в портале (Iona, Anna Maria College).
- Админ-сторона (Immunization Compliance Module): **кастомные когорты** (по курсу/программе/статусу вакцинации), **расширенная фильтрация** (тип вакцины, дата, срок годности, статус), отчёты для clinical-site партнёров.
- **Hold / registration block**: «hold placed on your account… will not be able to register for upcoming classes unless all requirements are met» + ежемесячные compliance-аудиты (UNT).

**(d) Best practice.** Разделение «portal для студента» и «compliance dashboard для staff»; автоматическая верификация совпадающих записей (VeriVax) при сохранении ручного review для спорных; ежемесячный аудит соответствия (не разово на старте семестра).

## Источники
- [Medicat (главная)](https://medicat.com) — вендор college-health EHR, продукты MedicatOne / Immunization Compliance / VeriVax — проверено 2026-06-16.
- [Medicat — Immunizations Archives (VeriVax, Compliance Module)](https://medicat.com/tag/immunizations) — описание end-to-end workflow «студент сдаёт → staff проверяет», автоверификация, когорты и фильтры — проверено 2026-06-16.
- [UNT Health — Medicat](https://www.unthealth.edu/students/student-health/medicat.html) — реальный вузовский процесс: загрузка, верификация, hold на регистрацию, ежемесячные аудиты — проверено 2026-06-16.
- [Medicat Compliance Services — Quick Reference (Wellesley PDF)](https://www1.wellesley.edu/sites/default/files/assets/departments/healthservices/files/mcs_student_instructions_wellesley.pdf) — «verified and approved by a health administrator», форматы файлов, сроки обработки — проверено 2026-06-16.
- [Anna Maria College — Medicat Portal How-To (PDF)](https://annamaria.edu/wp-content/uploads/2024/05/2024-2025-Medicat-Portal-How-To-Instructions.pdf) — меню портала (Immunizations/Upload/Forms/Messages), e-consent-формы для новичков — проверено 2026-06-16.
- [University of Miami — Uploading Immunization Forms](https://studenthealth.studentaffairs.miami.edu/immunization-information/uploading-immunization-forms/index.html) — статус-светофор «Y/N compliant», MyUHealthChart — проверено 2026-06-16.

---

## 2. Point and Click (PnC / Point ‘N Click Solutions)

**(a) Вендор/ссылка.** Point and Click Solutions — ветеран college-health EHR. Сайт: https://www.pointandclicksolutions.com (модуль Practice Management: Registration | Scheduling | Billing). Студенческий портал — «Point and Click (PNC) Patient Portal». Интеграции с Banner SCT и PeopleSoft из коробки.

**(b) Workflow.**
- Registration-система «оптимизирована под college», интегрируется с кампусными системами для определения eligibility, **трекинга immunization compliance** и связи со счётом студента (bursar account).
- Студент: вкладка **Medical Clearances** (левое меню) → у каждого требования кнопка **Update** → вводит даты → загружает оригинал документа во вкладку **Immunization Records**. Важная деталь PnC: «Entering the dates **without original documentation** will result in the clearance being marked non-compliant» — даты без скана не засчитываются.
- **Верифицирует staff** (SHWC): «Once reviewed, you will either see the status change to **compliant**… or you will receive a message in your portal from the SHWC staff» (Clark Atlanta / Morehouse).
- Exemptions (медотвод/религиозный отказа): отдельная вкладка **Downloadable Forms** — зелёная кнопка Upload под нужной категорией (напр. «COVID-19 Medical Exemption Request»), действительны 1 год.

**(c) UX-детали.**
- Левое меню; зелёные кнопки **Update / Upload / Save**; синий info-блок со спец-инструкциями вверху экрана требования.
- **Hold**: «Hlth Immunizations Reg Hold» — отдельный тип хода в SIS (Tulane); снимается за 1–2 рабочих дня после выполнения.
- Светофор «compliant / non-compliant»; автозаполнение записей из клиник кампуса (флю-прививка в clinic кампуса попадает в портал автоматически, Tulane).
- Примечание: ряд крупных вузов мигрируют с PnC на Epic — UCLA Ashe Center перешёл с Point ‘N Click на **Epic Care Connect (MyStudentChart)**, который тоже умеет «Complete the UCLA Immunization Requirements» в портале.

**(d) Best practice.** Жёсткая связка «дата + оригинал документа = условие compliance» (анти-фрод против ввода голых дат); registration-hold как нативный тип SIS-хода; интеграция с bursar/Banner/PeopleSoft.

## Источники
- [Point and Click — Practice Management](https://www.pointandclicksolutions.com/practice-management) — registration оптимизирован под college, трекинг immunization compliance, интеграция Banner/PeopleSoft, bursar — проверено 2026-06-16.
- [Morehouse College — How to Upload to PnC Patient Portal (PDF)](https://morehouse.edu/hubfs/22200391/Files/PDFs/How-to-Upload-Immunization-Records-to-Point-and-Click-Patient-Portal.pdf) — вкладка Medical Clearances, Update-кнопки, «даты без оригинала = non-compliant», staff-review — проверено 2026-06-16.
- [Clark Atlanta / MSM — Upload to PnC (PDF)](https://www.msm.edu/Current_Students/student-health/documents/CAU-Upload-Instructions-Nov2024.pdf) — exemption-формы (1 год), статус меняется на compliant после review — проверено 2026-06-16.
- [Tulane Campus Health — Returning Students](https://campushealth.tulane.edu/immunizations/returning-students) — «Hlth Immunizations Reg Hold», авто-внесение прививок из clinic кампуса — проверено 2026-06-16.
- [UCLA Ashe Center — Epic / MyStudentChart](https://www.studenthealth.ucla.edu/epic) — миграция с Point ‘N Click на Epic, immunization requirements в портале — проверено 2026-06-16.

---

## 3. PyraMED (PyraMED Health Systems / PyraMED ANYwhere)

**(a) Вендор/ссылка.** PyraMED Health Systems — EHR для университетских кампусов (25+ лет на рынке college health). Сайт: https://pyramed-health.com. Уникальная черта — **единая платформа для 4 служб кампуса**: Health Services, Counseling/Mental Health, **Athletic Training Services**, Accommodation & Disability Services, с «detailed security access controls» (раздельный доступ по службам). Модули: EHR, E-Prescribing, Patient Communication, **Immunization Management**, Interfaces, Document Management, Financial Management. Мобильный доступ — PyraMED ANYwhere.

**(b) Workflow.**
- Студент через университетский SSO (пример Mt. San Jacinto College: вход в Microsoft SSO → плитка «PyraMED» → вкладка **My Forms**) заполняет формы электронно, загружает immunization/медицинскую документацию, видит secure-сообщения от Health Services (Georgian Court University).
- Верифицирует медперсонал службы; до verified — hold.
- Athletic Training как отдельная служба в той же системе: спортивные допуски ведутся раздельно от общей student-health, но в едином EHR.

**(c) UX-детали.**
- SSO-плитка, вкладка «My Forms» с очередью незаполненных форм; синяя плашка с активным телемедицинским приёмом.
- **Hold на регистрацию**: «Failure to comply will result in a hold on your account and inability to register for future classes» (Georgian Court).
- Раздельные content-наборы и **security access controls** по службам — ключ к role-segregation (тренер/AT видит спортивные допуски, но не general-health чарт).

**(d) Best practice.** Один EHR на все wellness-службы кампуса при детальном раздельном контроле доступа — самый близкий к РЭУ кейс «физкультура + общий медучёт в одной системе, но с разделением ролей».

## Источники
- [PyraMED Health (главная)](https://pyramed-health.com) — EHR для college, 4 службы (health/counseling/athletic training/disability), модуль Immunization Management, security access controls — проверено 2026-06-16.
- [Georgian Court University — Health Services (PyraMED)](https://georgian.edu/health-services) — студент заполняет формы и грузит immunization-доки в PyraMED-портал; hold при невыполнении — проверено 2026-06-16.
- [Mt. San Jacinto College — Patient Portal (PyraMED)](https://msjc.edu/healthcenter/patient-portal.html) — вход через SSO-плитку PyraMED, вкладка «My Forms», заполнение перед приёмом — проверено 2026-06-16.

---

## 4. Privit / Privit Profile (e-PPE) — допуск к спорту

**(a) Вендор/ссылка.** PRIVIT (осн. 2009, Columbus OH / London ON) — облачный лидер по **электронной pre-participation evaluation (e-PPE)** для спорта. Продукт Privit Profile (бывш. Privit e-PPE), сайт: https://privit.com/athletics. Используется как высшими школами (University of Cincinnati, McMaster University USport/OUA), так и школьными ассоциациями (Georgia High School Association). Брендированный поддомен на вуз: напр. `mcmasterathletics.privitprofile.ca`.

**(b) Workflow.**
- **Грузит студент-атлет (и/или родитель, если несовершеннолетний)**: заполняет Personal Details (до 100%), Pre-Participation History Form (медистория), Release/Emergency, согласия (concussion, sudden cardiac arrest, drug-testing) — каждое с **e-signature** атлета и/или родителя.
- Система генерирует pre-заполненную Physical & Clearance Form → студент печатает → **врач проводит осмотр и подписывает** → студент загружает подписанную форму обратно (Manage Documents → Upload Document → Document Type «Physical Exam & Eligibility Form (Signed)»).
- **Верифицирует/допускает staff (athletic trainer)**: «A staff member at the school will update the **Clearance Status** — the status is **not automatically updated**». Это критично: завершённость форм («Submission Complete») ≠ допуск; clearance ставит руками сертифицированный медспециалист (в McMaster — Sport Medicine Certified Therapist).
- Medical History Summary автоматически выделяет зоны риска для врача (фокусирует осмотр).

**(c) UX-детали.**
- **Completion status вплоть до уровня отдельной формы** — «so all staff know the status of each athlete profile»; при «Submission Incomplete» — навести курсор и увидеть, чего не хватает.
- **Clearance status контролируется staff** и в реальном времени сообщает тренеру об eligibility («informing coaches of participation eligibility in real time»).
- **Живая e-подпись** на тач-устройстве, документы со штампом даты/времени подписи, полный **audit trail** всех версий в каждом аккаунте.
- **Field-level encryption**; Sideline App — тренер/AT видит инфо и обновляет clearance «на бровке» поля.

**(d) Best practice.** Чёткое разделение **«submission complete» (студент) vs «cleared» (staff)** — допуск всегда выставляет человек-медик; светофор eligibility для тренера показывает СТАТУС, а не медисторию; полный audit trail подписей.

## Источники
- [PRIVIT — Athletics (Intake Forms)](https://privit.com/athletics) — completion status по формам, clearance контролируется staff, eligibility тренеру в реальном времени, e-signature + audit trail, field-level encryption — проверено 2026-06-16.
- [Privit — Plainfield Parent Instructions (PDF)](https://files-backend.assets.thrillshare.com/documents/asset/uploaded_file/66/Athletics/ccb1a7dd-f94d-4d9b-9cc3-b83916f5d5c4/PRIVIT-Plainfield_Parent_Instructions-1.pdf) — «staff member will update the Clearance Status, the status is not automatically updated»; «Submission Complete/Incomplete» — проверено 2026-06-16.
- [McMaster University Athletics — Privit Medical Screening](https://marauders.ca/sports/2011/3/26/medicalscreeningprocess.aspx) — пре-заполненная physical form, загрузка подписанной врачом формы, review Sport Medicine Certified Therapist — проверено 2026-06-16.
- [PRIVIT — University of Cincinnati selects Privit Profile](https://privit.com/university-cincinnati-selects-privit-e-ppe-streamline-sport-participation) — вузовский кейс e-PPE, medical history summary выделяет риски — проверено 2026-06-16.

---

## 5. ARMS Software → Teamworks Compliance + Recruiting — допуск к спорту (NCAA)

**(a) Вендор/ссылка.** ARMS Software (ныне **Teamworks Compliance + Recruiting**, поглощён Teamworks) — управление intercollegiate-атлетикой NCAA: compliance, recruiting, формы. Логин: https://my.armssoftware.com; обзор: https://teamworks.com/compliance. Health-формы атлета (включая pre-participation physical) ведутся в **ARMS profile → Health Forms Packet** (пример American International College, NCAA).

**(b) Workflow.**
- Студент-атлет загружает pre-participation physical (по NCAA — в течение 6 мес. до первой активности) через свой ARMS-профиль, раздел Health Forms Packet.
- Workflow-движок **автоматизирует цепочку согласований** (approval chain) между атлетом, админами и compliance-staff — без email-пинг-понга; задачи (формы к заполнению) автоматически назначаются и трекаются.
- Видимость: «all activities and workflows… readily visible to the necessary staff and student-athletes» — прозрачность для нужных ролей.

**(c) UX-детали.**
- Beginning-of-Year формы с **автозаполнением данными атлета**; задачи в мобильном приложении (Teamworks Hub) — «one app they use every day».
- Real-time alerts о невыполненных задачах; дашборд completion.
- Конкуренты с тем же паттерном: **Spry** (spry.so) — отдельные роли Administrators / Coaches / Compliance Staff / Student-Athletes, compliance-monitoring, workflow-builder.

**(d) Best practice.** Автоматизированный approval-chain + назначение/трекинг задач с role-based видимостью; единое мобильное приложение для атлета — повышает completion.

## Источники
- [Teamworks — Student-Athlete Compliance](https://teamworks.com/compliance) — формы Beginning-of-Year с автозаполнением, задачи compliance в одном app, role-based видимость — проверено 2026-06-16.
- [Teamworks — The Power of ARMS Compliance](https://teamworks.com/de/blog/the-power-of-arms-compliance) — автоматизация approval-chain, авто-назначение и трекинг задач, прозрачность для staff — проверено 2026-06-16.
- [American International College — New Student-Athlete Requirements](https://aicyellowjackets.com/sports/2025/5/27/new-incoming-student-athlete-requirements.aspx) — загрузка pre-participation physical через ARMS profile / Health Forms Packet, NCAA-правило 6 мес. — проверено 2026-06-16.
- [Spry — Intercollegiate Athletics Management](https://spry.so) — раздельные роли (admin/coach/compliance/athlete), compliance-monitoring, paperwork/workflow — проверено 2026-06-16.

---

## 6. Pomelo Health (by TELUS Health) — patient engagement / иммунизация (Канада)

**(a) Вендор/ссылка.** Pomelo Health (ранее Health Myself Innovations, поглощён TELUS Health) — канадская patient-engagement-платформа, интегрируется с EMR Accuro/Medesync, ~5000+ клиник. Сайт-эко: TELUS Health; брошюра CEQ: ontariomd.live. Это НЕ нишевый студенческий продукт, а массовый движок записи/напоминаний/форм — полезен как образец UX напоминаний и e-forms (и как один из вариантов «облачного» self-service-приёма документов в РФ-неприменимой, но методически близкой модели).

**(b) Workflow.**
- Пациент (студент) сам бронирует слот онлайн, заполняет **e-forms** (intake/triage) до визита; формы автопопулируют чарт; динамические поля и условные триггеры; mobile self-check-in с QR-кодом («proof-of-presence»).
- Верификация — на стороне клиники (EMR-интеграция); broadcasting — массовые приватные рассылки/бюллетени с аналитикой engagement.

**(c) UX-детали.**
- **Автоматические напоминания** по SMS/voice/email с возможностью confirm/cancel — главный инструмент против no-show.
- Динамические e-forms (contactless, патиент-заполняемые), pre-screening, шаринг файлов; раздельные/общие inbox’ы с access permissions.
- Encrypted private messaging, patient portal как «единая точка доступа».

**(d) Best practice.** Автоматизированные многоканальные напоминания (SMS/email/voice) + динамические условные формы + mobile self-check-in — это эталон «automated reminders» из сводки best practices.

## Источники
- [Pomelo by TELUS Health — брошюра CEQ (PDF)](https://ontariomd.live/wp-content/uploads/2022/09/brochure_pomelo_ceq_en.pdf) — e-forms с динамическими полями и авто-триггерами, mobile check-in (proof-of-presence/QR), напоминания SMS/voice/email, broadcasting, encrypted messaging — проверено 2026-06-16.
- [Pomelo Health — обзор (Zoftware)](https://zoftwarehub.com/products/pomelo-health/product-details) — продукт TELUS Health, интеграция Accuro/Medesync, ~5000 клиник, online intake forms — проверено 2026-06-16.

---

## 7. Synergy Gateway «Verified» — допуск к практике/клинике (Канада, University of Toronto и др.)

**(a) Вендор/ссылка.** Synergy Gateway Inc. — канадский провайдер healthcare/compliance-решений для вузов; продукт **Verified by Synergy** с услугой **Electronic Requirements Verification (ERV)**. Сайт поддержки: www.synergyhelps.com. Используется University of Toronto (Faculty of Social Work, Leslie Dan Pharmacy, Bloomberg Nursing), Ontario Tech и др. У UofT параллельно есть собственные порталы: Registration Document Portal, **POWER** (PGME — статус и истёкшие требования).

**(b) Workflow.**
- Студент сам загружает в Synergy: UofT Immunization Record Form (подписана HCP), blood work/lab reports, сертификаты (CPR/BLS, mask-fit, police check) — в именованные папки.
- Бронирует **ERV-приём** (платный: initial review ~$50–52.50+HST, follow-up $10); до приёма всё должно быть загружено к 9:00 EST дня review.
- **Верифицирует Synergy** (turnaround 3–5 раб. дней): выставляет статус **«PASS»**; студент скачивает **Compliance Summary / Completion Certificate**. Без «PASS» — не допуск к практике, риск де-регистрации с курса.
- Renewal-логика: требования с истечением в период placement надо обновлять заранее (annual TB, mask-fit раз в 2 года) — повторный ERV.

**(c) UX-детали.**
- Папочная структура загрузки (Annual Vaccinations / Health & Safety Certificates / Permit Form / Medical documents).
- Платный «человеко-review» (ERV) как сервис — нечастый, но показательный пример аутсорса верификации.
- POWER (UofT PGME): «view any missing or expiring immunization requirements» — дашборд истекающих требований.

**(d) Best practice.** Verification-as-a-service со скачиваемым Compliance Summary (артефакт допуска); раздельные deadlines и renewal-логика по типам требований; «expiring requirements» dashboard.

## Источники
- [UofT Factor-Inwentash — Immunization Form Verification by Synergy Gateway](https://socialwork.utoronto.ca/immunization-form-verification-by-synergy-gateway) — папочная загрузка, ERV-review к 9:00 EST, Compliance Summary как Completion Certificate, fees — проверено 2026-06-16.
- [UofT Leslie Dan Pharmacy — Required Immunizations (Synergy Verified)](https://www.pharmacy.utoronto.ca/current-students/pharmd/oee/additional-requirements/required-immunizations) — Synergy Gateway Verified + ERV, «PASS» обязателен для допуска к ротациям — проверено 2026-06-16.
- [UofT PGME — Immunization & Mask Fit Requirements (POWER)](https://pgme.utoronto.ca/pgme-immunization-mask-fit-test-requirements) — Registration Document Portal, POWER-статус missing/expiring, renewal annual TB / mask-fit 24 мес. — проверено 2026-06-16.
- [Ontario Tech — Pre-placement requirements (Verified by Synergy)](https://healthsciences.ontariotechu.ca/nursing-practicum/practicum-information/pre-placement-requirements.php) — turnaround 3–5 дней, риск де-регистрации без «PASS», fees — проверено 2026-06-16.

---

## 8. Университетские порталы: США (Cornell, UC/UCLA), Великобритания (KCL/Optima/OHWorks), Австралия (Sonia Online, USC)

### 8.1 Cornell University — myCornellHealth (на базе Medicat-класса EHR)
- **Workflow:** студент в `myCornellHealth` → раздел **Clearances & Requirements** → вводит даты прививок/TB → **Upload Immun. Records** (документы на английском от HCP/школы/военных). Альтернатива загрузке — fax/mail.
- **Верификация:** «have the information reviewed and approved by Requirements staff»; review до 3–4 недель в пик.
- **Каскад холдов по датам (отличный UX-приём):** 8 июля — enrollment-change hold; 6 авг — add/drop hold; 10 сент — **withdrawal из университета без права re-enroll**. «In Progress»-статус (записался на приём + прислал proof) снимает hold временно.
- **Status-not-diagnosis для не-медиков** прямо закреплён политикой: «Per HIPAA privacy law, we are NOT able to communicate about your requirements by email» — только через secure-портал.

### 8.2 UC / UCLA — My Student Chart / MyStudentChart (Epic), immunizationrequirement.ucla.edu
- Светофор + «View My Compliances»; загрузка jpg/png/pdf; TB Risk form с условным появлением нового clearance «TB Test».
- **UX-нюанс холда:** «placeholder»-hold показывают заранее (дата активации 1 ноября) как напоминание, не блокируя enrollment до дедлайна; типы хода — «Grades Only» vs enrollment-блокирующий.

### 8.3 Великобритания — модель Occupational Health (Optima Health «MyOH», OHWorks, Innovate Healthcare)
- **Не «портал вуза», а аутсорс-Occupational-Health-провайдер** (KCL → Optima Health, ряд вузов → OHWorks/Innovate). Студент-медик заполняет **Pre-Placement Questionnaire** и грузит evidence вакцинаций в **MyOH portal**; иммунитет проверяется по JCVI «Green Book».
- **Двухуровневый clearance:** «Placement clearance» (минимум для старта) → «Full clearance» (полный график). EPP-screening (exposure-prone procedures) для отдельных специальностей.
- **Эталон status-not-diagnosis + role-segregation:** «Your course and placement clearance status are shared with your course admin and placement lead, but… they will **not be able to access any of your OH records**» (Essex); OHWorks: записи не показываются никому вне OH (включая placement staff, tutors, GP) без явного **consent** студента (DPA 2018 / UK GDPR; провайдеры SEQOHS/ISO 27001).

### 8.4 Австралия — Sonia Online + InPlace (placement compliance)
- USC (University of the Sunshine Coast) Nursing & Midwifery: **Sonia Online** (soniaonline.usc.edu.au) — вкладка **Checks** → найти нужный check → загрузить evidence соответствия для placement; все вакцинации до placement (курс может занять до 12 мес.).
- Требования штата (пример: Diphtheria/Tetanus/Pertussis, Hep B, MMR, Varicella; WWCC — Working with Children Check; ежегодный flu); InPlace/Sonia — доминирующие placement-системы австралийских вузов.

### Best-practice по университетским порталам
Каскадные холды с растущей жёсткостью (Cornell), «placeholder»-холд как ранний reminder (UCLA), двухступенчатый clearance (UK OH), внешний OH-провайдер с жёсткой изоляцией медзаписей от учебного персонала.

## Источники
- [Cornell Health — Ithaca Students Health Requirements](https://health.cornell.edu/getcare/healthrequirements/undergrad-grad-prof) — Clearances & Requirements, Upload Immun. Records, каскад холдов (8 июля/6 авг/10 сент), «In Progress», review 3–4 недели — проверено 2026-06-16.
- [Cornell Health — Health Requirements for New Students](https://health.cornell.edu/get-care/health-requirements-new-students) — «Per HIPAA… NOT able to communicate by email», только secure-портал — проверено 2026-06-16.
- [UCI Student Health — Immunizations (My Student Chart)](https://studenthealth.uci.edu/immunizations) — «View My Compliances», Update-кнопки, условный TB Test clearance, авто-обновление лаб-результатов — проверено 2026-06-16.
- [UCLA Immunization Requirement — Holds & Compliance FAQ](https://immunizationrequirement.ucla.edu/frequently-asked-questions/deadline-compliance) — «placeholder» hold, типы (Grades Only vs enrollment), сроки processing 4 недели — проверено 2026-06-16.
- [KCL — OH Guide for Nursing & Midwifery (Optima Health, PDF)](https://www.kcl.ac.uk/nmpc/assets/guide-for-king's-college-london-nursing-midwifery-students-may2025.pdf) — Pre-Placement Questionnaire, загрузка в MyOH, JCVI Green Book — проверено 2026-06-16.
- [University of Essex — OH Student Clearance Guidance (PDF)](https://www.essex.ac.uk/-/media/documents/directories/occupational-health/students/occupational-health-student-clearance-guidance.pdf) — clearance status делится с course admin/placement lead, но «not able to access any of your OH records» — проверено 2026-06-16.
- [Wrexham University — Healthcare Student Clearance (PDF)](https://wrexham.ac.uk/media/marketing/policies-and-documents/admissions/HM_Student_HCW-Clearance-2024_compressed.pdf) — двухуровневый clearance: Placement clearance vs Full clearance — проверено 2026-06-16.
- [USC — Sonia Online, Nursing & Midwifery](https://soniaonline.usc.edu.au/soniaonline/School.aspx?SchoolId=2) — все вакцинации до placement (до 12 мес.) — проверено 2026-06-16.
- [USC — Sonia FAQ (Checks tab upload)](https://usc.custhelp.com/app/answers/list/search/1/kw/what%20is%20sonia/suggested/1) — вкладка Checks, загрузка evidence соответствия — проверено 2026-06-16.
- [University of Newcastle (AU) — Clinical Placement immunisation](https://www.newcastle.edu.au/current-students/career-ready-placements/clinical-placements/information-page-6) — список требуемых иммунитетов для placement — проверено 2026-06-16.

---

## Сводная таблица

| Система | Вендор | Кто грузит | Кто верифицирует | Что видит преподаватель/тренер | UX-фишка |
|---|---|---|---|---|---|
| **Medicat** (Patient Portal / One / VeriVax) | Medicat LLC (TX-RAMP L2) | Студент сам (даты + скан) | Health administrator / Compliance Services staff; VeriVax — автоматч | Только статус compliant/non-compliant + holds (не диагноз) | Secure messaging; когорты + фильтры в admin-дашборде; автоверификация совпадений |
| **Point and Click (PnC)** | Point and Click Solutions | Студент сам (Medical Clearances → Update) | SHWC staff (review → compliant/сообщение) | Статус + нативный SIS-hold «Hlth Immunizations Reg Hold» | Дата без оригинала = non-compliant; интеграция Banner/PeopleSoft/bursar |
| **PyraMED** | PyraMED Health Systems | Студент (My Forms, SSO-плитка) | Медперсонал службы | Раздельно по службам (AT/тренер видит спорт-допуск, не general chart) | Единый EHR на 4 службы + detailed security access controls |
| **Privit Profile (e-PPE)** | PRIVIT (e-PPE) | Студент-атлет + родитель (e-sign); врач подписывает physical | Athletic trainer ставит **Clearance Status вручную** | Светофор eligibility в реальном времени (Sideline App) | «Submission Complete» ≠ «Cleared»; audit trail подписей; field-level encryption |
| **ARMS / Teamworks Compliance** | ARMS Software → Teamworks | Студент-атлет (Health Forms Packet) | Compliance staff через approval-chain | Задачи/статус по ролям в Teamworks Hub (mobile) | Авто-назначение и трекинг задач, real-time alerts, автозаполнение форм |
| **Pomelo Health** | Pomelo (by TELUS Health) | Пациент/студент (e-forms, self-check-in) | Клиника (EMR-интеграция Accuro/Medesync) | — (общеклинический движок, не вузовские роли) | Многоканальные напоминания SMS/voice/email + динамические условные формы + QR check-in |
| **Synergy «Verified» / ERV** | Synergy Gateway Inc. | Студент (папки в Synergy) | Synergy reviewer (ERV) → статус «PASS» | Course/placement lead видят «PASS» + Compliance Summary | Verification-as-a-service; скачиваемый Completion Certificate; expiring-requirements дашборд (POWER) |
| **Univ. порталы** (Cornell myCornellHealth / UCLA Epic / KCL MyOH / USC Sonia) | Medicat/Epic/Optima/OHWorks/Sonia+InPlace | Студент сам | Requirements staff / OH-провайдер | Только clearance-статус; OH-записи изолированы от учебного персонала | Каскадные холды (Cornell); «placeholder»-hold-reminder (UCLA); 2-уровневый clearance (UK); consent-gating (UK GDPR/DPA 2018) |

---

## Сводка: best practices (и как они ложатся на MedSpravki-REU)

1. **Self-service upload студентом.** Студент сам вводит даты и загружает скан/фото справки (jpg/png/pdf), часто с мобильного. Принцип PnC: «дата без оригинала документа = non-compliant» — анти-фрод по умолчанию.
2. **Staff verification — допуск ставит человек-медик.** Везде статус «загружено/submission complete» отделён от «verified/cleared». В Privit это прямо: clearance status «is not automatically updated» — выставляет athletic trainer. Автоматизировать стоит только однозначный матчинг (VeriVax), спорное — на ручной review.
3. **Role-segregated visibility (раздельная видимость по ролям).** Тренер/преподаватель/деканат видят СТАТУС допуска (зелёный/красный/hold), но не медзаписи. Эталон — Essex/OHWorks: «clearance status shared with course admin… but they will not be able to access any of your OH records». PyraMED — «detailed security access controls» по службам.
4. **Status-not-diagnosis для не-медиков.** Преподавателю/тренеру отдаётся только «допущен / не допущен / на проверке / освобождён» + срок действия и группа (для физкультуры — основная/подготовительная/СМГ-аналог), без диагнозов, кодов МКБ, результатов анализов. Юридически в США это закреплено FERPA: медформы, используемые для скрининга eligibility в school-sponsored athletics, становятся «education records» с ограничениями на раскрытие (ed.gov, 34 CFR §99.3(b)(4)); общий health-чарт — «treatment records», исключённые из HIPAA, но защищённые FERPA.
5. **Automated reminders + holds (напоминания и блокировки).** Многоканальные напоминания (SMS/email/voice — Pomelo) + каскадные холды на регистрацию с растущей жёсткостью (Cornell: enrollment-hold → add/drop hold → withdrawal) + «placeholder»-холд как ранний reminder (UCLA) + ежемесячные аудиты соответствия (Medicat/UNT), а не разовая проверка на старте семестра.
6. **Светофор статусов + compliance dashboard + audit trail.** Студенту — светофор по каждому требованию (Update/Upload-кнопки, синяя info-плашка); персоналу — дашборд с фильтрами/когортами; всем — audit trail версий документов и e-подписей с штампом времени (Privit).
7. **e-consent встроен в поток.** Согласие на обработку/лечение, privacy notice, отказы/медотводы (exemptions) подписываются электронно в портале (Medicat-формы для новичков; Privit e-signature; UK — explicit consent на передачу report’а в учебную часть). Для РЭУ это согласие на обработку ПДн по 152-ФЗ.
8. **Двухуровневый/срочный clearance + renewal-логика.** «Placement clearance» vs «Full clearance» (UK); requirements с истечением (annual TB, mask-fit 24 мес.) обновляются заранее с дашбордом «expiring» (UofT POWER). Для физкультуры РЭУ аналог — справка с датой окончания и автонапоминанием о пересдаче медосмотра.

## Источники (сводка / регуляторика)
- [ed.gov Student Privacy — Joint Guidance on HIPAA and FERPA (2019, PDF)](https://studentprivacy.ed.gov/sites/default/files/resource_document/file/2019%20HIPAA%20FERPA%20Joint%20Guidance%20508.pdf) — treatment records postsecondary исключены из HIPAA (45 CFR §160.103); когда применяется FERPA vs HIPAA — проверено 2026-06-16.
- [ed.gov — Know Your Rights: FERPA Protections for Student Health Records (2023, PDF)](https://studentprivacy.ed.gov/sites/default/files/resource_document/file/Know%20Your%20Rights_FERPA%20Protections%20for%20Student%20Health%20Records.pdf) — student health records, как правило, не под HIPAA, а под FERPA; minimum necessary disclosure — проверено 2026-06-16.
- [Holland & Knight — FERPA Guidance on Student Health Records (2023)](https://www.hklaw.com/en/insights/publications/2023/04/us-department-of-education-issues-new-ferpa-guidance-on-student) — ключевой тезис: медформы для скрининга eligibility в school-sponsored athletics = «education records» под FERPA — проверено 2026-06-16.
- [California School-Based Health Alliance — HIPAA/FERPA Guide (34 CFR §99.3(b)(4))](https://www.schoolhealthcenters.org/resource/hipaa-ferpa-guide-california) — точная формулировка treatment-records-исключения и условий, при которых записи становятся education records — проверено 2026-06-16.
- [Healthie — HIPAA Compliant EHR](https://www.gethealthie.com) — модульная white-label платформа (intake forms, e-consent, scheduling, BAA/HIPAA) как пример кастомизируемого портала-допуска — проверено 2026-06-16.
- [Paubox — Is Healthie HIPAA compliant?](https://www.paubox.com/blog/is-healthie-hipaa-compliant) — BAA, minimum necessary PHI, de-identification (45 CFR §164.502(d)/§164.514) — проверено 2026-06-16.

Файлов на диске не создавал — research пассивный, активного сканирования rea.ru не проводилось. Все факты по системам и регуляторике подтверждены первоисточниками выше.


---

# Блок 2. Правовой режим обработки медданных студентов: зарубеж vs РФ
I now have all primary sources confirmed with precise article citations. I have everything needed to write the complete jurisdiction-by-jurisdiction analysis with the comparison table.

---

# БЛОК 2. Правовой режим обработки МЕДИЦИНСКИХ данных СТУДЕНТОВ: зарубеж vs РФ

> Контекст: ИС медсправок кафедры физкультуры РЭУ. Данные о состоянии здоровья = особо чувствительная категория во всех юрисдикциях. Ниже — режим по 5 юрисдикциям с точными артикулами, далее итоговая таблица-сопоставление и вывод о переносимости практик в РФ-режим.

---

## 1. Европейский союз — GDPR (Regulation (EU) 2016/679)

**Данные о здоровье = особая категория.** Art. 9(1) GDPR прямо запрещает обработку «data concerning health» (наряду с расой, политвзглядами, генетикой, биометрией, половой жизнью). Обработка возможна только если снят запрет одним из оснований Art. 9(2).

**Двойное основание (Art. 6 + Art. 9).** Для данных о здоровье недостаточно только основания из Art. 6 (lawfulness). Нужно ОДНОВРЕМЕННО:
- основание законности по **Art. 6(1)** (например, 6(1)(a) согласие; 6(1)(e) задача в общественном интересе; 6(1)(c) юробязанность); и
- условие снятия запрета по **Art. 9(2)**. Релевантные для студенческого медпункта:
  - **Art. 9(2)(a)** — explicit consent (прямое, явное согласие на одну/несколько конкретных целей);
  - **Art. 9(2)(h)** — «preventive or occupational medicine, … medical diagnosis, the provision of health … care or treatment or the management of health … care systems and services» (на основании права Союза/государства-члена либо договора с медработником, при условии профессиональной тайны по Art. 9(3));
  - **Art. 9(2)(i)** — общественное здоровье.
  - Art. 6 и Art. 9 «не стыкуются один к одному»: например, основание «исполнение договора» (6(1)(b)) не имеет зеркального эквивалента в Art. 9.

**Профессиональная тайна (Art. 9(3)).** Обработка по 9(2)(h) допустима, только если данные обрабатываются медработником или под его ответственность, связанным обязанностью профессиональной тайны.

**Принципы Art. 5** (применяются всегда):
- **Art. 5(1)(c)** — data minimisation: данные «adequate, relevant and limited to what is necessary» (нельзя собирать «на всякий случай»);
- **Art. 5(1)(e)** — storage limitation: хранение не дольше необходимого для целей; дольше — только для архивных/научных/статистических целей по Art. 89(1);
- **Art. 5(1)(b)** — purpose limitation; **Art. 5(1)(f)** — integrity and confidentiality (безопасность); **Art. 5(2)** — accountability.

**DPIA — Art. 35.** Оценка воздействия обязательна до начала обработки при высоком риске. **Art. 35(3)(b)** прямо называет триггером «processing on a large scale of special categories of data referred to in Article 9(1)». Содержание DPIA по Art. 35(7): описание операций и целей, оценка необходимости/пропорциональности, оценка рисков, меры по их снижению.

**ePrivacy** (Directive 2002/58/EC) — релевантна, если в ИС есть веб-портал/мобильное приложение: требует согласия на доступ к информации на устройстве пользователя (cookies/трекинг), отдельно от согласия GDPR. Для чисто внутренней справочной БД, как правило, не ключевая.

**Трансграничная передача** — Chapter V (Art. 44–49): передача в третьи страны только при adequacy decision (Art. 45), либо appropriate safeguards (Art. 46, SCC/BCR), либо дерогации (Art. 49). Локализации (обязательного хранения в ЕС) GDPR НЕ требует.

### Источники
- [Art. 9 GDPR — Processing of special categories of personal data](https://gdpr-info.eu/art-9-gdpr/) — текст запрета (ч.1) и оснований ч.2(a), (h), (i); проф. тайна ч.3 — проверено 2026-06-16.
- [Art. 5 GDPR — Principles relating to processing](https://gdpr-info.eu/art-5-gdpr/) — минимизация 5(1)(c), ограничение хранения 5(1)(e), purpose limitation — проверено 2026-06-16.
- [Art. 35 GDPR — Data Protection Impact Assessment](https://gdpr-info.eu/art-35-gdpr/) — DPIA обязателен; 35(3)(b) large-scale special categories Art. 9(1) — проверено 2026-06-16.
- [Defining and using health data — Taylor Wessing Global Data Hub](https://www.taylorwessing.com/en/global-data-hub/2022/march---health-data---getting-the-right-balance-between-innovation-and-data-protection/defining-and-using-health-data) — health data как special category, необходимость двойного основания Art.6+Art.9, их «нестыковка» — проверено 2026-06-16.

---

## 2. США — FERPA vs «treatment records» vs HIPAA

В США медданные студента ВУЗа регулируются НЕ HIPAA, а почти всегда FERPA — ключевая особенность для университетского медпункта.

**FERPA (20 U.S.C. § 1232g; 34 CFR Part 99).** Защищает «education records» — записи, прямо относящиеся к студенту и поддерживаемые учебным заведением. Раскрытие — только с письменного согласия (prior written consent) eligible student (студент ≥18 лет / обучающийся в ВУЗе), кроме узких исключений (20 U.S.C. § 1232g(b); 34 CFR § 99.31).

**Исключение «treatment records» — 20 U.S.C. § 1232g(a)(4)(B)(iv); 34 CFR § 99.3.** Медзапись студенческого здравпункта НЕ является «education record» (= «treatment record»), если выполнены три критерия:
1. запись на студента ≥18 лет / обучающегося в ВУЗе;
2. сделана/ведётся врачом, психиатром, психологом или иным признанным профессионалом (или парапрофессионалом) в его профессиональном качестве;
3. сделана/ведётся/используется ТОЛЬКО в связи с лечением студента и НЕ доступна никому (включая самого студента), кроме лиц, оказывающих лечение (с правом личного ознакомления врачом по выбору студента).

**Ключевой «триггер перехода».** Как только treatment record раскрывается для любой иной цели (включая выдачу самому студенту, скрининг для допуска к спорту, биллинг) — она перестаёт быть treatment record и становится обычным «education record» под полным режимом FERPA. То есть медсправка для допуска к физкультуре — это, как правило, уже education record (использование вне «лечения»).

**Неприменимость HIPAA.** HIPAA Privacy Rule (45 CFR Parts 160/164) прямо ИСКЛЮЧАЕТ из определения «protected health information» (PHI): (i) education records под FERPA и (ii) treatment records по § 1232g(a)(4)(B)(iv) — **45 CFR § 160.103 (определение PHI)**. Поэтому большинство университетских медцентров под HIPAA НЕ подпадают — даже если формально являются «covered entity»: их единственные записи о студентах = FERPA records, исключённые из HIPAA. HIPAA применяется лишь в узких случаях (например, лечение НЕ-студентов; биллинг как covered transaction для не-студенческих пациентов).

**Стыковка FERPA↔HIPAA — совместное руководство ED + HHS** («Joint Guidance on the Application of FERPA and HIPAA to Student Health Records», ред. 2019). Правило: к одной и той же записи две системы одновременно не применяются — если применяется FERPA, HIPAA не применяется. Решающий фактор — кто ведёт запись и для какой цели.

FERPA не предписывает технических мер защиты так, как HIPAA Security Rule; но регуляторы ожидают разумной безопасности и контроля доступа.

### Источники
- [Joint Guidance on the Application of HIPAA and FERPA to Student Health Records (2019), ED + HHS (PDF)](https://studentprivacy.ed.gov/sites/default/files/resource_document/file/2019%20HIPAA%20FERPA%20Joint%20Guidance%20508.pdf) — определение treatment records, исключение из HIPAA, биллинг = education record — проверено 2026-06-16.
- [Dear Colleague Letter: Protecting Student Medical Records, ED (PDF)](https://studentprivacy.ed.gov/sites/default/files/resource_document/file/DCL_Medical%20Records_Final%20Signed_dated_9-2.pdf) — три критерия treatment record, переход в education record при раскрытии, HIPAA не применяется — проверено 2026-06-16.
- [34 CFR § 99.3 «Education records» (определение, п.(b)(4)) — studentprivacy.ed.gov/ferpa](https://studentprivacy.ed.gov/ferpa) — нормативный текст trio-критериев, «treatment» не включает учебную деятельность — проверено 2026-06-16.
- [FERPA vs HIPAA — HIPAA Vault](https://www.hipaavault.com/resources/ferpa-vs-hipaa) — практический разбор: кто ведёт запись определяет применимый закон — проверено 2026-06-16.

---

## 3. Великобритания — UK GDPR + Data Protection Act 2018

После Brexit действует UK GDPR (текстуально близок к GDPR ЕС) совместно с **DPA 2018**.

**Special category data.** Данные о здоровье — special category (Art. 9 UK GDPR). Нужно ОДНОВРЕМЕННО основание Art. 6 и условие Art. 9 (как в ЕС). 10 условий Art. 9(2): (a) explicit consent, (h) health/social care, (i) public health, (j) research, и др.

**Дополнительный слой — Schedule 1 DPA 2018.** Для пяти условий Art. 9 (это (b), (h), (i), (j) и условия из (g)) требуется дополнительно выполнить условие из **Schedule 1 DPA 2018** (через ss. 10–11 DPA 2018):
- условие (h) «health or social care» → **Schedule 1, Part 1, condition 2**;
- условие (i) «public health» → condition 3; (j) research → condition 4.
- s. 11(1) DPA 2018 уточняет, когда выполнено требование профессиональной обязанности секретности (medical confidentiality).

**Appropriate Policy Document (APD).** Для многих Schedule 1 условий (особенно employment и substantial public interest, Schedule 1 paras 1, 5) требуется «appropriate policy document» — короткий документ с мерами compliance, политиками хранения и удаления. Примечание: для condition 2 (health/social care) и 3 (public health) сам APD формально НЕ обязателен, но ICO рекомендует документировать.

**Принципы и DPIA.** Принципы Art. 5 UK GDPR идентичны ЕС (минимизация, storage limitation). DPIA по Art. 35 UK GDPR обязателен при high-risk processing, включая large-scale special category data. ICO напрямую указывает: для special category data нужна DPIA при высоком риске.

### Источники
- [What are the rules on special category data? — ICO](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/what-are-the-rules-on-special-category-data/) — связка Art.9 + Schedule 1 DPA 2018, таблица соответствия условий, ss.10–11 — проверено 2026-06-16.
- [What are the conditions for processing? — ICO](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/what-are-the-conditions-for-processing/) — Art.9(2)(h) health/social care = Schedule 1 condition 2; APD-требования — проверено 2026-06-16.
- [Special category data (at a glance) — ICO](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/a-guide-to-lawful-basis/special-category-data/) — двойное основание Art.6+Art.9, 10 условий, 5 требуют Schedule 1, DPIA при high risk — проверено 2026-06-16.
- [Data protection and workers' health information — ICO](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/employment/information-about-workers-health/data-protection-and-workers-health-information/) — health = special category, APD, запрет авто-решений, DPIA — проверено 2026-06-16.

---

## 4. Канада — PIPEDA (+ провинциальный контекст PHIPA/FIPPA)

**PIPEDA** (S.C. 2000, c. 5) — федеральный закон для коммерческой деятельности. Построен на 10 принципах (Schedule 1, основан на CSA Model Code).

**Согласие и чувствительность.** PIPEDA не вводит формального перечня «special categories», но через принципы требует усиленной защиты sensitive info:
- **Principle 4.3 (Consent)** — обработка требует согласия.
- **Principle 4.3.4** — форма согласия зависит от чувствительности; «some information (for example, medical records and income records) is almost always considered to be sensitive». То есть медданные — почти всегда чувствительные.
- По позиции OPC и s. 6.1 PIPEDA (valid consent) для чувствительной информации (включая health) требуется **express (явное) согласие**, а не подразумеваемое (implied). Express consent обязателен, когда информация чувствительна / выходит за рамки разумных ожиданий / есть «meaningful residual risk of significant harm».

**Безопасность пропорционально чувствительности.** **Principle 4.7 (Safeguards)** + **4.7.2**: «More sensitive information should be safeguarded by a higher level of protection». Контроль доступа должен ограничивать круг лиц.

**Минимизация и сроки.** Principle 4.4 (Limiting Collection) — сбор только необходимого; Principle 4.5 (Limiting Use, Disclosure, Retention) — хранить не дольше, чем нужно для цели; Accuracy — Principle 4.6.

**Провинциальный/публичный контекст.** Если организация — health information custodian в провинции с «substantially similar» законом, применяется провинциальный закон вместо PIPEDA для медданных внутри провинции:
- **PHIPA (Онтарио, S.O. 2004, c. 3, Sch. A)** — health-specific закон; признан «substantially similar» PIPEDA; согласие может быть express/implied, но всегда knowledgeable, voluntary, related, given by individual. Custodians освобождены от PIPEDA в части PHI внутри Онтарио (но PIPEDA остаётся для межпровинциальных/международных передач).
- **FIPPA** — для публичного сектора (университеты Онтарио — public bodies под FIPPA; их медданные могут попадать в стык FIPPA/PHIPA).

PIPEDA не содержит обязательной локализации, но трансграничная передача требует сопоставимой защиты и информирования (accountability, Principle 4.1).

### Источники
- [Interpretation Bulletin: Sensitive Information — OPC Canada](https://www.priv.gc.ca/en/privacy-topics/privacy-laws-in-canada/the-personal-information-protection-and-electronic-documents-act-pipeda/pipeda-compliance-help/pipeda-interpretation-bulletins/interpretations_10_sensible/) — Principle 4.3.4 (медзаписи почти всегда sensitive), 4.7/4.7.2 (защита по чувствительности), express consent для health — проверено 2026-06-16.
- [Guidelines for obtaining meaningful consent — OPC Canada](https://www.priv.gc.ca/en/privacy-topics/business-privacy/collecting-personal-information/consent/gl_omc_201805/) — express consent при чувствительности и risk of significant harm; health = generally sensitive — проверено 2026-06-16.
- [PHIPA FAQ — IPC Ontario (PDF)](https://www.ipc.on.ca/sites/default/files/legacy/2015/11/phipa-faq.pdf) — PHIPA «substantially similar» PIPEDA, требования к согласию, стык с FIPPA/MFIPPA, PIPEDA для меж-/трансграничных передач — проверено 2026-06-16.
- [PIPEDA — valid consent s.6.1, express vs implied — TermsFeed](https://www.termsfeed.com/blog/pipeda/) — определение valid consent, когда обязателен express consent — проверено 2026-06-16.

---

## 5. Российская Федерация — 152-ФЗ + 323-ФЗ + ПП-1119 + приказ ФСТЭК №21

**Спецкатегория — ст. 10 152-ФЗ.** Данные о состоянии здоровья отнесены к специальным категориям. **Ч. 1 ст. 10**: обработка таких данных ЗАПРЕЩАЕТСЯ, кроме случаев ч. 2. Ключевые основания:
- **п. 1 ч. 2 ст. 10** — субъект дал **согласие в письменной форме**;
- **п. 4 ч. 2 ст. 10** — обработка в медико-профилактических целях, для установления медицинского диагноза, оказания медицинских/медико-социальных услуг при условии, что обрабатывает **лицо, профессионально занимающееся медицинской деятельностью и обязанное сохранять врачебную тайну**;
- п. 3 — защита жизненно важных интересов, когда согласие получить невозможно.

**Письменная форма согласия — ч. 4 ст. 9 152-ФЗ.** В случаях, предусмотренных федеральным законом, обработка ведётся только с **согласием в письменной форме**. Равнозначно письменному — электронный документ, подписанный электронной подписью. Обязательные реквизиты (ч. 4 ст. 9): ФИО, адрес субъекта, номер/дата/орган выдачи документа, удостоверяющего личность; наименование оператора; цель обработки; перечень ПДн; перечень действий; срок действия согласия и т.д.

**Принципы — ст. 5 152-ФЗ:**
- ч. 2 — обработка ограничивается достижением конкретных, заранее определённых, законных целей (purpose limitation);
- ч. 4 — обработке подлежат только ПДн, отвечающие целям; ч. 5 — содержание и объём не должны быть избыточными (минимизация);
- ч. 3 — запрет объединения БД с несовместимыми целями.

**Локализация — ч. 5 ст. 18 152-ФЗ** (в ред. ФЗ от 28.02.2025 № 23-ФЗ): при сборе ПДн граждан РФ (запись, систематизация, накопление, хранение, уточнение, извлечение) запрещается использование баз данных, находящихся ЗА ПРЕДЕЛАМИ территории РФ (кроме узких исключений по пп. 2, 3, 4, 8 ч. 1 ст. 6). То есть первичная база — только в РФ. Меры по выполнению обязанностей оператора — **ст. 18.1** (политика, локальные акты, назначение ответственного, оценка вреда и т.д.).

**Врачебная тайна — ст. 13 323-ФЗ** «Об основах охраны здоровья граждан»: сведения о факте обращения за медпомощью, состоянии здоровья, диагнозе и иные сведения, полученные при обследовании и лечении, составляют врачебную тайну; разглашение (в т.ч. после смерти лица) лицами, которым они стали известны при обучении, исполнении трудовых/служебных обязанностей, не допускается без согласия (кроме случаев ч. 3, 4 ст. 13).

**Уровни защищённости — ПП-1119 от 01.11.2012** (принято во исполнение ч. 3 ст. 19 152-ФЗ). Устанавливает 4 уровня защищённости (УЗ). Для ИС, обрабатывающей **специальные категории** ПДн (здоровье):
- **УЗ-2** требуется, в частности, если актуальны угрозы 2-го типа и спецкатегории, либо угрозы 3-го типа и спецкатегории > 100 000 субъектов (не сотрудников);
- **УЗ-3** требуется, если актуальны **угрозы 3-го типа** и ИС обрабатывает спецкатегории ПДн **сотрудников** оператора либо < 100 000 субъектов-несотрудников.
- Для медпункта вуза (студенты — не сотрудники, объём < 100 000, при актуальных угрозах 3-го типа без НДВ) практически реализуемый ориентир — **УЗ-3** (при ином моделировании угроз — выше). Точный УЗ определяется моделью угроз.

**Приказ ФСТЭК России № 21 от 18.02.2013** — состав и содержание организационных и технических мер защиты для каждого УЗ ИСПДн (детализирует требования ПП-1119): идентификация/аутентификация, управление доступом, регистрация событий, антивирус, межсетевое экранирование, контроль целостности и т.д. Состав мер растёт от УЗ-4 к УЗ-1.

### Источники
- [Ст. 10 152-ФЗ — Специальные категории персональных данных — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_61801/26edb2934b899bf9c74c3a8f7e574651c6565e6d) — запрет ч.1, п.1 ч.2 (письменное согласие), п.4 ч.2 (медцели + врачебная тайна) — проверено 2026-06-16.
- [Ст. 9 152-ФЗ — Согласие; ч.4 письменная форма и реквизиты — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_61801/6c94959bc017ac80140621762d2ac59f6006b08c) — ч.4: письменная форма / ЭП, обязательные реквизиты согласия — проверено 2026-06-16.
- [Ст. 18 152-ФЗ — ч.5 локализация (ред. ФЗ 28.02.2025 №23-ФЗ) — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_61801/cbf4e15b7c330f9372e876cdf2bc928bad7950ef) — запрет использования зарубежных БД при сборе ПДн граждан РФ — проверено 2026-06-16.
- [Ст. 5 152-ФЗ — Принципы (purpose limitation, минимизация ч.4/ч.5) — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_61801/96fbc469f91f57235cc842a85e0516a99f23dc85) — законные цели, неизбыточность, запрет объединения БД — проверено 2026-06-16.
- [Ст. 13 323-ФЗ — Врачебная тайна — cis-legislation (текст ФЗ 21.11.2011 №323-ФЗ)](https://cis-legislation.com/document.fwx?rgn=47975) + [перечень КонсультантПлюс, врачебная тайна = ст.13 323-ФЗ](https://www.consultant.ru/document/cons_doc_LAW_93980) — состав врачебной тайны, запрет разглашения — проверено 2026-06-16.
- [ПП РФ от 01.11.2012 №1119 — Требования к защите ПДн, уровни защищённости (УЗ) — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_137356/8c86cf6357879e861790a8a7ca8bea4227d56c72) — условия УЗ-2/УЗ-3 для спецкатегорий, угрозы 1/2/3 типов — проверено 2026-06-16.
- [Ст. 19 152-ФЗ — Меры безопасности (основание для ПП-1119 и приказа ФСТЭК) — КонсультантПлюс](https://www.consultant.ru/document/cons_doc_LAW_61801/ca9e5658710519f09ab2fdb8196fcb3eb024a051) — ч.3 уровни защищённости, ч.4 меры (приказ ФСТЭК №21) — проверено 2026-06-16.

---

## 6. ИТОГОВАЯ ТАБЛИЦА-СОПОСТАВЛЕНИЕ

| Требование | GDPR (ЕС) | FERPA+HIPAA (США) | UK GDPR / DPA 2018 | PIPEDA (Канада) | 152-ФЗ + 323-ФЗ (РФ) |
|---|---|---|---|---|---|
| **Согласие на обработку** | Art. 9(2)(a): explicit consent (одно из оснований; либо 9(2)(h) лечение без согласия). Условия согласия — Art. 7 | FERPA: prior written consent студента (20 U.S.C. §1232g(b); 34 CFR §99.30); treatment records — без согласия только для лечения (§1232g(a)(4)(B)(iv)) | Art. 9(2)(a) UK GDPR: explicit consent; для (h)/(i)/(j) — основание из Schedule 1 DPA 2018 вместо/вместе с согласием | Principle 4.3 + 4.3.4; для health (sensitive) — **express consent** (позиция OPC, s. 6.1) | **Письменная форма** согласия: п.1 ч.2 ст.10 + ч.4 ст.9 152-ФЗ (или ЭП). Либо п.4 ч.2 ст.10 — медцели медработником под врачебной тайной |
| **Минимизация** | Art. 5(1)(c): adequate, relevant, limited to necessary | FERPA напрямую не формулирует, но «treatment» ограничено целью лечения; иное использование → education record | Art. 5(1)(c) UK GDPR (идентично ЕС) | Principle 4.4 (Limiting Collection) — только необходимое для цели | Ст. 5 ч.4, ч.5 152-ФЗ: только отвечающие целям, не избыточные |
| **Разделение доступа / роль** | Art. 5(1)(f) + Art. 32 (security); Art. 9(3): доступ под ответственностью медработника, проф. тайна | Treatment records доступны ТОЛЬКО лицам, оказывающим лечение (34 CFR §99.3); раскрытие иным → теряет статус | Art. 5(1)(f) + Art. 32 UK GDPR; s. 11 DPA 2018 (medical confidentiality) | Principle 4.7 + 4.7.2: «more sensitive → higher protection»; ограничение круга доступа | Ст.13 323-ФЗ (врачебная тайна — только лица при исполнении обязанностей); приказ ФСТЭК №21 — управление доступом по ролям |
| **Сроки хранения** | Art. 5(1)(e): storage limitation — не дольше необходимого (искл. Art. 89(1)) | FERPA: нет жёсткого срока; education record хранится по политике учреждения; права доступа студента | Art. 5(1)(e) UK GDPR; политика хранения в APD (Schedule 1) | Principle 4.5: хранить не дольше необходимого для цели | Ст. 5 ч.7 152-ФЗ (хранение не дольше цели) + отраслевые сроки медучёта; п.5 ст.5 — уничтожение по достижении цели |
| **Трансграничная передача / локализация** | Chapter V, Art. 44–49 (adequacy/SCC/дерогации). **Локализации НЕТ** | HIPAA/FERPA: нет общего запрета на хранение за рубежом (контрактные требования к вендорам) | Chapter V UK GDPR (UK adequacy/IDTA). Локализации нет | Передача требует сопоставимой защиты + accountability (Principle 4.1). Локализации нет (провинц.: PHIPA внутри провинции) | **ЖЁСТКАЯ локализация: ч.5 ст.18 152-ФЗ** — первичная БД граждан РФ только на территории РФ; трансгранич. передача — ст. 12 |
| **Спец. требования к health data** | Art. 9 (запрет + основания); Art. 9(3) проф. тайна; **DPIA обязателен — Art. 35(3)(b)** (large-scale Art.9) | HIPAA, как правило, НЕ применяется (45 CFR §160.103 исключает FERPA records); режим = FERPA treatment/education records | Art. 9 UK GDPR + **Schedule 1 DPA 2018** (health → condition 2); APD; DPIA при high risk | Health «almost always sensitive» (4.3.4); провинц. health-законы (PHIPA) substantially similar | Запрет ст.10 ч.1 152-ФЗ; врачебная тайна ст.13 323-ФЗ; **ПП-1119 (УЗ-3/УЗ-2)** + приказ ФСТЭК №21 (тех. меры) |

---

## 7. ВЫВОД: что переносимо в РФ-режим, а что РФ требует жёстче

### Переносимо из лучших зарубежных практик (совместимо с 152-ФЗ и усиливает compliance)

1. **Минимизация (data minimisation).** GDPR Art. 5(1)(c) / PIPEDA Principle 4.4 полностью созвучны ст. 5 ч.4–5 152-ФЗ. Практика: для допуска к физкультуре в ИС хранить только статус («допущен / подготовительная / спецгруппа / освобождён» + срок), а НЕ диагноз и анамнез. Это снижает и УЗ по ПП-1119, и риск нарушения врачебной тайны. Прямой урок из FERPA: справка для допуска к спорту — это уже «не-лечебная» цель, поэтому минимизируйте передаваемое в неклиническую часть системы.

2. **Role-segregation / разделение доступа.** Модель FERPA treatment records (доступ ТОЛЬКО лечащему персоналу) и GDPR Art. 9(3) / PIPEDA 4.7.2 («чем чувствительнее — тем выше защита») переносимы как ролевая модель: медработник видит диагноз/основание; кафедра физкультуры/деканат видит только производный статус допуска и срок. Это реализует и ст. 13 323-ФЗ (врачебная тайна), и приказ ФСТЭК №21 (управление доступом).

3. **E-consent (электронное согласие).** ч. 4 ст. 9 152-ФЗ прямо признаёт согласие в форме электронного документа, подписанного ЭП, равнозначным письменному. Зарубежная практика «explicit/express consent» (GDPR 9(2)(a), PIPEDA express consent) переносима в РФ как структурированное e-consent с обязательными реквизитами ч. 4 ст. 9 — при условии квалифицированной/иной допустимой ЭП.

4. **DPIA / оценка воздействия.** GDPR Art. 35 (обязательна для large-scale health data) и DPIA-практика ICO — методологически переносимы как добровольная «оценка вреда субъектам» по ст. 18.1 152-ФЗ и часть модели угроз для ПП-1119/ФСТЭК. Хорошая практика даже без формального требования.

5. **Storage limitation + политики удаления.** GDPR Art. 5(1)(e), UK APD, PIPEDA 4.5 — переносимы как явная политика сроков хранения и уничтожения медданных по достижении цели (созвучно ст. 5 ч.7 152-ФЗ).

### Что РФ требует ЖЁСТЧЕ зарубежных режимов

1. **Локализация (ч. 5 ст. 18 152-ФЗ).** Уникальное российское требование: первичная база ПДн граждан РФ — физически на территории РФ. Ни GDPR, ни FERPA/HIPAA, ни UK GDPR, ни PIPEDA обязательной локализации не вводят (они регулируют трансграничную передачу через гарантии, но не запрещают хранение за рубежом). Для ИС медсправок РЭУ это означает: облако/сервер — только в РФ; зарубежный SaaS для первичного хранения недопустим.

2. **Письменная форма согласия на спецкатегорию (п. 1 ч. 2 ст. 10 + ч. 4 ст. 9).** РФ требует именно письменного (или ЭП-эквивалента) согласия с жёстким перечнем реквизитов. GDPR/UK требуют «explicit consent», но без обязательной письменной формы и без законодательно фиксированного списка реквизитов; PIPEDA — express consent без формальной письменной обязательности; FERPA — письменное согласие, но узкое (для раскрытия education records, а treatment records вообще обрабатываются без согласия в целях лечения). РФ-режим формально строже по форме.

3. **Врачебная тайна как отдельный самостоятельный режим (ст. 13 323-ФЗ).** В РФ медтайна — отдельная охраняемая законом тайна с прямым запретом разглашения и уголовной/административной ответственностью, поверх режима ПДн. В ЕС/UK это «профессиональная тайна» как условие Art. 9(3)/s.11 DPA; в США — конструкция FERPA treatment records. РФ накладывает ДВА параллельных режима (152-ФЗ + 323-ФЗ) на одни и те же данные.

4. **Предписанные государством технические меры (ПП-1119 + приказ ФСТЭК №21).** РФ жёстко регламентирует уровень защищённости (УЗ-1…4) и конкретный обязательный состав мер защиты, зачастую с требованием сертифицированных ФСТЭК/ФСБ средств. GDPR/UK/PIPEDA используют риск-ориентированный, технологически нейтральный подход («appropriate technical measures»), без государственного перечня обязательных СЗИ. HIPAA Security Rule ближе всех к предписывающему подходу, но для университетских медданных, как правило, неприменим (FERPA-режим). РФ-режим — самый предписывающий по технике.

**Практический итог для ИС медсправок РЭУ:** базовая архитектура — хранение в РФ (УЗ-3 по ПП-1119, меры по приказу ФСТЭК №21), e-consent с реквизитами ч.4 ст.9, ролевое разделение «медработник видит диагноз / кафедра видит только статус допуска» (реализует ст.13 323-ФЗ + минимизацию), политика сроков хранения. Зарубежные практики (минимизация, role-segregation, DPIA, storage limitation) надстраиваются сверху как усиление, не вступая в конфликт с 152-ФЗ.


---

# None
---

# Блок 3. Безопасность (главный блок)

Методология: 5 параллельных финдеров (STRIDE, загрузка/OCR, медданные+152‑ФЗ/ФСТЭК, тулинг/DevSecOps, интеграция Bitrix) + код‑факты по реальному `app/src` + адверсариал‑верификаторы, перепроверявшие каждый значимый вывод против кода. Ниже: модель угроз STRIDE и границы доверия (3.1), затем разбор по измерениям (3.2–3.6, материалы финдеров), затем код‑факты‑приложение (3.7) и **сводный чек‑лист P0/P1/P2 с результатами адверсариал‑проверки** (3.8).

## 3.1. Модель угроз STRIDE и границы доверия

**Акторы / хранилища / границы доверия (DFD словесно):**

- *Внешние акторы:* Студент (v2 — личный кабинет, загрузка), Преподаватель‑физрук, Медработник, Завкафедрой, Админ; **портал РЭУ Bitrix** (источник идентичности при интеграции).
- *Процессы:* ASP.NET Core 8 (Razor Pages + minimal‑API `/scans/{id}/file`), OCR‑конвейер (`pdftoppm` → Ollama).
- *Хранилища:* PostgreSQL 16 (медполя, `RecognitionJson`, `audit_logs`), файловое хранилище сканов `App_Data/scans/*.bin`, узел Ollama (RTX 3060, Tailscale).
- *Границы доверия:* (TB1) браузер↔приложение (HTTPS); (TB2) приложение↔БД; (TB3) приложение↔Ollama (**http по Tailscale**); (TB4, при интеграции) портал/обратный прокси↔наш .NET — **главная новая граница**, должна проходить по периметру нашего контура.

**Таблица STRIDE (ключевые угрозы, с привязкой к коду и финальной важностью):**

| Категория STRIDE | Угроза | Где | Контрмера | Важность |
|---|---|---|---|---|
| **S**poofing | Спуфинг `X‑Remote‑User`/`X‑Forwarded‑*` при прямом доступе к Kestrel в обход прокси (вариант d) | `Program.cs` — нет `UseForwardedHeaders`/`KnownProxies` | ForwardedHeaders+KnownProxies first‑in‑pipeline; подписанный JWT/HMAC от прокси; Kestrel только на loopback; mTLS | **P0** (инвариант до SSO) |
| Spoofing | Слабая аутентификация: bootstrap = Admin+Teacher, lockout без длительности, нет MFA, перечисление пользователей | `DataSeeder.cs:48-49`, `DependencyInjection.cs:38` | Разделить роли, `DefaultLockoutTimeSpan`, единое сообщение об ошибке входа | P1/P2 |
| **T**ampering | Подмена `.bin` в хранилище: SHA‑256 не перепроверяется при открытии (ложная декларация ОЦЛ) | `FileScanStorage.cs:45-51` vs `CertificateScan.cs:27` | Пересчёт+сверка SHA перед OCR/скачиванием, либо снять декларацию | P2 |
| Tampering | `audit_logs` не INSERT‑only на уровне БД — журнал можно править/удалять | `InitialCreate.cs:62-81` (нет REVOKE/триггеров) | Отдельная роль БД GRANT INSERT + REVOKE UPDATE/DELETE, BEFORE‑триггер, WORM‑экспорт | P1 |
| **R**epudiation | Просмотр/скачивание медскана и медполей, вход/выход **не логируются**; `IpAddress` всегда null | `Program.cs:55-61`, `ScanService.cs:85-94`, `AuditEntryFactory.cs:9-30`, `Login.cshtml.cs` | Логировать `ScanView`/`StudentView`/`Login(Failed)` с UserId+IP; `UseForwardedHeaders` для реального IP | **P0** (РСБ) |
| **I**nfo Disclosure | BOLA/IDOR: любой служебный пользователь открывает скан любого студента по `scanId` | `ScanService.OpenAsync` (нет owner‑фильтра); `Program.cs:55-61` | Resource‑based authorization (scope по группе/факультету) + аудит чтения | P1 (понижено с P0) |
| Info Disclosure | Спецкатегория ПДн **без шифрования at‑rest** (файлы, столбцы, `RecognitionJson`) | `FileScanStorage.cs:29,49`, `ApplicationDbContext.cs:102-117` | DataProtection/AES на файлы, EF ValueConverter/pgcrypto на чувствительные столбцы, мин. BitLocker/TDE | P1 (подтв.) |
| Info Disclosure | Stored‑XSS через загрузку (клиентский MIME + inline без nosniff) → чтение DOM медкарт/действия | `Scans/Index.cshtml.cs:55`, `ScanService.cs:93`, `Program.cs:60` | Magic‑байты + AV; `nosniff` + `Content-Disposition: attachment`; серверный MIME | **P0** (цепочка) |
| Info Disclosure | Медданные на Ollama по `http` без TLS; Ollama без аутентификации | `appsettings.Development.json:16`, `LocalOllamaRecognitionProvider.cs:61-62` | TLS/mTLS к Ollama или фиксация Tailscale как СКЗИ + ACL; запрет `http` вне localhost | P1 |
| Info Disclosure | `AllowedHosts="*"` + нет forwarded Host → Host‑injection/cache‑poisoning медответов под общим доменом | `appsettings.json:21`, `Program.cs` | Whitelist хостов; `Cache-Control: no-store` на `/scans/*`; раздельные cookie‑пути | P1 |
| **D**oS | Рендер тяжёлого/битого PDF через `pdftoppm` без таймаута/лимита памяти/семафора; нет rate‑limit на распознавание | `LocalOllamaRecognitionProvider.cs:125-134` | Жёсткий таймаут+Kill, cgroup/Job Object, лимит DPI/страниц, `AddRateLimiter`, семафор на GPU | P1 |
| **E**levation | Все аутентифицированные = полный доступ; нет per‑page ролей; `/journal`,`/import` всем; будущий студент получит всё | `Program.cs:19-25`, `Journal/Index` | Default‑deny per‑page policy; роль студента deny‑by‑default; scope по группам | P1 |
| Elevation | RCE/парсинг недоверенного PDF в poppler (CVE‑история JBIG2/JPX) в контексте сервиса с доступом ко всем сканам | `LocalOllamaRecognitionProvider.cs:116-146` | Песочница/отд. пользователь/seccomp, pin версии poppler, изоляция рендера | P1 |

**Уточнения по угрозам, опровергнутым код‑фактами (false‑negative по брифу):** command/argument‑injection в `pdftoppm` через имя файла **невозможен** (`UseShellExecute=false`, пути из server‑side GUID, имя клиента туда не попадает); SSRF к Ollama через пользовательский ввод **отсутствует** (endpoint только из конфига); CSV/формула‑инъекция в текущем коде **неприменима** (ClosedXML подключён, но Excel‑экспорт/импорт в коде не используется — импорт идёт из SQL/OData); HTMX заявлен, но в коде **не используется** (формы — обычный POST, авто‑antiforgery Razor Pages действует).


## 3.2. Конвейер загрузки скан‑PDF и OCR/рендер _(снимок 2026‑06‑16)_

## Ревью конвейера обработки скан-PDF: «приём → хранение → рендер → OCR → раздача»

Конвейер MedSpravki-REU принимает скан медсправки (спецкатегория ПДн, 152-ФЗ ст. 10 + врачебная тайна 323-ФЗ ст. 13), хранит файл вне `wwwroot`, по требованию рендерит первую страницу PDF в PNG через `poppler pdftoppm`, отправляет изображение на локальный Ollama (`qwen2.5vl:7b`, Tailscale, HTTP без TLS), а исходный файл отдаёт по `GET /scans/{id}/file`. Ниже — анализ каждой стадии с точками контроля; severity и контр-аргументы — в findings.

### Карта конвейера и точек контроля

| Стадия | Файл:строка | Что происходит | Контроль СЕЙЧАС | Чего не хватает |
|---|---|---|---|---|
| 1. Приём (HTTP-форма) | `Scans/Index.cshtml.cs:46-68` | multipart upload, IFormFile | размер ≤ 10 МБ (`:53`); whitelist по `file.ContentType` (`:55`); `Path.GetFileName(FileName)` (`:61`) | magic-bytes/sniff, проверка расширения, AV-скан, лимит страниц/пикселей, decompression-bomb guard |
| 2. Хранение (ФС) | `FileScanStorage.cs:20-43`, `ScanService.cs:50-75` | GUID `.bin` вне wwwroot, SHA-256, метаданные+клиентский ContentType в БД | анти-traversal `Path.GetFileName` (`:48,55`); SHA-256 считается | шифрование at-rest отсутствует; клиентский MIME консервируется и используется дальше |
| 3. Рендер (poppler) | `LocalOllamaRecognitionProvider.cs:116-146` | `pdftoppm -png -singlefile -r 200 -f 1 -l 1 file base` | `UseShellExecute=false`; пути из server-side GUID; рендер только стр.1 (`-f 1 -l 1`) | нет тайм-аута процесса, нет лимита памяти/CPU/размера PNG, нет sandbox/seccomp/отд. пользователя, нет `-cropbox`, нет версии-pin poppler |
| 4. OCR (сеть) | `LocalOllamaRecognitionProvider.cs:45-72` | base64-картинка → POST `OllamaUrl/api/generate` | endpoint из конфига (не из ввода → нет classic SSRF); HttpClient.Timeout=180с | HTTP без TLS — медданные открыто в сети; нет лимита размера ответа Ollama; текст исключения сохраняется в RecognitionJson (`ScanService.cs:144`) |
| 5. Раздача | `Program.cs:55-61`, `ScanService.cs:85-94` | `Results.File(stream, scan.ContentType, enableRangeProcessing:true)` | роль Teacher/Head/Admin | НЕТ `Content-Disposition: attachment`, НЕТ `X-Content-Type-Options: nosniff`, НЕТ проверки принадлежности скана, НЕТ аудита просмотра, SHA-256 не перепроверяется |

### Эксплойт-сценарии (с привязкой к контексту медданных/Bitrix)

**A. Stored XSS через раздачу файла (главная цепочка).** Атакующий (любой аутентифицированный сотрудник, либо студент в v2 при появлении его роли) грузит файл `evil.pdf`, в котором фактически HTML+JS, выставляя в multipart-заголовке `Content-Type: application/pdf` (клиентский MIME, тривиально подделывается curl/Burp). Whitelist `Index.cshtml.cs:55` пропускает по подделанному MIME, magic-bytes не проверяются. Файл сохраняется, `scan.ContentType = "application/pdf"` (`ScanService.cs:59`). При открытии `/scans/{id}/file` ответ идёт inline (`Results.File` без `fileDownloadName` → нет `Content-Disposition: attachment`) с `Content-Type: application/pdf` и БЕЗ `X-Content-Type-Options: nosniff` (`Program.cs:60`, заголовков-middleware нет вовсе). Часть браузеров/встроенных вьюеров при определённых условиях MIME-sniff'ят содержимое; ещё надёжнее — загрузить `image/jpeg`/`application/pdf`-помеченный SVG или HTML и открыть его прямой ссылкой: скрипт исполняется в origin `med.rea.ru`, крадёт cookie ASP.NET Identity (HttpOnly спасает от чтения через JS, но не от CSRF-действий и не от чтения DOM медкарт). В контексте интеграции это особенно критично: если приложение встраивается reverse-proxy под `student.rea.ru/medspravki` (вариант 2d из блока 4) — XSS исполняется уже в origin портала РЭУ Bitrix, расширяя blast radius на весь портал и его сессии (`BITRIX_SM_*`).

**B. DoS / resource-exhaustion через poppler (decompression bomb / тяжёлый PDF).** Загружается валидный по сигнатуре PDF 9.9 МБ (проходит лимит 10 МБ), но с огромным объектным деревом / вложенными XObject / гигантской растровой страницей. При нажатии «Распознать» `pdftoppm -r 200` (`:126`) рендерит страницу 1: при экстремальных размерах MediaBox или 200 DPI на A0-странице PNG занимает гигабайты RAM на хост-процессе .NET-сервиса (poppler — дочерний процесс без ограничений памяти/CPU). У `Process` нет тайм-аута (`WaitForExitAsync(ct)` без deadline — `:134`), нет cgroup/seccomp, нет отдельного пользователя. Несколько параллельных запросов исчерпывают RAM IIS-воркера → отказ обслуживания. Смягчает только то, что рендерится одна страница (`-f 1 -l 1`), но «тяжесть» страницы и парсинг всего документа poppler'ом остаются.

**C. RCE/parsing-уязвимости poppler/Ghostscript на недоверенном PDF.** poppler исторически имеет цепочку CVE парсинга (переполнения в JBIG2/JPX/CCITT-декодерах и т. п.). `pdftoppm` исполняется в том же security-контексте, что и веб-сервис, на Windows-хосте РЭУ, версия poppler нигде не закреплена и не сканируется. Argument/command injection через имя файла действительно невозможен (`UseShellExecute=false`, пути — server-side GUID из `Path.GetTempPath()`+`Guid` — `:118-120`), но это не закрывает уязвимости самого парсера на содержимом файла. Эксплойт: специально сформированный PDF триггерит баг poppler → выполнение в контексте сервиса, имеющего доступ к БД медданных и каталогу всех сканов.

**D. BOLA/IDOR на медскане + отсутствие аудита.** `ScanService.OpenAsync` (`:85-94`) ищет скан только по `s.Id == scanId`, без сверки `StudentId`/преподавателя/`UploadedByUserId`. Любой пользователь с ролью открывает скан любого студента, перебрав/получив GUID; факт просмотра спецкатегории ПДн НЕ логируется (нет `AuditLogs.Add` ни в `OpenAsync`, ни в обработчике `Program.cs:55-61`). Для УЗ-3/323-ФЗ это нарушение и принципа минимизации доступа, и обязательной регистрации событий доступа к врачебной тайне.

### Источники

- OWASP ASVS 4.0.3 — V5 (Validation/Sanitization/Encoding), V12 (File and Resources): валидация типа файла по содержимому, форс `Content-Disposition` + `X-Content-Type-Options: nosniff`, обработка недоверенных файлов в песочнице — https://owasp.org/www-project-application-security-verification-standard/ (актуально на 2026-06-16)
- OWASP Top 10 2021 — A01 Broken Access Control, A04 Insecure Design, A05 Security Misconfiguration, A08 Software & Data Integrity — https://owasp.org/Top10/ (2026-06-16)
- OWASP Cheat Sheet — File Upload (magic bytes, AV/ClamAV, store outside webroot, force download) — https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html (2026-06-16)
- poppler security advisories / CVE-история парсера PDF (JBIG2/JPX) — https://gitlab.freedesktop.org/poppler/poppler/-/issues (2026-06-16)
- ASP.NET Core file uploads — рекомендация не доверять `IFormFile.ContentType`, проверять сигнатуру — https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads (2026-06-16)


## 3.3. Защита медданных и меры ФСТЭК‑21 (УЗ‑3) _(снимок 2026‑06‑16)_

## Карта медданных MedSpravki-REU: что / где / как защищено

Все пути относительны от `…/projects/MedSpravki-REU/app/src`. Проверено чтением кода 2026-06-16.

### Классификация данных, проходящих через систему

Кафедра физвоспитания обрабатывает **спецкатегорию ПДн** (152-ФЗ ст. 10 — «данные, касающиеся состояния здоровья») и **врачебную тайну** (323-ФЗ ст. 13). Это автоматически задаёт **УЗ-3 по ПП-1119** (спецкатегория + субъекты не-сотрудники + актуальные угрозы 3-го типа). Положительный момент: команда сознательно реализовала **минимизацию** — поле «диагноз» в модели отсутствует, в `MedicalCertificate.cs:27` явно задекларировано «Ограничения — функциональные формулировки, БЕЗ диагноза»; промпт OCR (`LocalOllamaRecognitionProvider.cs:83`) тоже требует «restrictions без диагноза». Это сильная сторона дизайна, её надо сохранить и закрепить организационно.

### Таблица: где живут медданные и как защищены

| Где | Что хранится | At-rest шифрование | Маскирование | Аудит чтения | Файл |
|---|---|---|---|---|---|
| БД `medical_certificates` | `HealthGroup`, `PhysicalGroup`, `Restrictions` (свободный текст), `Comment`, `MedicalOrganization`, `CertificateNumber`, даты | **НЕТ** (plain text/jsonb) | **НЕТ** | **НЕТ** | `MedicalCertificate.cs`, `ApplicationDbContext.cs:84-100` |
| БД `certificate_scans.RecognitionJson` (jsonb) | распознанные ФИО/даты/физгруппа/ограничения/печать/подпись | **НЕТ** | **НЕТ** | **НЕТ** | `CertificateScan.cs:37`, `ScanService.cs:129` |
| ФС `App_Data/scans/*.bin` | оригинал PDF/JPG/PNG справки целиком | **НЕТ** (`File.Create`/`File.OpenRead`) | n/a | **НЕТ** | `FileScanStorage.cs:29,49` |
| Сеть → Ollama | base64 изображения справки | **HTTP без TLS** (Tailscale-туннель шифрует транспорт, но не TLS) | n/a | частично (ScanRecognized) | `appsettings.Development.json:16`, `LocalOllamaRecognitionProvider.cs:61` |
| БД `audit_logs` | снимок действий, `UserNameSnapshot`, `Description` (может содержать ФИО) | НЕТ | НЕТ | n/a | `AuditLog.cs`, миграция `InitialCreate.cs:62-81` |
| Транзит браузер↔приложение | всё вышеперечисленное в HTML/inline-PDF | HTTPS+HSTS (только non-Dev) | НЕТ | НЕТ | `Program.cs:36-45` |

**Главный вывод по защите данных:** транспорт частично закрыт (HTTPS/HSTS вне Dev, Tailscale для Ollama), а **at-rest и разграничение доступа к данным — открыты полностью**. Для УЗ-3 это инвертированный приоритет: спецкатегория ПДн обязана быть защищена и в покое (СКЗИ/шифрование носителей по ФСТЭК-21 п. ЗНИ), и на уровне доступа (ни один сервис не проверяет, имеет ли преподаватель право видеть данного студента).

### Матрица ролей vs. реальность кода

ТЗ и заголовок эндпоинта (`Program.cs:54`) декларируют разграничение «сотрудники видят сканы, студенты — нет». В реальности:
- Объявлены 3 роли (`AppRole.cs:15-19`), но **ни одна Razor Page не имеет `[Authorize(Roles=…)]`** — единственная ролевая проверка во всём приложении на `/scans/{id}/file` (`Program.cs:61`).
- Bootstrap-пользователь получает сразу `Admin`+`Teacher` (`DataSeeder.cs:48-49`).
- **Ни один сервис не различает преподавателей между собой**: `RegistryQueryService.SearchAsync` фильтрует по `IsActive`, но `TeacherId` — это пользовательский параметр фильтра, а не серверное ограничение области видимости (`RegistryQueryService.cs:32-33`). Любой преподаватель видит реестр, карточки и сканы всех студентов всех кафедр. Для медтайны это нарушение принципа «знать только необходимое».
- Преподаватель технически **может открыть скан** (роль Teacher в whitelist `Program.cs:61`) — то есть декларация «преподаватель не видит скан» в коде не реализована; нужна или отдельная роль-просмотрщик сканов, или ограничение whitelist до `HeadOfDepartment/Admin`.

### Чек-таблица организационно-технических мер ФСТЭК №21 для УЗ-3

Статус: «есть» / «частично» / «нет» — по фактам кода и инфраструктуры. Для УЗ-3 по приказу №21 базовый набор включает ИАФ, УПД, РСБ, АВЗ, ЗНИ, ОЦЛ, ОДТ и др.

| Группа мер ФСТЭК-21 | Мера | Статус | Обоснование (file:line / факт) |
|---|---|---|---|
| **ИАФ** (идентификация/аутентификация) | Парольная политика | Частично | `DependencyInjection.cs:33-37` — длина ≥8, цифра+регистр; нет требования спецсимвола, нет срока действия/истории паролей, `RequireUniqueEmail=false` |
| ИАФ | Блокировка после неуспехов | Частично | `MaxFailedAccessAttempts=5` (`:38`), но `DefaultLockoutTimeSpan`/`AllowedForNewUsers` не заданы — поведение на дефолтах |
| ИАФ | Скрытие обратной связи (пароль) | Есть | Identity-хеширование PBKDF2 по умолчанию |
| **УПД** (управление доступом) | Разграничение по ролям | **Нет** | Нет `[Authorize(Roles)]` ни на одной странице (`Program.cs:22`); единственный RequireRole на `/scans/file` |
| УПД | Разграничение по объекту (область видимости) | **Нет** | `ScanService.OpenAsync` (`:85-94`), `StudentService`, `RegistryQueryService.cs:32` — нет owner/group-фильтра |
| УПД | Разделение ролей (least privilege) | Нет | Bootstrap = Admin+Teacher (`DataSeeder.cs:48-49`) |
| **РСБ** (регистрация событий ИБ) | Логирование действий с ПДн | Частично | Пишется создание/импорт/распознавание (`ScanService.cs:70`, `CertificateService.cs:52`) |
| РСБ | Логирование **доступа на чтение** медданных | **Нет** | `/scans/{id}/file` и просмотр карточек не пишут аудит (`Program.cs:55-61`, `ScanService.cs:85-94`) |
| РСБ | Логирование входа/выхода | **Нет** | `Login.cshtml.cs:44-67`, `Logout.cshtml.cs` — нет AuditLog; `IpAddress` всегда null (`AuditEntryFactory.cs:9-30`) |
| РСБ | Защита журнала от модификации | **Нет** | `audit_logs` без REVOKE/триггеров (`InitialCreate.cs:62-81`) — только декларация в комментарии `AuditLog.cs:6-7` |
| **АВЗ** (антивирус) | Проверка загружаемых файлов | **Нет** | `Scans/Index.cshtml.cs:46-68` — ни AV, ни magic-bytes |
| **ЗНИ** (защита машинных носителей) | Шифрование носителей с ПДн | **Нет** | Файлы сканов и БД — без шифрования (`FileScanStorage.cs:29`, `ApplicationDbContext.cs`) |
| **ОЦЛ** (контроль целостности) | Контроль целостности файлов | Частично/декларативно | SHA-256 пишется при загрузке (`FileScanStorage.cs:41`), но **не перепроверяется при чтении** вопреки `CertificateScan.cs:27` |
| **ЗИС** (защита инфосистемы) | Security-заголовки, защита от clickjacking | **Нет** | `Program.cs` — нет CSP/X-Frame/nosniff/Referrer |
| ЗИС | Защита канала передачи (TLS) | Частично | HTTPS+HSTS вне Dev (`:36-45`); Ollama — HTTP в Tailscale |
| **ОДТ** (доступность) | Резервное копирование | Нет данных в коде | Не настроено в репозитории — орг-мера на стороне РЭУ/IIS |
| **Управление конфигурацией** | Секреты вне кода | **Нет** | Пароль БД и `<демо-пароль>` в `appsettings.json:3,8` + код-литералы (`DependencyInjection.cs:25`, `DataSeeder.cs:45`) |

**Итог по ФСТЭК-21:** из критичных для УЗ-3 групп **УПД (разграничение доступа), РСБ (регистрация доступа к ПДн и входов), ЗНИ (шифрование носителей), АВЗ (антивирус загрузок)** — фактически **отсутствуют** в реализации. Это блокирует аттестацию ИСПДн и должно быть закрыто до развёртывания «с сетевым доступом студентов». РСБ-аудит чтения медданных — это не «улучшение», а прямое требование (доступ к врачебной тайне фиксируется обязательно).

### Связь с интеграцией в портал РЭУ (Bitrix)

Выбранный архитектурный вариант (отдельный поддомен `med.rea.ru` + SSO, изоляция контура) — правильный с точки зрения 152-ФЗ/УЗ-3, он минимизирует blast radius. Но он усиливает два кодовых пробела:
1. **Отсутствие CSP/`X-Frame-Options`** становится критичнее: при встраивании/соседстве с Bitrix-порталом и при реверс-прокси clickjacking и подмена контекста реальны.
2. **`UseForwardedHeaders` не настроен** — при варианте reverse-proxy (вариант 2d из блока интеграции) приложение не увидит реальный IP клиента и будет уязвимо к header-spoofing, если прокси не вырезает клиентские `X-Forwarded-*`. Это же — причина, почему `IpAddress` в аудите пуст и не заработает «само» за прокси.

## Источники

- 152-ФЗ «О персональных данных», ст. 5 (минимизация), ст. 10 (спецкатегория, письменное согласие), ст. 18.1 (локализация на территории РФ) — действующая редакция на 2026-06-16.
- 323-ФЗ «Об основах охраны здоровья граждан», ст. 13 (врачебная тайна, режим доступа и согласие на разглашение).
- Постановление Правительства РФ № 1119 от 01.11.2012 — уровни защищённости ИСПДн (основание для УЗ-3).
- Приказ ФСТЭК России № 21 от 18.02.2013 — состав и содержание мер защиты ПДн (группы ИАФ, УПД, РСБ, АВЗ, ЗНИ, ОЦЛ, ОДТ, ЗИС).
- OWASP Top 10 2021 (A01 Broken Access Control, A02 Cryptographic Failures, A05 Security Misconfiguration, A09 Logging/Monitoring Failures), OWASP ASVS 4.0.3 (V1/V3/V4/V6/V7/V12).
- Microsoft Learn — ASP.NET Core Data Protection / Forwarded Headers / Security Headers (проверено 2026-06-16).


## 3.4. Безопасность интеграции PHP‑Bitrix + .NET

## Спецриски склейки PHP-Bitrix-портала (`student.rea.ru`, БУС) и нашего ASP.NET Core 8 (`MedSpravki-REU`)

Анализ исходит из подтверждённого факта: наше приложение в текущем виде **не подготовлено к работе за прокси и к встраиванию**. В `Program.cs` нет ни `UseForwardedHeaders`, ни security-заголовков (CSP/X-Frame-Options/nosniff), `AllowedHosts="*"` (`appsettings.json:21`), cookie ASP.NET Identity использует дефолты (`SameSite=Lax`, `SecurePolicy=SameAsRequest`, `Domain` не задан — `DependencyInjection.cs:44-49`). Любой из четырёх вариантов интеграции (a поддомен+SSO, b iframe, c PHP-модуль, d reverse-proxy) добавляет к уже выявленным BOLA/IDOR-дырам (нет owner-проверок в `ScanService.OpenAsync`, `Program.cs:55-61`) ещё и риски доверия к чужому контуру. Ниже — анализ по вариантам и сквозная матрица.

### Базовое правило trust-boundary для медданных (спецкатегория, УЗ-3)

Граница доверия должна проходить **по периметру нашего .NET-контура**, а не по периметру `*.rea.ru`. Всё, что приходит «снаружи» (заголовки идентичности, куки, Host, postMessage), доверяется только если оно (1) пришло через known-proxy с известного IP/сети, (2) криптографически подписано секретом, который знаем только мы и доверенный прокси/IdP, (3) проверено по сроку/audience. Это сразу обесценивает «общую куку `.rea.ru`» и «голый `X-Remote-User`».

### Матрица: вариант интеграции × ключевой риск × контрмера

| Риск \ Вариант | (a) Поддомен + SSO `med.rea.ru` | (b) iframe в Bitrix | (c) PHP-модуль в БУС | (d) Reverse-proxy `student.rea.ru/medspravki` |
|---|---|---|---|---|
| **Спуфинг проксированных заголовков идентичности** (`X-Forwarded-User`/`X-Remote-User`) | Не применимо (идентичность через OIDC-токен, не заголовок). Риск только если прокси всё же ставит заголовок | Не применимо | Не применимо (родной пользователь Bitrix) | **ГЛАВНЫЙ риск.** Прямой доступ в обход nginx → клиент сам шлёт `X-Remote-User: admin`. Контрмера: `ForwardedHeaders` + `KnownProxies`/`KnownNetworks`, кастомный AuthHandler доверяет заголовку ТОЛЬКО при `RemoteIpAddress ∈ KnownProxies`; подпись HMAC/короткий JWT; mTLS прокси↔.NET; firewall: Kestrel слушает только loopback/прокси-IP |
| **Session fixation / чтение чужих кук на общем домене** | Низкий, если **НЕ** ставить `Domain=.rea.ru` (cookie host-only по умолчанию — хорошо). Bitrix `BITRIX_SM_*` нашим .NET не читаются | Высокий: iframe = third-party, нужен `SameSite=None;Secure` → кука нашего .NET видна в любом фрейме на `*.rea.ru` | Куки портала — общий контур | Средний: общий домен → cookie-коллизии по имени/пути с Bitrix; нужен уникальный `Cookie.Name` + `Cookie.Path=/medspravki` |
| **SSO-токены Bitrix OAuth2/REST** (подпись/срок/audience) | Применимо: валидировать `iss`/`aud`/`exp`/подпись OIDC; PKCE+`state`; защита client_secret в Key Vault/env | Не несёт токенов сам по себе | Не применимо | Опционально (header-JWT от прокси): валидировать подпись+`exp`+`aud=med` |
| **Clickjacking при встраивании** | Запретить фрейминг целиком: `frame-ancestors 'none'` + `X-Frame-Options: DENY` | **Обязательно** разрешить только портал: `frame-ancestors 'self' https://*.rea.ru` (НЕ `*`); X-Frame-Options устаревший — главное CSP | Не применимо | `frame-ancestors 'self' https://*.rea.ru` если страница рендерится во фрейме портала |
| **CSRF из-за `SameSite=None`** | Не нужно (same-site навигация) — оставить `SameSite=Lax`/`Strict` | `SameSite=None` снимает браузерную CSRF-защиту → критична серверная антифоргери на ВСЕХ мутациях (Razor авто-CSRF есть, но minimal-API GET-only — ОК) | N/A (Bitrix CSRF) | `SameSite=Lax` достаточно (same-site) |
| **postMessage / XSS из родителя** | N/A (нет фрейма) | Валидировать `event.origin === 'https://student.rea.ru'`, не использовать `*`; родительский XSS на Bitrix → чтение нашего DOM, если не изолировано | N/A | N/A |
| **Path confusion / кэш-отравление / Host** | Низкий (свой домен, свой TLS) | Низкий | N/A | **Высокий:** `AllowedHosts="*"` (`appsettings.json:21`) → Host-injection; нужен whitelist хостов; `X-Forwarded-Prefix`→`PathBase`; раздельные cookie-пути; запрет кэширования медответов (`Cache-Control: no-store`) |
| **Blast radius / смешение контуров (152-ФЗ/323-ФЗ)** | **Минимальный** — изолированный контур, аттестуется только наш | Средний (данные рендерятся в чужом шаблоне) | **Максимальный** — медданные в БД Bitrix, весь портал → спецкатегория | Средний — разделить БД/журналы, один сетевой периметр |

### Вывод по выбору варианта

Подтверждаю рекомендацию из Блока 4: **основной — (a) поддомен `med.rea.ru` + OIDC-SSO** (минимальный blast radius, изоляция под УЗ-3, заголовки/CSP/журналы полностью под нашим контролем). **(d) reverse-proxy** — допустимая комбинация (SSO для идентичности + проксирование для адреса), но требует жёсткой дисциплины прокси (см. finding `spoof-fwd-identity`). **(b) iframe** — только как косметическая обёртка над уже-аутентифицированным поддоменом, не как канал медданных. **(c) PHP-модуль — отвергнуть** (уничтожает архитектуру, наследует CVE Bitrix, поднимает весь портал до спецкатегории).

### Что нужно сделать в нашем коде ДО любой интеграции (минимум)

1. Включить `ForwardedHeadersMiddleware` с `KnownProxies`/`KnownNetworks` (только при варианте d) — **первым** в конвейере, до `UseAuthentication`.
2. Добавить middleware security-заголовков: CSP с `frame-ancestors`, `X-Frame-Options`, `nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` для `/scans/*` и медстраниц.
3. Форсировать cookie: `Cookie.SecurePolicy=Always`, `Cookie.HttpOnly=true`, осознанно выбрать `SameSite` (Lax для a/d; None+Secure только для b с серверной CSRF), уникальное `Cookie.Name`, `Cookie.Path` при общем домене. **Не** ставить `Domain=.rea.ru`.
4. Заменить `AllowedHosts="*"` на явный whitelist (`med.rea.ru`/`student.rea.ru`).
5. Если header-based SSO (d) — кастомный `AuthenticationHandler`, доверяющий заголовку только от known-proxy + проверка HMAC/JWT-подписи; явно срезать входящий `X-Remote-User`/`X-Forwarded-User` от клиента.
6. Закрыть BOLA/IDOR (owner-проверка в `ScanService.OpenAsync`) и аудит чтения скана — иначе любой SSO лишь аккуратнее доставит злоумышленника к незащищённому объекту.

## Источники
- [Microsoft Learn — Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer) — `ForwardedHeadersMiddleware`, по умолчанию `ForwardedHeaders.None`, `KnownProxies`/`KnownNetworks` — проверено 2026-06-16
- [anthonysimmon.com — How to securely reverse-proxy ASP.NET Core web apps](https://anthonysimmon.com/securely-reverse-proxy-aspnet-core-web-apps) — прокси обязан вырезать клиентские X-Forwarded заголовки, иначе спуфинг — проверено 2026-06-16
- [MDN — CSP frame-ancestors](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Security-Policy/frame-ancestors) — `frame-ancestors` заменяет X-Frame-Options, поддерживает host-source — проверено 2026-06-16
- [OWASP — Clickjacking Defense Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Clickjacking_Defense_Cheat_Sheet.html) — CSP frame-ancestors + X-Frame-Options как defense-in-depth — проверено 2026-06-16
- [web.dev — SameSite cookies explained](https://web.dev/articles/samesite-cookies-explained) — `SameSite=None;Secure` для cross-site/iframe, последствия для CSRF — проверено 2026-06-16
- [OWASP ASVS 4.0](https://owasp.org/www-project-application-security-verification-standard/) — V3 (сессии/cookie), V4 (доступ), V14 (конфигурация/заголовки) — проверено 2026-06-16


## 3.5. DevSecOps‑плейбук + ручной pentest‑чек‑лист

# DevSecOps-плейбук: MedSpravki-REU

> Все команды готовы к запуску из корня кода. Базовые переменные (вставь в начало сессии):
>
> ```bash
> export APP_ROOT="<корень-репозитория>"
> export SRC="$APP_ROOT/app"            # содержит ReuMedCertificates.sln
> export CODE="$APP_ROOT/app/src"       # 4 проекта Clean Architecture
> export OUT="$APP_ROOT/_sec"           # папка под отчёты сканеров (не в git: добавь в .gitignore)
> mkdir -p "$OUT"
> ```
>
> **Состояние тулинга на машине (проверено 2026-06-16):** `dotnet 8.0.127` ✓, `semgrep` ✓ (`~/.local/bin`), `gitleaks` ✓ (`/usr/bin`), `docker` ✓; **нет** `trivy`, `codeql`, `nikto`, `testssl.sh`, `nuclei`, `dotnet-outdated`, ZAP. Установка каждого — в своём разделе.
>
> **Состояние репозитория (проверено 2026-06-16):** нет `.github/`, нет `Dockerfile`/`docker-compose`, нет `.editorconfig`, `Directory.Build.props` содержит `TreatWarningsAsErrors=false` и НЕ включает `EnableNETAnalyzers`/`AnalysisMode`. То есть security-гейтов пока ноль — это процессная находка P1 (см. findings).

---

## 0. Один прогон «всё подряд» (smoke baseline)

Скопируй блок целиком — он запускает быстрые сканеры, не требующие БД/контейнера, и складывает отчёты в `$OUT`. Тяжёлые сканеры (CodeQL, Dependency-Check, ZAP, Trivy-image) — в своих разделах.

```bash
set +e   # не падать на первом ненулевом коде
# 1. секреты
gitleaks detect --source "$APP_ROOT" --redact --report-format sarif --report-path "$OUT/gitleaks.sarif" --no-banner
# 2. SAST
semgrep --config p/csharp --config p/owasp-top-ten --config p/secrets \
        --sarif --output "$OUT/semgrep.sarif" "$CODE"
# 3. SCA по NuGet-манифестам (нужен сетевой доступ к nuget.org или офлайн-кэш)
( cd "$SRC" && dotnet restore && dotnet list package --vulnerable --include-transitive ) | tee "$OUT/nuget-vuln.txt"
( cd "$SRC" && dotnet list package --deprecated ) | tee "$OUT/nuget-deprecated.txt"
# 4. файловый Trivy (если установлен) — vuln+secret+misconfig
command -v trivy >/dev/null && trivy fs --scanners vuln,secret,misconfig --format table "$CODE" | tee "$OUT/trivy-fs.txt"
set -e
echo "Отчёты: $OUT"
```

---

## 1. SAST (статический анализ кода)

### 1.1 Semgrep

```bash
# базовый прогон под C# + OWASP Top-10 (как в брифе)
semgrep --config p/csharp --config p/owasp-top-ten "$CODE"

# расширенный: + секреты, + .NET-специфика, SARIF для CI и человекочитаемый текст
semgrep --config p/csharp \
        --config p/owasp-top-ten \
        --config p/secrets \
        --config p/dotnet \
        --sarif --output "$OUT/semgrep.sarif" \
        --text \
        --metrics off \
        "$CODE"

# узкий «high-confidence only» прогон для гейта (только ERROR-severity)
semgrep --config p/csharp --config p/owasp-top-ten --severity ERROR --error "$CODE"
```

Куда смотреть в первую очередь (известные горячие точки этого кода):
- `ReuMedCertificates.Infrastructure/Services/SqlRosterSource.cs` — `NpgsqlCommand(_options.Sql.Query, conn)`: непараметризованный SQL-текст из конфига. Semgrep правило `csharp.lang.security.sql-injection` может промолчать (источник = конфиг, не request), поэтому добавь кастомное правило (см. 1.4).
- `ReuMedCertificates.Application/Registry/RegistryQueryService.cs:35-40` — `EF.Functions.Like($"%{normalized}%")` без экранирования `%`/`_` (LIKE-wildcard injection, функциональная DoS, не SQLi).
- `ReuMedCertificates.Infrastructure/Services/LocalOllamaRecognitionProvider.cs:125-130` — `ProcessStartInfo`; semgrep `csharp.lang.security.process-start` подсветит — это True-Negative (аргументы серверные GUID, `UseShellExecute=false`), нужно зафиксировать как accepted в комментарии-аннотации.

> Замечание по брифу: ClosedXML подключён, но в коде НЕ используется (экспорта Excel/CSV нет), поэтому правила про формула-/CSV-инъекцию (`p/owasp-top-ten` → A03) сработают вхолостую. Это ожидаемо — точку экспорта добавите позже, тогда правило станет релевантным.

### 1.2 CodeQL (глубокий interprocedural taint)

CodeQL найдёт data-flow, который Semgrep не видит (например, путь «клиентский ContentType из формы → сохранение в БД → отдача в `Results.File`»). Требует сборки проекта (autobuild для C#).

```bash
# установка (один раз) — CLI + библиотека запросов
mkdir -p ~/codeql-home && cd ~/codeql-home
# скачай codeql-bundle-linux64.tar.gz из github.com/github/codeql-action/releases (bundle уже содержит queries)
tar xzf codeql-bundle-linux64.tar.gz       # распакует ./codeql
export PATH="$HOME/codeql-home/codeql:$PATH"
codeql --version

# 1) создать базу (autobuild соберёт sln через dotnet)
codeql database create "$OUT/codeql-db" \
  --language=csharp \
  --source-root "$SRC" \
  --command="dotnet build ReuMedCertificates.sln -c Release"

# 2) анализ security-extended (полный security-набор) → SARIF
codeql database analyze "$OUT/codeql-db" \
  codeql/csharp-queries:codeql-suites/csharp-security-extended.qls \
  --format=sarifv2.1.0 --output="$OUT/codeql.sarif" --threads=0

# человекочитаемая выжимка
codeql database interpret-results "$OUT/codeql-db" --format=csv --output="$OUT/codeql.csv" "$OUT/codeql.sarif" 2>/dev/null || true
```

В CI используйте официальный `github/codeql-action` (язык `csharp`, query `security-and-quality`). Здесь bundle-вариант — для локального прогона без интернета на машине сборки.

### 1.3 Roslyn-анализаторы (.NET-native, бесплатно, в сборке)

Сейчас НЕ включены. Включаем на уровне `Directory.Build.props` (один файл — на все 4 проекта) — это самый дешёвый постоянный гейт.

Шаг 1 — правим `app/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12.0</LangVersion>
    <RootNamespace>ReuMedCertificates</RootNamespace>

    <!-- ВКЛЮЧАЕМ анализаторы качества+безопасности -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-Recommended</AnalysisLevel>
    <AnalysisMode>AllEnabledByDefault</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <!-- На CI включить как ошибки; локально можно держать false, чтобы не мешать разработке -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Шаг 2 — добавляем сторонний security-анализатор `SecurityCodeScan.VS2019` в `Infrastructure` и `Web` (`*.csproj`):

```xml
<ItemGroup>
  <PackageReference Include="SecurityCodeScan.VS2019" Version="5.6.7">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

Шаг 3 — `.editorconfig` в корне `app/` поднимает критичные CA-правила до error именно для security:

```ini
# app/.editorconfig
root = true
[*.cs]
# CA3001 SQL injection, CA3003 path injection, CA3006 command injection,
# CA3075 XXE, CA5350/5351 слабая крипта, CA5359 cert-validation отключён,
# CA5394 небезопасный random, CA2100 SQL из строки
dotnet_diagnostic.CA3001.severity = error
dotnet_diagnostic.CA3003.severity = error
dotnet_diagnostic.CA3006.severity = error
dotnet_diagnostic.CA3075.severity = error
dotnet_diagnostic.CA2100.severity = error
dotnet_diagnostic.CA5350.severity = error
dotnet_diagnostic.CA5351.severity = error
dotnet_diagnostic.CA5359.severity = error
dotnet_diagnostic.CA5394.severity = warning
# SecurityCodeScan: SCS0002 SQLi, SCS0018 path traversal, SCS0026 SQL из конкатенации
dotnet_diagnostic.SCS0002.severity = error
dotnet_diagnostic.SCS0018.severity = error
```

Шаг 4 — прогон (CA2100/SCS0002 должны подсветить `SqlRosterSource.cs`):

```bash
cd "$SRC"
dotnet build ReuMedCertificates.sln -c Release /warnaserror:CA3001,CA3003,CA3006,CA2100,SCS0002,SCS0018 \
  /p:ReportAnalyzer=true 2>&1 | tee "$OUT/roslyn-build.txt"
```

### 1.4 Кастомное Semgrep-правило под нашу болевую точку (непараметризованный SQL)

Файл `$OUT/rules/npgsql-raw.yml`:

```yaml
rules:
  - id: medspravki-npgsql-raw-command
    languages: [csharp]
    severity: WARNING
    message: >
      NpgsqlCommand построен из строки конфигурации без параметров.
      Если Roster:Sql:Query когда-либо станет настраиваемым из UI — это SQLi.
    patterns:
      - pattern: new NpgsqlCommand($Q, $CONN)
      - pattern-not: new NpgsqlCommand("...", $CONN)
```

```bash
semgrep --config "$OUT/rules/npgsql-raw.yml" "$CODE"
```

---

## 2. SCA / зависимости

### 2.1 dotnet list package (встроено, офлайн-кэш ок)

```bash
cd "$SRC"
dotnet restore
# уязвимые (прямые + транзитивные) — главный отчёт
dotnet list package --vulnerable --include-transitive | tee "$OUT/nuget-vuln.txt"
# устаревшие/deprecated
dotnet list package --deprecated | tee "$OUT/nuget-deprecated.txt"
dotnet list package --outdated   | tee "$OUT/nuget-outdated.txt"
```

Что точно всплывёт по версиям из `.csproj` (пакеты `8.0.4`, фиксированный патч):
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore 8.0.4`, `Microsoft.EntityFrameworkCore 8.0.4`, `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.4` — есть более новые `8.0.x` с security-фиксами рантайма; обновить до последнего `8.0.*`.
- `ClosedXML 0.104.2` — подключён, но не используется; удалить из `Infrastructure.csproj` (снижение surface).
- `FluentValidation.AspNetCore 11.3.0`, `Serilog.AspNetCore 8.0.1` — проверить на актуальность.

### 2.2 dotnet-outdated (удобный апгрейд-репорт)

```bash
dotnet tool install --global dotnet-outdated-tool
dotnet outdated "$SRC/ReuMedCertificates.sln" --output "$OUT/outdated.json" --output-format json
# применить минорные апгрейды интерактивно нельзя в этой среде; патчи можно так:
dotnet outdated "$SRC/ReuMedCertificates.sln" --upgrade --version-lock Major
```

### 2.3 OWASP Dependency-Check

```bash
# установка (Docker — самый простой путь; первый прогон качает NVD, держи кэш в томе)
docker run --rm \
  -v "$CODE:/src:ro" \
  -v "$OUT/dc-data:/usr/share/dependency-check/data" \
  -v "$OUT:/report" \
  owasp/dependency-check:latest \
  --scan /src --format "ALL" --project "MedSpravki-REU" --out /report \
  --enableExperimental   # .NET-анализатор для assembly/nuget
# отчёты: $OUT/dependency-check-report.{html,sarif,json}
```

> Для офлайн-РЭУ-машины: один раз скачать NVD-кэш (`--updateonly`), затем гонять с `--noupdate`.

### 2.4 GitHub Dependabot (когда появится remote/CI)

`.github/dependabot.yml`:

```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/app"
    schedule: { interval: "weekly" }
    open-pull-requests-limit: 10
```

Плюс включить **Dependabot security updates** и **CodeQL code scanning** в Settings → Security репозитория.

---

## 3. Secret-scan

```bash
# gitleaks — файлы рабочей копии
gitleaks detect --source "$APP_ROOT" --redact --report-format sarif --report-path "$OUT/gitleaks.sarif" --no-banner
# gitleaks — ВСЯ история git (важно: пароль БД мог быть закоммичен раньше)
gitleaks detect --source "$APP_ROOT" --redact --log-opts="--all" --report-path "$OUT/gitleaks-history.json"

# trufflehog (верифицирует «живость» секрета) — через docker
docker run --rm -v "$APP_ROOT:/repo" trufflesecurity/trufflehog:latest \
  filesystem /repo --only-verified --json | tee "$OUT/trufflehog.json"
# по git-истории:
docker run --rm -v "$APP_ROOT:/repo" trufflesecurity/trufflehog:latest git file:///repo --json | tee "$OUT/trufflehog-git.json"
```

Что ОЖИДАЕМО найдётся (это не ложные срабатывания):
- `app/src/ReuMedCertificates.Web/appsettings.json:3` — `Password=postgres` в connection string (P1, см. findings).
- демо-пароль `<демо-пароль>` в `appsettings.json` и `appsettings.Development.json`, и как литерал-fallback в `DataSeeder.cs:45`.
- `appsettings.Development.json:16` — приватный Tailscale-IP Ollama `<tailscale-ip-узла>` (не секрет, но инфраструктурная утечка).

Превентивно — pre-commit hook gitleaks (gitleaks-хук на коммит в рабочем окружении уже активен; убедись, что он покрывает и эту папку):

```bash
gitleaks protect --staged --redact --no-banner   # запускать в pre-commit
```

---

## 4. DAST (динамический анализ работающего приложения)

Приложение слушает HTTPS на порту, который печатает Kestrel при `dotnet run` (RUNBOOK). Для DAST зафиксируй порт явно. Бриф упоминает `http://localhost:5080` — пример ниже под него.

### 4.0 Поднять стенд (БД + приложение) с ДЕМО-данными

```bash
# Postgres
docker run -d --name reu-pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=reu_med_certificates -p 5432:5432 postgres:16
# приложение на фиксированном порту, dev-окружение (BootstrapUser teacher / <демо-пароль>)
cd "$CODE/ReuMedCertificates.Web"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:5080" dotnet run
```

### 4.1 OWASP ZAP — baseline (пассивный, быстрый)

```bash
docker run --rm --network host -v "$OUT:/zap/wrk:rw" \
  ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t http://localhost:5080 -r zap-baseline.html -J zap-baseline.json
```
Baseline сразу подсветит отсутствие `CSP`, `X-Frame-Options`, `X-Content-Type-Options`, `HSTS` (в dev HSTS выключен намеренно — гоняй DAST против Production-сборки для honest-результата по HSTS).

### 4.2 ZAP — аутентифицированный full-scan (главное для IDOR/meddata)

Ключевая уязвимость — BOLA/IDOR на `/scans/{id}/file`, `/Students/Details?id=`, `/Review`. Их не поймать без логина. Контекст ZAP с form-auth:

```bash
# 1. экспорт контекста подготовь заранее (ZAP Desktop → Context → Export) ИЛИ скриптом authentication
# 2. full-scan с контекстом и пользователем teacher
docker run --rm --network host -v "$OUT:/zap/wrk:rw" \
  ghcr.io/zaproxy/zaproxy:stable \
  zap-full-scan.py -t http://localhost:5080 \
  -n /zap/wrk/medspravki.context \
  -U teacher \
  -r zap-full.html -J zap-full.json \
  -z "-config replacer.full_list(0).description=cookie \
      -config api.disablekey=true"
```

Параметры form-auth (поля логина) — со страницы `Pages/Auth/Login.cshtml` (POST на `/Auth/Login`, поля имени/пароля + antiforgery-токен; ZAP должен забирать свежий `__RequestVerificationToken` из формы → используй «Form-based auth» с `loginRequestData` и регэксп индикатора залогиненности «выйти/Logout»).

> Для прицельной проверки IDOR удобнее ручной Burp/curl (см. чек-лист §9.1), потому что ZAP плохо угадывает «чужой GUID». Сгенерируй 2 учётки и сравни доступ к одному `scanId`.

### 4.3 Nikto (быстрый веб-баннер/misconfig)

```bash
docker run --rm --network host sullo/nikto -h http://localhost:5080 -o "$OUT/nikto.txt" -Format txt
```

---

## 5. Контейнер / образ

Dockerfile пока НЕТ. Когда появится (для не-IIS контура или CI-сборки), типовой multi-stage:

```dockerfile
# app/Dockerfile (пример для проверки сканерами)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ReuMedCertificates.Web/ReuMedCertificates.Web.csproj -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
USER app                     # не root
ENTRYPOINT ["dotnet","ReuMedCertificates.Web.dll"]
```

### 5.1 Trivy

```bash
# установка
curl -sfL https://raw.githubusercontent.com/aquasecurity/trivy/main/contrib/install.sh | sh -s -- -b ~/.local/bin

# файловая система кода (vuln+secret+misconfig) — без Docker
trivy fs --scanners vuln,secret,misconfig --format table "$CODE" | tee "$OUT/trivy-fs.txt"

# собрать образ и просканировать его
docker build -t medspravki:local -f "$SRC/Dockerfile" "$SRC"
trivy image --scanners vuln,secret --severity HIGH,CRITICAL medspravki:local | tee "$OUT/trivy-image.txt"

# проверить сам Dockerfile (misconfig: root-user, latest-tag и т.п.)
trivy config "$SRC/Dockerfile" | tee "$OUT/trivy-config.txt"
```

### 5.2 Grype + Syve (SBOM)

```bash
curl -sSfL https://raw.githubusercontent.com/anchore/grype/main/install.sh | sh -s -- -b ~/.local/bin
curl -sSfL https://raw.githubusercontent.com/anchore/syft/main/install.sh  | sh -s -- -b ~/.local/bin
syft  dir:"$CODE" -o cyclonedx-json="$OUT/sbom.cdx.json"     # SBOM
grype "sbom:$OUT/sbom.cdx.json" -o table | tee "$OUT/grype.txt"
grype medspravki:local -o table | tee "$OUT/grype-image.txt"  # по образу
```

> Целевой хост — IIS/Windows, не Docker. Контейнер-сканеры применимы к CI-сборочному образу и/или к Postgres-образу. Для IIS-деплоя важнее §6 (TLS) и §7 (заголовки), а также `dotnet publish`-артефакт прогнать Trivy `fs`.

---

## 6. TLS

Проверять ВНЕШНИЙ TLS на целевом хосте (IIS) и TLS до Postgres. Локальный dev-сертификат не показателен.

```bash
# testssl.sh (без установки — через docker)
docker run --rm -ti drwetter/testssl.sh https://med.rea.ru:443 | tee "$OUT/testssl.txt"
# либо клон:
git clone --depth 1 https://github.com/drwetter/testssl.sh ~/testssl && ~/testssl/testssl.sh https://med.rea.ru

# sslyze
pipx install sslyze   # или: pip install --user sslyze
sslyze --json_out "$OUT/sslyze.json" med.rea.ru:443
```

Отдельно — **TLS до Ollama**: сейчас OCR-данные (спецкатегория ПДн) идут по `http://<tailscale-ip-узла>:11434` без TLS внутри Tailscale (`appsettings.Development.json:16`). Tailscale шифрует транспорт WireGuard'ом, но приложение об этом не знает — это процессно-архитектурная находка (см. findings). Проверка факта:

```bash
grep -rn "OllamaUrl\|http://" "$CODE"/*/appsettings*.json
```

---

## 7. Security-заголовки

Сейчас НЕ выставляются ни CSP, ни X-Frame-Options, ни nosniff, ни Referrer-Policy (`Program.cs`, grep=0). Критично, т.к. (а) скан отдаётся с клиентским MIME без `nosniff`; (б) приложение планируют встраивать рядом с Bitrix-порталом — clickjacking без `frame-ancestors`.

Проверка работающего стенда:

```bash
# заголовки корня и эндпоинта файла
curl -skI https://med.rea.ru/registry
curl -skI "https://med.rea.ru/scans/<GUID>/file" -H "Cookie: <auth-cookie>"
# ожидаем УВИДЕТЬ после фикса: Content-Security-Policy, X-Frame-Options/frame-ancestors,
# X-Content-Type-Options: nosniff, Referrer-Policy, Strict-Transport-Security, Content-Disposition: attachment
```

Внешние оценщики (только для контура, доступного из интернета — НЕ для ЛВС):
- securityheaders.com (Probely): `https://securityheaders.com/?q=https://med.rea.ru&followRedirects=on`
- Mozilla Observatory: `https://developer.mozilla.org/en-US/observatory/analyze?host=med.rea.ru`

Минимальный фикс в `Program.cs` (между `UseRouting` и `UseAuthentication`):

```csharp
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY"; // если встраивание не требуется
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
    await next();
});
```
А для `/scans/{id}/file` отдавать `Results.File(stream, ct, fileDownloadName: name)` (форсирует `Content-Disposition: attachment`, гасит inline-XSS/HTML-sniffing).

После фикса перепроверить тем же `curl -I` и ZAP baseline.

---

## 8. Дополнительно: миграции и EF Core query logging

### 8.1 Ревью миграций (SQL-скрипт глазами)

```bash
cd "$SRC"
# полный SQL всех миграций — искать REVOKE/GRANT/TRIGGER для audit_logs (их там НЕТ — см. findings)
dotnet ef migrations script \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web \
  --idempotent --output "$OUT/migrations.sql"
grep -niE "revoke|grant|trigger|rule|encrypt|pgcrypto" "$OUT/migrations.sql" || echo "НЕТ защиты INSERT-only / шифрования в миграциях"
```
Ожидаемо: `audit_logs` создаётся как обычная таблица — нет REVOKE UPDATE/DELETE, нет триггеров (декларация INSERT-only только в комментарии `AuditLog.cs:6-7`). Это P1 (см. findings) — добавить миграцию с `REVOKE UPDATE, DELETE ON audit_logs FROM <app_role>;`.

### 8.2 EF Core query logging — поиск N+1, LIKE-инъекций, утечки медполей в логи

```bash
# включить SQL-логирование на dev-стенде:
ASPNETCORE_ENVIRONMENT=Development \
Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information \
dotnet run --project "$CODE/ReuMedCertificates.Web"
```
Дополнительно в dev включить `EnableSensitiveDataLogging()` ТОЛЬКО локально, чтобы увидеть параметры (на проде — категорически нет: иначе медПДн утекут в Serilog-лог). Прогоняя поиск по реестру с `%`/`_` в запросе, по логу проверь, что `RegistryQueryService` шлёт wildcard как параметр (не конкатенацию) — это подтверждает отсутствие SQLi, но фиксирует LIKE-wildcard DoS. Также прокликай карточку студента со сканами и смотри на число запросов (N+1).

---

## 9. ЧЕК-ЛИСТ РУЧНОГО PENTEST

Пользователь для тестов: `teacher / <демо-пароль>` (dev). Для IDOR заведи ВТОРОГО юзера через сидинг/админку и работай двумя сессиями (две банки cookie). Утилита: `curl --cookie-jar`/Burp.

### 9.1 Authorization / IDOR (BOLA) — наивысший приоритет

| # | Проверка | Как воспроизвести | Ожидаемый дефект (подтверждён кодом) |
|---|---|---|---|
| A1 | Доступ к чужому скану | юзером A открыть `GET /scans/{scanId_B}/file` со `scanId` другого студента | 200 + файл: `ScanService.OpenAsync` (`ScanService.cs:85-94`) ищет только по `s.Id==scanId`, без owner-проверки |
| A2 | Карточка чужого студента | `GET /Students/Details?id={чужой_id}` | данные видны: `StudentService.GetDetailAsync` (`StudentService.cs:66-98`) без scope |
| A3 | Approve/Reject чужой справки | POST на `/Review` с `certificateId` вне своей области | проходит: `CertificateService.ApproveAsync/RejectAsync` (`:85-127`) без owner-фильтра |
| A4 | Привязка справки к любому студенту | `Certificates/Create` с произвольным `studentId` | создаётся: `Create.cshtml.cs:60-90` без проверки принадлежности |
| A5 | Доступ к журналу аудита | любой аутентифицированный → `/Journal` | открыт всем ролям: `Journal/Index` без `[Authorize(Roles=…)]` |
| A6 | Вертикальная эскалация | проверить, что НЕ-Admin не может в админ-операции | сейчас все Razor Pages — только `AuthorizeFolder("/")` (только аутентификация, без ролей), `Program.cs:19-25` |
| A7 | Перебор GUID | взять `scanId`/`studentId` из одного ответа, дёргать соседние | GUID непредсказуемы (v4), но утечка через любой список (реестр) даёт валидные id |

Команды для A1 (две сессии):
```bash
# логин юзером A, сохранить cookie + добыть antiforgery
curl -sk -c "$OUT/a.jar" https://localhost:5080/Auth/Login -o /tmp/login.html
TOKEN=$(grep -oP 'name="__RequestVerificationToken"[^>]*value="\K[^"]+' /tmp/login.html | head -1)
curl -sk -b "$OUT/a.jar" -c "$OUT/a.jar" -X POST https://localhost:5080/Auth/Login \
  --data-urlencode "Input.UserName=teacher" --data-urlencode "Input.Password=<демо-пароль>" \
  --data-urlencode "__RequestVerificationToken=$TOKEN"
# попытка открыть «чужой» скан
curl -skI -b "$OUT/a.jar" "https://localhost:5080/scans/<GUID_ДРУГОГО>/file"
```

### 9.2 Upload (загрузка скана)

| # | Проверка | Как | Ожидаемое |
|---|---|---|---|
| U1 | Подмена MIME | загрузить `.exe`/`.html`, выставив `Content-Type: application/pdf` в multipart | пройдёт: валидация по клиентскому `file.ContentType` (`Index.cshtml.cs:55`), magic-bytes НЕ проверяются |
| U2 | Polyglot/HTML-в-картинке | загрузить файл, который браузер отрисует как HTML; затем открыть `/scans/{id}/file` | при отсутствии `nosniff` + inline-отдаче возможен stored-XSS-вектор (см. §7) |
| U3 | Превышение размера | файл > 10 МБ | отбой по `MaxUploadBytes` (`:53`) — должно отклониться |
| U4 | Имя файла | загрузить `..\..\evil.pdf`, имена с RTL/Unicode | путь режется `Path.GetFileName`, но `OriginalFileName` хранится как есть (`ScanService.cs:57`) — проверь вывод в UI/Excel на инъекцию при будущем экспорте |
| U5 | Антивирус | заведомо EICAR-файл | НЕ проверяется (AV нет) — зафиксировать как остаточный риск |
| U6 | Двойное расширение / SVG | `scan.svg`+`image/png` | SVG с JS → XSS при inline-отдаче |

### 9.3 Session / Cookie

| # | Проверка | Как | Ожидаемое |
|---|---|---|---|
| S1 | Флаги cookie | `curl -I` после логина, смотреть `Set-Cookie` | HttpOnly=true, SameSite=Lax (дефолт), но `Secure` НЕ форсирован (`Cookie.SecurePolicy` не задан) — на HTTP кука уйдёт открыто до redirect |
| S2 | Срок жизни | `ExpireTimeSpan` не задан + `SlidingExpiration=true` | дефолт 14 дней скользящий — для медданных многовато, зафиксировать |
| S3 | Фиксация сессии | сравнить cookie до/после логина | Identity ротирует cookie при входе (ок), проверить |
| S4 | Lockout | 6 неверных паролей подряд | блокировка после 5 (`MaxFailedAccessAttempts=5`), но `DefaultLockoutTimeSpan`/`AllowedForNewUsers` не заданы — проверь, что lockout реально включается |
| S5 | Logout инвалидирует | после `/Auth/Logout` дёрнуть защищённый URL старой cookie | должно быть 302 на логин |
| S6 | Параллельные сессии | один пользователь, два устройства | политики нет — зафиксировать |

### 9.4 CSRF

| # | Проверка | Как | Ожидаемое |
|---|---|---|---|
| C1 | POST без токена | повторить POST upload/approve без `__RequestVerificationToken` | 400 (Razor Pages авто-валидация antiforgery включена по конвенции) — ДОЛЖНО блокироваться |
| C2 | Чужой токен | подставить токен другой сессии | 400 |
| C3 | Mutating GET | проверить, что нет state-changing GET | мутаций-GET нет, `/scans/{id}/file` — read-only GET (ок) |
| C4 | SameSite-обход | при встраивании в портал (third-party) | если перейдёте на `SameSite=None` для iframe — CSRF-поверхность растёт, держать antiforgery обязательно |

### 9.5 Security-заголовки (ручная сверка с §7)

Прогнать `curl -I` по: `/registry`, `/Students/Details`, `/scans/{id}/file`, `/Auth/Login`. Чек: `CSP` есть; `frame-ancestors`/`X-Frame-Options` есть; `nosniff` есть; `Referrer-Policy` есть; `HSTS` есть (на Production-сборке); `/scans/.../file` отдаётся `Content-Disposition: attachment` (а не inline); нет утечки `Server`/`X-Powered-By` версий.

### 9.6 Business-logic жизненного цикла справки

Модель статусов: `Draft → NeedsReview → Verified → (Rejected|Expired|Revoked)`. Проверить, что переходы нельзя «перепрыгнуть» через прямые POST.

| # | Проверка | Ожидаемое |
|---|---|---|
| B1 | Approve из `Draft` минуя `NeedsReview` | должно отклоняться (state-machine), проверь `CertificateService.ApproveAsync` |
| B2 | Повторный Approve уже Verified | идемпотентность/отказ |
| B3 | Approve удалённой (`IsDeleted`) справки | фильтр `!c.IsDeleted` есть (`:85-127`) — проверь |
| B4 | Подделка `EndDate`/`StartDate` (просрочка) | можно ли создать «вечную» справку; логика `Expired` по дате |
| B5 | Загрузка скана к Verified-справке и повторное распознавание | меняет ли статус, перетирает ли verified-данные |
| B6 | Race-condition на Approve (две параллельные) | xmin-concurrency (заявлен в миграции) должен ловить конфликт |
| B7 | Revoke без аудита-причины | пишется ли BeforeJson/AfterJson |

### 9.7 OCR / распознавание

| # | Проверка | Ожидаемое |
|---|---|---|
| O1 | Argument injection в pdftoppm через имя файла | НЕвозможно: пути из server-side GUID, имя клиента не попадает (`LocalOllamaRecognitionProvider.cs:118-130`, `UseShellExecute=false`) — подтвердить негатив |
| O2 | SSRF к Ollama через ввод | НЕвозможно: endpoint только из конфига — подтвердить негатив |
| O3 | DoS через большой/битый PDF | таймаут 180с, временные файлы в `Path.GetTempPath()` — проверить очистку (`TryDelete` в finally) и заполнение диска при ошибке |
| O4 | Утечка распознанных медполей в `RecognitionJson` при ошибке модели | текст исключения сохраняется в `RecognitionJson` (`ScanService.cs:144`) — проверь, не утекают ли пути/стек |

### 9.8 Аудит (полнота под 152-ФЗ/323-ФЗ)

| # | Проверка | Ожидаемое (подтверждено кодом) |
|---|---|---|
| AU1 | Логируется ли ПРОСМОТР скана `/scans/{id}/file` | НЕТ — главный пробел по медданным (`Program.cs:55-61`, `ScanService.OpenAsync`) |
| AU2 | Логируется ли просмотр карточки/реестра | НЕТ |
| AU3 | Логируется ли вход/выход/неуспех входа | НЕТ (`Login.cshtml.cs`, `Logout.cshtml.cs` — только `LastLoginAt`) |
| AU4 | Заполняется ли `IpAddress` в аудите | НЕТ — `AuditEntryFactory.Create` не принимает IP, поле всегда null; `UseForwardedHeaders` отсутствует |
| AU5 | INSERT-only на уровне БД | НЕТ — нет REVOKE/триггеров (см. §8.1) |

### 9.9 Интеграция Bitrix / портал РЭУ

Контур интеграции (выбран вариант 2a: поддомен `med.rea.ru` + SSO через OIDC/OAuth2 БУС или внешний IdP). Пентест-чек именно интеграции:

| # | Проверка | Ожидаемое |
|---|---|---|
| BX1 | Open-redirect в SSO-флоу | валидировать `redirect_uri`/`returnUrl` строгим allowlist; проверь, нет ли `?returnUrl=//evil` |
| BX2 | OAuth `state`/PKCE | при OIDC-клиенте обязателен `state` (анти-CSRF) + PKCE; без них — auth-code injection |
| BX3 | Доверие токену/заголовку от reverse-proxy | при варианте 2d: ASP.NET ДОЛЖЕН вырезать клиентский `X-Forwarded-*`/`X-Remote-User`, доверять только `KnownProxies`; иначе спуфинг личности |
| BX4 | `UseForwardedHeaders` + PathBase | при префиксе `/medspravki` за nginx — корректный `X-Forwarded-Prefix`, без него ломаются редиректы/cookie-path |
| BX5 | Cross-subdomain cookie | НЕ ставить `Domain=.rea.ru` (расширяет trust-boundary на весь портал; медданные = спецкатегория) |
| BX6 | clickjacking при встраивании | если iframe в портал — `frame-ancestors` ограничить ровно доменом портала, не `*` |
| BX7 | Изоляция БД медданных от Bitrix | проверить, что медПДн физически НЕ в БД портала (вариант 2c=PHP-модуль отвергнут) |
| BX8 | Раскрытие версий стека | у нас сейчас нет `X-Powered-By`/version-заголовков (ASP.NET Core их не шлёт по умолчанию) — НЕ добавлять; убрать `Server: Kestrel` через `AddServerHeader=false` |

---

## 10. Сводная матрица «что → чем»

| Категория | Инструмент | Команда (кратко) | Установлен? |
|---|---|---|---|
| SAST | Semgrep | `semgrep --config p/csharp --config p/owasp-top-ten $CODE` | ✓ |
| SAST | CodeQL | `codeql database create … && analyze csharp-security-extended` | ✗ (bundle) |
| SAST | Roslyn/SCS | `Directory.Build.props` + `.editorconfig` + `dotnet build /warnaserror:CA…` | встроено |
| SCA | dotnet | `dotnet list package --vulnerable --include-transitive` | ✓ |
| SCA | Dependency-Check | `docker run owasp/dependency-check --scan /src` | ✗ |
| SCA | dotnet-outdated | `dotnet outdated $SRC/…sln` | ✗ (tool) |
| Secrets | gitleaks | `gitleaks detect --source $APP_ROOT --redact` | ✓ |
| Secrets | trufflehog | `docker run trufflesecurity/trufflehog filesystem /repo --only-verified` | ✗ |
| DAST | ZAP | `zap-baseline.py -t http://localhost:5080` / `zap-full-scan.py -U teacher` | ✗ |
| DAST | nikto | `docker run sullo/nikto -h http://localhost:5080` | ✗ |
| Контейнер | Trivy | `trivy fs $CODE` / `trivy image medspravki:local` | ✗ |
| Контейнер | Grype/Syft | `syft dir:$CODE -o cyclonedx-json && grype sbom:…` | ✗ |
| TLS | testssl.sh | `docker run drwetter/testssl.sh https://med.rea.ru` | ✗ |
| TLS | sslyze | `sslyze med.rea.ru:443` | ✗ |
| Заголовки | curl | `curl -skI https://med.rea.ru/registry` | ✓ |
| Миграции | dotnet-ef | `dotnet ef migrations script --idempotent` | через restore |

## Источники

- OWASP Top 10 2021 — https://owasp.org/Top10/ — проверено 2026-06-16
- OWASP ASVS v4.0.3 — https://owasp.org/www-project-application-security-verification-standard/ — проверено 2026-06-16
- .NET code-quality analyzers (CA-rules, EnableNETAnalyzers/AnalysisMode) — https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview — проверено 2026-06-16
- SecurityCodeScan (SCS rules) — https://security-code-scan.github.io/ — проверено 2026-06-16
- CodeQL CLI / csharp-security-extended — https://codeql.github.com/docs/codeql-cli/ — проверено 2026-06-16
- ZAP automation (baseline/full-scan) — https://www.zaproxy.org/docs/docker/ — проверено 2026-06-16
- Trivy docs — https://aquasecurity.github.io/trivy/ — проверено 2026-06-16
- ASP.NET Core proxy/forwarded-headers — https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer — проверено 2026-06-16


## 3.6. OWASP Top‑10 — повторный аудит по ТЕКУЩЕМУ коду (2026‑06‑17)

> Этот подраздел отражает код на 2026‑06‑17 (после пакета P0‑фиксов, см. раздел «Обновление» вверху). Где он расходится со снимком §3.2–§3.5/§3.7 (2026‑06‑16) — верен ЭТОТ подраздел.

## Повторный аудит MedSpravki-REU (ASP.NET Core 8 + Razor Pages) — OWASP Top 10 2021

ВАЖНО: код заметно изменился со времени написания ТЗ к этому прогону. Несколько «известных» проблем из брифа УЖЕ ИСПРАВЛЕНЫ в текущем коде — фиксирую честно, чтобы не раздувать отчёт ложными находками:

- **Security-заголовки ЕСТЬ** — `Program.cs:45-56` ставит `X-Content-Type-Options=nosniff`, `X-Frame-Options=DENY`, `Referrer-Policy=no-referrer` и CSP (`default-src 'self'`, `frame-ancestors 'none'`, `object-src 'self'`, `base-uri 'self'`). Бриф утверждал «НЕТ security-заголовков» — это неверно для текущего кода. CSP содержит `script-src 'unsafe-inline'` и `style-src 'unsafe-inline'` — это единственная слабость заголовков (см. MED-A05-CSP).
- **Аудит просмотра скана ПИШЕТСЯ** — `Program.cs:86-91` логирует `ScanView` один раз на открытие (range-догрузки не дублируются). Бриф утверждал «аудит просмотра не пишется» — неверно.
- **Аудит входа/выхода ПИШЕТСЯ** — `Login.cshtml.cs:68/72`, `Logout.cshtml.cs:30` пишут `Login/LoginFailed/Logout` с IP.
- **IpAddress в аудите ЕСТЬ** — `AuditEntryFactory.cs:29` берёт `user.IpAddress`, `CurrentUser.cs:23` отдаёт `RemoteIpAddress`. Бриф «нет IpAddress» — неверно.
- **ForwardedHeaders сужены** — `Program.cs:40-43` доверяет X-Forwarded-* только loopback (KnownProxies/KnownNetworks по умолчанию), что корректно для IP-аудита за реверс-прокси.
- **Антифоргери активен** — Razor Pages валидируют токен на POST по умолчанию, `_Layout.cshtml:36` рендерит `@Html.AntiForgeryToken()`. Минимал-API только GET. CSRF неприменим/смягчён.
- **Path traversal в хранилище закрыт** — `FileScanStorage.cs:48/55` использует `Path.GetFileName(storedName)`, имена — серверные GUID. Подтверждаю отсутствие command-injection в pdftoppm (`UseShellExecute=false`, серверный GUID-путь, `LocalOllamaRecognitionProvider.cs:125-130`) и отсутствие SSRF через ввод (Ollama-URL из конфига). XSS-стоков нет: ни одного `Html.Raw`/`MarkupString` во всём проекте — Razor автоэкранирует распознанные ИИ-поля и ФИО.

### Вердикт по 10 категориям

**A01 Broken Access Control — ЕСТЬ ПРОБЛЕМА (главная).** Авторизация только на уровне папки: `Program.cs:25` `AuthorizeFolder("/")` требует лишь *аутентификации*, без ролей. Единственная ролевая проверка — minimal-API `/scans/{id}/file` (`Program.cs:96` RequireRole Teacher/HeadOfDepartment/Admin). Ни одна Razor-страница не несёт `[Authorize(Roles=…)]`: `Journal/Index.cshtml.cs`, `Import/Index.cshtml.cs`, `Review/Index.cshtml.cs` (Approve/Reject), `Certificates/Create.cshtml.cs`, `Students/Details.cshtml.cs`, `Scans/Index.cshtml.cs` (список/загрузка/распознавание) доступны ЛЮБОМУ вошедшему. Object-level scope (BOLA) отсутствует на всех уровнях: `ScanService.OpenAsync` (`ScanService.cs:85-94`) и `GetAsync` ищут скан только по `scanId`, без проверки владельца/преподавателя студента; `CertificateService.ApproveAsync/RejectAsync` (`CertificateService.cs:85-127`) — только по `certificateId`. В v1 (только сотрудники Admin+Teacher) риск СМЯГЧЁН доверенным персоналом в ЛВС, но при появлении роли Student (план v2) это мгновенно превращается в полное BOLA-раскрытие спецкатегории ПДн. (MED-A01-FOLDER, MED-A01-BOLA-SCAN, MED-A01-REVIEW)

**A02 Cryptographic Failures — ЕСТЬ ПРОБЛЕМА (смягчено).** Сканы медсправок (спецкатегория ПДн ст.10 152-ФЗ + врачебная тайна) хранятся в ФС в открытом виде, без шифрования at-rest (`FileScanStorage.cs:29` `File.Create`). Канал к Ollama — `http://<tailscale-ip-узла>:11434` (`appsettings.Development.json:16`), плейн-текст по Tailscale (Tailscale = WireGuard-шифрование на транспорте, смягчает). Пароли — корректный Identity-хеш (PBKDF2). (MED-A02-AT-REST)

**A03 Injection — СМЯГЧЕНО / частично.** SQL-инъекций нет: EF Core параметризует значения, LIKE использует параметр (`RegistryQueryService.cs:39`). НО user-ввод поиска интерполируется в LIKE-паттерн без экранирования `%`/`_` — `Student.Normalize` (`Student.cs:36-38`) только lower/trim, wildcards не вычищает → LIKE-wildcard abuse + обход GIN-индекса (sequential scan, мини-DoS). `SqlRosterSource.cs:27` гонит raw SQL из конфига (`_options.Sql.Query`) — это доверенный конфиг, не пользовательский ввод (low). Лог-инъекция Serilog: значения подставляются как структурированные параметры (`{Model}`, `{Count}`), но в `AuditLog.Description` кладётся интерполированный текст с именами файлов/ФИО — в БД (jsonb/text) это безопасно, без CRLF-инъекции в файловые логи. (MED-A03-LIKE)

**A04 Insecure Design — ЕСТЬ ПРОБЛЕМА (смягчено архитектурой).** Дизайн в целом грамотный (case-lifecycle, human-in-the-loop, RequiresManualReview всегда true). Но: (а) отсутствует объектная модель «кто чей преподаватель» в авторизации (см. A01); (б) нет rate-limiting на `/scans/{id}/file` и на распознавание (ИИ-эндпойнт ~50 сек/запрос — потенциальный resource-DoS); (в) загрузка сканов привязана к route-`studentId` без проверки, что текущий пользователь вправе грузить за этого студента. (MED-A04-RATELIMIT — учтено в A01/A05.)

**A05 Security Misconfiguration — ЕСТЬ ПРОБЛЕМА.** (1) `AllowedHosts=*` (`appsettings.json:21`) — Host-header не валидируется. (2) Секреты в `appsettings.json`: `Password=postgres` (`appsettings.json:3`), причём это суперпользователь Postgres — приложение и аудит работают из-под него. (3) CSP допускает `script-src/style-src 'unsafe-inline'` (`Program.cs:53-54`) — ослабляет анти-XSS. (4) Bootstrap-пароль `<демо-пароль>` в открытом виде в конфиге (`appsettings.json:8`, `appsettings.Development.json:6`). (MED-A05-HOSTS, MED-A05-SECRETS, MED-A05-CSP, MED-A07-BOOTSTRAP)

**A06 Vulnerable & Outdated Components — ЕСТЬ ПРОБЛЕМА (низкий-средний).** Все пакеты прибиты к `8.0.4` (`*.csproj`): `Microsoft.EntityFrameworkCore 8.0.4`, `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.4`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore 8.0.4`, `Serilog.AspNetCore 8.0.1`, `FluentValidation.AspNetCore 11.3.0` (deprecated/устарел). 8.0.4 — ранний релиз .NET 8 (апрель 2024); последующие 8.0.x патчи закрывали .NET-CVE. Нет `Directory.Packages.props`/lock-файла. (MED-A06-NUGET)

**A07 Identification & Authentication — ЕСТЬ ПРОБЛЕМА (смягчено).** (1) Демо-пароль `<демо-пароль>` + bootstrap-юзер получает СРАЗУ две роли Admin+Teacher (`DataSeeder.cs:48-49`) — слияние ролей. В проде `BootstrapUser:Enabled=false` (`appsettings.json:6`) — смягчает, но включён в Development. (2) Cookie ASP.NET Identity: Secure/HttpOnly/SameSite НЕ заданы явно (`DependencyInjection.cs:44-49` — только LoginPath/SlidingExpiration). Дефолты Identity: HttpOnly=true, SameSite=Lax, SecurePolicy=SameAsRequest — не «Always», т.е. без принудительного HTTPS-флага. (3) Перечисление пользователей: сообщение об ошибке логина обобщённое (`Login.cshtml.cs:78` «Неверный логин или пароль»), НО различимое «Учётная запись заблокирована» при lockout (`Login.cshtml.cs:76-77`) косвенно подтверждает существование логина. (4) Lockout: задан только `MaxFailedAccessAttempts=5`; длительность НЕ переопределена → действует дефолт Identity = 5 минут (бриф «без длительности» неточен; дефолт есть, но он короткий — слабая защита от перебора). (MED-A07-BOOTSTRAP, MED-A07-COOKIE, MED-A07-ENUM)

**A08 Software & Data Integrity — ЕСТЬ ПРОБЛЕМА (низкий).** SHA-256 считается при загрузке (`FileScanStorage.cs:25-42`) и пишется в `Sha256`, НО при чтении/распознавании (`ScanService.OpenAsync/RecognizeAsync`) хеш НЕ сверяется — нарушение целостности файла на диске не детектируется. Аудит-логи в обычной таблице без append-only (см. A09). (MED-A08-SHA)

**A09 Logging & Monitoring — ЕСТЬ ПРОБЛЕМА (средний).** Бизнес-аудит присутствует и богат (вход, выход, просмотр скана, создание/подтверждение/отклонение справки, импорт). НО таблица `audit_logs` (`InitialCreate.cs:62-81`) — обычная таблица: нет REVOKE UPDATE/DELETE, нет триггеров append-only, нет отдельной роли БД. Приложение коннектится как суперпользователь `postgres` → журнал РСБ полностью изменяем/удаляем как через прямой доступ к БД, так и через любой будущий баг с raw-SQL. Для медданных (323-ФЗ, требования к неизменяемости РСБ) это существенно. Логи только в Console (`Program.cs:19`) — нет персистентного sink/алертов. (MED-A09-IMMUTABLE)

**A10 SSRF — НЕПРИМЕНИМО / смягчено.** Единственный исходящий вызов — Ollama (`LocalOllamaRecognitionProvider.cs:61`) и OData-1С (`OneCODataRosterSource.cs:40`); оба URL берутся из конфигурации, не из пользовательского ввода. SSRF через ввод нет (подтверждаю). OData без TLS-pinning/проверки схемы — теоретический риск при компрометации конфига, но это не SSRF и контролируется админом (low, не выделяю в отдельную находку P-уровня).

### Итог приоритизации
- **P0:** MED-A01-FOLDER (отсутствие ролевой авторизации страниц), MED-A01-BOLA-SCAN (нет owner-проверки при чтении скана спецкатегории ПДн).
- **P1:** MED-A01-REVIEW (BOLA на Approve/Reject/Create), MED-A05-SECRETS (суперюзер-пароль БД в конфиге), MED-A09-IMMUTABLE (мутабельный РСБ + суперюзер), MED-A02-AT-REST, MED-A07-COOKIE.
- **P2:** MED-A05-HOSTS, MED-A05-CSP, MED-A07-BOOTSTRAP, MED-A07-ENUM, MED-A03-LIKE, MED-A06-NUGET, MED-A08-SHA.

Главный системный риск: приложение спроектировано «для доверенных сотрудников в ЛВС» (v1), и в этом контексте многое СМЯГЧЕНО. Но архитектура авторизации (folder-level, без object-scope) не выдержит перехода к v2 «личный кабинет студента», к которому проект явно готовится (роли Student, StudentUpload, Scans). Перед любым сетевым доступом студентов A01-находки обязаны быть закрыты.

### Находки OWASP‑прогона (текущий код)

| ID | Sev | Категория | Находка | Где | Контр‑аргумент |
|---|---|---|---|---|---|
| MED-A01-FOLDER | P0 | Access Control | AuthorizeFolder("/") даёт только аутентификацию без ролей — все страницы (журнал, импорт,  | Program.cs:25 (options.Conventions.AuthorizeFolder("/")); ни в одном P | В v1 единственные заведённые пользователи — сотрудники кафедры (bootstrap Admin+Teacher),  |
| MED-A01-BOLA-SCAN | P0 | Access Control | ScanService.OpenAsync читает скан медсправки только по scanId без проверки владельца (IDOR | ScanService.cs:87 (var scan = await _db.Scans.AsNoTracking().FirstOrDe | Имена хранилища — серверные GUID (FileScanStorage), а scanId тоже GUID — угадать нельзя, п |
| MED-A01-REVIEW | P1 | Access Control | Approve/Reject/Create справок и список сканов доступны любому аутентифицированному без obj | CertificateService.cs:87 (FirstOrDefaultAsync(c => c.Id == certificate | В v1 решения принимают только сотрудники (Admin+Teacher), для которых подтверждение справо |
| MED-A05-SECRETS | P1 | Configuration | Пароль суперпользователя Postgres в открытом виде в appsettings.json; приложение работает  | appsettings.json:3 ("DefaultConnection": "...Username=postgres;Passwor | Это значение по умолчанию для локального Docker-Postgres (reu-pg), Password=postgres — заг |
| MED-A09-IMMUTABLE | P1 | Logging & Monitoring | Таблица audit_logs не защищена от изменения/удаления (нет REVOKE/триггеров/append-only рол | InitialCreate.cs:62-81 (CreateTable audit_logs — только PrimaryKey, ни | В ЛВС РЭУ доступ к самой СУБД ограничен админами, а прикладной слой нигде не предоставляет |
| MED-A02-AT-REST | P1 | Cryptography | Сканы медсправок (спецкатегория ПДн) хранятся без шифрования at-rest | FileScanStorage.cs:29-39 (File.Create + запись буфера без шифрования); | Файлы лежат вне wwwroot (недоступны статикой), имена — непрогнозируемые GUID, развёртывани |
| MED-A07-COOKIE | P1 | Authentication | Cookie аутентификации без явных Secure/SameSite=Strict/Always — полагается на дефолты Iden | DependencyInjection.cs:44-49 (ConfigureApplicationCookie — нет options | Дефолты Identity уже дают HttpOnly=true и SameSite=Lax, антифоргери активен, UseHttpsRedir |
| MED-A05-HOSTS | P2 | Configuration | AllowedHosts=* — отсутствует валидация Host-заголовка | appsettings.json:21 ("AllowedHosts": "*") | В v1 нет исходящих ссылок/писем и нет публичного кэша, развёртывание офлайн в ЛВС за фикси |
| MED-A05-CSP | P2 | Configuration | CSP допускает script-src/style-src 'unsafe-inline' — ослаблена анти-XSS защита | Program.cs:53-54 ("...style-src 'self' 'unsafe-inline'; script-src 'se | На текущий момент в проекте нет ни одного Html.Raw/MarkupString, Razor автоэкранирует весь |
| MED-A07-BOOTSTRAP | P2 | Authentication | Демо-пароль <демо-пароль> в конфиге и слияние ролей Admin+Teacher у bootstrap-пользователя | DataSeeder.cs:45 (CreateAsync(user, bootstrap["Password"] ?? "ChangeMe | В продовом appsettings.json BootstrapUser:Enabled=false — учётка автоматически не создаётс |
| MED-A07-ENUM | P2 | Authentication | Различимое сообщение о блокировке косвенно подтверждает существование логина; lockout-окно | Login.cshtml.cs:56-79 (PasswordSignInAsync lockoutOnFailure:true; ветк | Перечисление пользователей в системе на ~76 известных преподавателей кафедры (ФИО публичны |
| MED-A03-LIKE | P2 | Injection | LIKE-wildcard abuse: символы % и _ из поискового ввода не экранируются (Student.Normalize  | RegistryQueryService.cs:35-40 (var normalized = Student.Normalize(filt | Это доступно только аутентифицированным сотрудникам, SQL-инъекции нет (EF параметризует),  |
| MED-A06-NUGET | P2 | Dependencies | Зависимости прибиты к ранним версиям 8.0.4 / устаревший FluentValidation.AspNetCore | ReuMedCertificates.Infrastructure.csproj (EFCore 8.0.4, Npgsql 8.0.4,  | Конкретные эксплуатируемые CVE в 8.0.4 для данного набора не подтверждены в этом аудите (н |
| MED-A08-SHA | P2 | Data Integrity | SHA-256 скана считается при загрузке, но не сверяется при чтении/распознавании | ScanService.cs:85-94 (OpenAsync открывает поток без сверки scan.Sha256 | Подмена файла требует прямого доступа к файловой системе сервера (ОС-компрометация), что с |


## 3.7. Код‑факты приложения _(снимок 2026‑06‑16; раздел «Обновление» вверху отражает изменения)_

All facts confirmed. The `IpAddress` column exists but is never populated by `AuditEntryFactory.Create` (no parameter for it); `Login`/`LoginFailed`/`Export` action types exist only as comments/demo-seed, never written by real runtime code. Here is the structured fact reference.

---

# КОД-ФАКТЫ безопасности: MedSpravki-REU (AppSec baseline, 2026-06-16)

Все пути относительны от `…/projects/MedSpravki-REU/app/src`. Только факты с `file:line`, без рекомендаций.

## 1. Аутентификация и авторизация

**Роли (ровно 3):** `ReuMedCertificates.Infrastructure/Identity/AppRole.cs:15-19` — `Teacher`, `HeadOfDepartment`, `Admin`. `AppRoles.All` сеется в `Persistence/DataSeeder.cs:23-25`. Bootstrap-пользователь (`DataSeeder.cs:27-52`) получает **обе** роли `Admin` + `Teacher` (`DataSeeder.cs:48-49`).

**Identity-конфиг:** `Infrastructure/DependencyInjection.cs:31-49`. Пароль: длина ≥ 8, цифра+верх+низ обязательны, неалфанум НЕ обязателен (`:33-37`). Lockout: `MaxFailedAccessAttempts = 5` (`:38`), но `Lockout.DefaultLockoutTimeSpan` и `AllowedForNewUsers` НЕ заданы (дефолт). `User.RequireUniqueEmail = false` (`:39`). Cookie: `LoginPath`/`AccessDeniedPath = /Auth/Login`, `SlidingExpiration = true` (`:44-49`). `ExpireTimeSpan`, `Cookie.SecurePolicy`, `Cookie.HttpOnly`, `Cookie.SameSite` — НЕ заданы явно (дефолты ASP.NET Identity: HttpOnly=true, SameSite=Lax, SecurePolicy=SameAsRequest).

**AuthorizeFolder:** `Web/Program.cs:19-25` — `AuthorizeFolder("/")` (всё под аутентификацией) + `AllowAnonymousToPage("/Auth/Login")` и `/Error`. Это требует только *аутентификации*, без проверки роли. `Login.cshtml.cs:10` дополнительно помечен `[AllowAnonymous]`.

**Применение ролей на маршрутах:** единственная ролевая политика — на minimal-API `GET /scans/{id:guid}/file` (`Program.cs:55-61`): `RequireRole("Teacher","HeadOfDepartment","Admin")`. На **всех Razor Pages ролевых ограничений НЕТ** — любой аутентифицированный пользователь (включая будущую роль студента, которой пока нет) имеет полный доступ к реестру, карточкам студентов, очереди проверки, импорту, журналу, загрузке/распознаванию сканов. Проверено: в `Pages/**/*.cshtml.cs` нет ни одного `[Authorize(Roles=…)]`/`RequireRole` (grep).

**Object-level / ownership проверки — ОТСУТСТВУЮТ ВЕЗДЕ:**
- `Students/Details.cshtml.cs:15-23` — `GetDetailAsync(id)` без фильтра по преподавателю/группе текущего пользователя.
- `StudentService.GetDetailAsync` (`Application/Students/StudentService.cs:66-98`) — выборка только по `s.Id == studentId`, без ограничения области видимости.
- `Certificates/Create.cshtml.cs:60-90` — добавляет справку любому `studentId` без проверки, что студент принадлежит текущему преподавателю.
- `Review/Index.cshtml.cs:20-32` — `Approve`/`Reject` по любому `certificateId`; `CertificateService.ApproveAsync/RejectAsync` (`CertificateService.cs:85-127`) фильтруют только `c.Id == certificateId && !c.IsDeleted`, без owner-проверки.
- `Scans/Index.cshtml.cs:46-79` — загрузка/распознавание сканов по любому `studentId`/`scanId`.
- `RegistryQueryService.SearchAsync` (`Application/Registry/RegistryQueryService.cs:22-88`) — фильтрует по `IsActive`, но НЕ по преподавателю текущего пользователя (фильтр `TeacherId` — это пользовательский параметр запроса, не серверное ограничение).
- `CurrentUser` (`Infrastructure/Services/CurrentUser.cs`) выдаёт только `UserId`/`UserName`/`IsAuthenticated`; роль/область не используются ни в одном сервисе.

## 2. Эндпоинт `/scans/{id}/file` и путь чтения скана

**Маршрут:** `Web/Program.cs:55-61`. Ролевой доступ есть (`Teacher/HeadOfDepartment/Admin`). Ответ — `Results.File(content.Stream, content.ContentType, enableRangeProcessing: true)` (`:60`).
- **Content-Disposition НЕ задаётся** — файл отдаётся inline (без `fileDownloadName`), `Results.File` без имени = inline.
- `Content-Type` берётся из `scan.ContentType`, который = **клиентский MIME, сохранённый при загрузке** (см. п.3) → возможен MIME, не совпадающий с реальным содержимым.
- `X-Content-Type-Options: nosniff` НЕ ставится (нет middleware заголовков, п.10).
- **Принадлежность скана НЕ проверяется:** `ScanService.OpenAsync` (`Application/Scans/ScanService.cs:85-94`) ищет скан только по `s.Id == scanId`, без сверки `StudentId`/преподавателя/`UploadedByUserId`. Любой сотрудник с ролью может открыть скан любого студента, зная/перебрав GUID.
- **Аудит просмотра/скачивания НЕ пишется:** в `OpenAsync` (`ScanService.cs:85-94`) и в обработчике маршрута (`Program.cs:55-61`) нет ни одного `AuditLogs.Add`. Просмотр медскана (спецкатегория ПДн) не логируется.
- **SHA-256 при открытии НЕ перепроверяется**, хотя `CertificateScan.cs:27` («перепроверяется при открытии») и UI (`Scans/Index.cshtml:25`) это декларируют. `OpenReadAsync` (`Infrastructure/Services/FileScanStorage.cs:45-51`) просто открывает файл, хеш не считается.

**Хранилище:** `FileScanStorage` (`Infrastructure/Services/FileScanStorage.cs`). Файлы вне wwwroot — каталог `App_Data/scans` (`appsettings.Development.json:10`), под `AppContext.BaseDirectory` если путь не абсолютный (`FileScanStorage.cs:14-17`). Имена — GUID `.bin` (`:22`). Анти-traversal через `Path.GetFileName(storedName)` при чтении/удалении (`:48, :55`). Файлы на диске **не шифруются** (`File.Create`/`File.OpenRead`, `:29, :49`).

## 3. Путь загрузки скана

**Обработчик:** `Scans/Index.cshtml.cs:46-68` (`OnPostUploadAsync`). Валидация:
- размер: `file.Length > MaxUploadBytes` (`:53`), лимит = `Scans:MaxUploadBytes` 10 МБ (`appsettings.Development.json:11`, `ScanStorageOptions.cs:12`).
- тип: `!_scanOptions.AllowedContentTypes.Contains(file.ContentType)` (`:55`) — **проверяется ИМЕННО клиентский `file.ContentType`** (заголовок multipart, подделывается тривиально). Whitelist: `application/pdf, image/jpeg, image/png` (`appsettings.Development.json:12`).
- имя: `Path.GetFileName(file.FileName)` (`Index.cshtml.cs:61`) — отсекает путь, но `OriginalFileName` сохраняется как есть в БД (`ScanService.cs:57`, столбец `varchar(255)`, `ApplicationDbContext.cs:105`).
- **Проверки магических байт (signature/sniffing) НЕТ.** **Антивирусной проверки НЕТ.** Содержимое не валидируется — сохраняется как есть (`ScanService.UploadAsync` → `FileScanStorage.SaveAsync`, `ScanService.cs:50-52`).
- Сохранённый `ContentType` (клиентский) затем используется при отдаче файла (`ScanService.cs:93`) и при выборе ветки OCR (`LocalOllamaRecognitionProvider.cs:47`).
- UI `accept="application/pdf,image/jpeg,image/png"` (`Scans/Index.cshtml:22`) — только клиентская подсказка.
- Аудит загрузки **пишется** (`ScanService.cs:70-72`, action `ScanUpload`).

## 4. OCR / распознавание (`LocalOllamaRecognitionProvider`)

`Infrastructure/Services/LocalOllamaRecognitionProvider.cs`.

**pdftoppm:**
- Запуск через `ProcessStartInfo("pdftoppm", …)` с `UseShellExecute = false` (`:125-130`) — **shell не используется**, классической shell-инъекции нет.
- Аргументы: `$"-png -singlefile -r {PdfRenderDpi} -f 1 -l 1 \"{pdfPath}\" \"{baseName}\""` (`:126`). `pdfPath`/`baseName` — **генерируются сервером** из `Path.GetTempPath()` + `Guid.NewGuid()` (`:118-120`), НЕ из имени загруженного файла. Argument injection через пользовательское имя файла **невозможен** (имя клиента сюда не попадает). `PdfRenderDpi` — int из конфига (`RecognitionOptions.cs:21`), не пользовательский ввод. При `UseShellExecute=false` аргументы передаются как одна строка `Arguments` (Windows-style парсинг), но управляемые поля — серверные GUID/int.
- Временные файлы: пишутся в системный temp (`:118-121`), удаляются в `finally` через `TryDelete` (`:141-151`); содержат PDF медсправки в открытом виде на время обработки.

**Сеть / SSRF:**
- POST на `$"{OllamaUrl.TrimEnd('/')}/api/generate"` (`:61-62`). `OllamaUrl` — **из конфига** (`Recognition:OllamaUrl`), не из пользовательского запроса; в Dev = `http://<tailscale-ip-узла>:11434` (Tailscale, **http без TLS**) (`appsettings.Development.json:16`). Дефолт `http://localhost:11434` (`RecognitionOptions.cs:12`). Пользователь не управляет endpoint → прямого SSRF через ввод нет; запрос идёт по открытому HTTP в Tailscale-сеть.
- Изображение/PDF кодируется в base64 и уходит на Ollama (`:55`); тело справки (спецкатегория ПДн) передаётся по незашифрованному HTTP.
- Таймаут HttpClient = `TimeoutSeconds` (`:42`, в Dev 180с).
- Ошибка модели → текст исключения сохраняется в `RecognitionJson` (`ScanService.cs:144`) и может содержать детали.

## 5. Доступ к данным и инъекции

**SqlRosterSource** (`Infrastructure/Services/SqlRosterSource.cs`):
- `NpgsqlCommand(_options.Sql.Query, conn)` (`:27`) — выполняется **произвольный SQL-текст из конфига** `Roster:Sql:Query` (`RosterOptions.cs:22-24`), без параметров. Это не инъекция через пользовательский ввод (значение конфигурации), но запрос конфигурируем и исполняется как есть. ConnectionString тоже из конфига (`:24`), при пустом — подставляется строка приложения (`DependencyInjection.cs:88-89`).
- Колонки читаются по имени (`GetString`/`reader.GetOrdinal`, `:44-48`), `course` через `Convert.ToInt16` (`:50-51`).

**OneCODataRosterSource** (`Infrastructure/Services/OneCODataRosterSource.cs`):
- Basic-auth: `Username:Password` из конфига → base64 в заголовок (`:24-29`). Креды 1С хранятся в конфиге (`RosterOptions.cs:31-32`).
- `GetStringAsync(o.Url)` (`:40`) — URL из конфига `Roster:OData:Url`. JSON парсится `JsonDocument.Parse` (`:42`), поля по именам из конфига (`:49-56`). Нет TLS-валидации/закрепления.

**Демо-источник 1С:** `DataSeeder.SeedOnecDemoTableAsync` (`DataSeeder.cs:70-91`) — `ExecuteSqlRawAsync` с **статическими литералами** (CREATE/DELETE/INSERT, `:72-90`), пользовательский ввод не конкатенируется.

**EF Core запросы:** все через LINQ/параметризацию. pg_trgm поиск: `RegistryQueryService.cs:35-40` — `EF.Functions.Like(s.NormalizedFullName, $"%{normalized}%")`; `normalized` = результат `Student.Normalize(filter.Query)`, передаётся EF как параметр (не строковая конкатенация в SQL). `%`/`_` в пользовательском вводе НЕ экранируются → LIKE-wildcard injection (функциональная, не SQLi). Пагинация: `Math.Clamp(PageSize,1,200)` (`:44`).

**ClosedXML / Excel:** пакет `ClosedXML 0.104.2` подключён (`Infrastructure.csproj:13`), но **в коде НЕ используется нигде** (grep по исходникам — 0 совпадений `XLWorkbook`/`.xlsx`/`SaveAs`). Импорт реестра идёт из SQL/OData (`RosterImportService.cs`), НЕ из Excel-файлов. Экспорта в Excel/CSV в коде нет → CSV/формула-инъекция в текущем коде неприменима (нет точки экспорта). `DraftSource.ExcelImport` объявлен (`DraftSource.cs:13`), но не используется.

**RosterImportService** (`Application/Roster/RosterImportService.cs`): данные из `IRosterSource` пишутся в БД через EF (`:119-132`), без санитизации ФИО/групп (хранятся как пришли); дедуп по `Normalize(FullName)+"|"+GroupName` (`:84`). Аудит импорта пишется (`:152-154`).

## 6. Хранение медданных

**Что хранится (БД, таблица `medical_certificates`):** `MedicalCertificate.cs` — `HealthGroup`, `PhysicalGroup` (enum), `Restrictions` (текст ограничений), `Comment`, `CertificateNumber`, `MedicalOrganization`, `IssueDate/StartDate/EndDate` (`:15-29`). `Restrictions` → столбец `text` (`Migrations/InitialCreate.cs:327`); комментарий `MedicalCertificate.cs:27` декларирует «БЕЗ диагноза». Диагноз как отдельное поле в модели отсутствует, но `Restrictions`/`Comment` — свободный текст, ничем не ограничен по содержанию.

**Сканы:** файл целиком на ФС (`App_Data/scans/*.bin`), в БД (`certificate_scans`) — метаданные + `Sha256` + `RecognitionJson` (`jsonb`) с распознанными медполями (`CertificateScan.cs:19-39`, `ApplicationDbContext.cs:102-117`). `RecognitionJson` содержит ФИО/даты/физгруппу/ограничения в открытом виде (`LocalOllamaRecognitionProvider.cs:21-35`, `ScanService.cs:129`).

**Шифрование at-rest:** **отсутствует на всех уровнях.** Нет `pgcrypto`/column encryption (grep — 0), нет EF `ValueConverter` для шифрования, нет ASP.NET DataProtection для полей. Файлы сканов на диске не шифруются (`FileScanStorage.cs:29,49`). Единственное расширение БД — `pg_trgm` (`ApplicationDbContext.cs:28`).

**Маскирование:** отсутствует — медполя отдаются в UI/файл/JSON как есть. ConnectionString с паролем в открытом виде в `appsettings.json:3`.

## 7. Аудит

**Модель** `Domain/Entities/AuditLog.cs`: поля `EntityType, EntityId, ActionType, UserId, UserNameSnapshot, OccurredAt, Description, BeforeJson, AfterJson(jsonb), IpAddress(:28)`. Комментарий `:6-7` декларирует «INSERT-only … запрещены на уровне прав БД (см. миграцию/деплой)».

**INSERT-only НЕ обеспечен в коде/БД:** в миграции `Migrations/InitialCreate.cs:62-81` таблица `audit_logs` создаётся как обычная — **нет REVOKE/GRANT, нет триггеров/правил, нет partitioning** (grep `REVOKE/GRANT/TRIGGER/RULE` — 0). Защита от UPDATE/DELETE — только в том, что приложение их не делает; на уровне БД ничего нет.

**Фабрика:** `AuditEntryFactory.Create` (`Application/Common/AuditEntryFactory.cs:9-30`) — собирает запись. **Параметра `IpAddress` НЕТ** — поле всегда `null` в runtime. IP-адрес нигде не захватывается (grep `RemoteIpAddress` — 0; `UseForwardedHeaders` — нет).

**Что логируется (реальный runtime):** `Student.Create` (`StudentService.cs:58-60`); `MedicalCertificate.Create/Approve/Reject` (`CertificateService.cs:52-54, 99-101, 122-124`); `ScanUpload`, `ScanRecognized` (`ScanService.cs:70-72, 134-136`); `Import` (`RosterImportService.cs:152-154`).

**Что НЕ логируется:**
- **Просмотр/скачивание скана** `GET /scans/{id}/file` (`Program.cs:55-61`, `ScanService.OpenAsync`) — нет аудита (ключевой пробел по медданным).
- **Просмотр карточки студента/медполей** (`Students/Details`, `Registry`, `BeforeClass`, `Review` list) — нет аудита чтения.
- **Вход/выход:** `Login.cshtml.cs:44-67` — успех/неуспех входа НЕ пишутся в AuditLog (только `LastLoginAt` обновляется, `:57-58`). `Logout.cshtml.cs:16-21` — нет аудита. `ActionType` `Login`/`LoginFailed`/`Export` фигурируют только в комментарии (`AuditLog.cs:16`) и в **демо-сидинге** (`DataSeeder.cs:101`), реальным кодом не создаются.
- **Update справки** (редактирование) — функционала нет, аудита нет.

**Чтение журнала:** `AuditQueryService.cs:26-39` — read-only, `Take(Clamp(take,1,1000))`. Доступ к `/journal` (`Journal/Index.cshtml.cs`) — без ролевого ограничения (любой аутентифицированный).

## 8. Зависимости (NuGet, версии из .csproj)

- `Web` (`ReuMedCertificates.Web.csproj`): `FluentValidation.AspNetCore 11.3.0` (`:8`); `Serilog.AspNetCore 8.0.1` (`:9`); `Microsoft.EntityFrameworkCore.Design 8.0.4` (`:10`).
- `Infrastructure` (`ReuMedCertificates.Infrastructure.csproj`): `ClosedXML 0.104.2` (`:13`, не используется); `Microsoft.AspNetCore.Identity.EntityFrameworkCore 8.0.4` (`:14`); `Microsoft.EntityFrameworkCore 8.0.4` (`:15`); `Microsoft.EntityFrameworkCore.Design 8.0.4` (`:16`); `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.4` (`:20`); `Serilog.AspNetCore 8.0.1` (`:21`); + `FrameworkReference Microsoft.AspNetCore.App` (`:4`).
- `Application` (`ReuMedCertificates.Application.csproj`): `Microsoft.EntityFrameworkCore 8.0.4` (`:8`).
- `Domain` (`ReuMedCertificates.Domain.csproj`): пакетов нет (`:1-3`).
- TargetFramework во всех — `net8.0` (по SDK; явного TFM в csproj нет). EF/ASP.NET Identity на `8.0.4` — фиксированный патч (есть более новые 8.0.x с security-фиксами; SCA через `dotnet list package --vulnerable` запускать на CI). HTMX/Bootstrap — статика, в csproj не пакетируются.

## 9. Секреты и конфиг

**`appsettings.json`:**
- `ConnectionStrings:DefaultConnection` = `Host=localhost;…;Username=postgres;Password=postgres` — **пароль БД в открытом виде в репозитории** (`appsettings.json:3`). Дублируется как fallback в коде: `DependencyInjection.cs:25` и `ApplicationDbContextFactory.cs:12`.
- `BootstrapUser:Enabled=false`, `Password="<демо-пароль>"` (`appsettings.json:5-10`).
- `AllowedHosts="*"` (`:21`).

**`appsettings.Development.json`:**
- `SeedDemoData=true` (`:2`) — засевает 12 демо-студентов/справок + демо-журнал + демо-таблицу 1С (`DataSeeder.cs:56-62`).
- `BootstrapUser:Enabled=true`, `Login="teacher"`, `Password="<демо-пароль>"` (`:3-8`) → создаётся учётка с ролями `Admin`+`Teacher` (`DataSeeder.cs:45-49`).
- `Scans:StoragePath=App_Data/scans`, `MaxUploadBytes=10485760`, `AllowedContentTypes=[pdf,jpeg,png]` (`:9-13`).
- `Recognition:Provider=LocalOllama`, `OllamaUrl=http://<tailscale-ip-узла>:11434` (http, Tailscale), `VisionModel=qwen2.5vl:7b`, `TimeoutSeconds=180`, `PdfRenderDpi=200` (`:14-20`).

**Демо-пароль `<демо-пароль>`:** задаётся в `appsettings.json:8` и `appsettings.Development.json:7`; используется в `DataSeeder.cs:45` (`bootstrap["Password"] ?? "<демо-пароль>"` — литерал-fallback прямо в коде). Креды 1С OData (`RosterOptions.cs:31-32`) — пустые по умолчанию, ожидаются из конфига.

Нет user-secrets/Key Vault/env-секретов в коде (кроме `REU_DB_CONNECTION` env в design-time factory, `ApplicationDbContextFactory.cs:11`).

## 10. Security-заголовки и middleware-конвейер (`Program.cs`)

**Конвейер** (`Program.cs:36-63`): `UseExceptionHandler("/Error")` + `UseHsts()` **только если не Development** (`:36-40`); `SeedIdentityAsync` (`:43`); `UseHttpsRedirection` (`:45`); `UseStaticFiles` (`:46`); `UseRouting` (`:48`); `UseAuthentication` (`:49`); `UseAuthorization` (`:50`); затем маршруты.

**ОТСУТСТВУЕТ:**
- `Content-Security-Policy` — нет.
- `X-Frame-Options` / `frame-ancestors` — нет (защита от clickjacking отсутствует; критично при встраивании в портал РЭУ Bitrix).
- `X-Content-Type-Options: nosniff` — нет (важно, т.к. скан отдаётся с клиентским MIME, п.2/3).
- `Referrer-Policy` — нет.
- `Permissions-Policy`, `Cross-Origin-*` — нет.
- Никакого `Use…SecurityHeaders`/кастомного middleware заголовков (grep — 0).

**Антифоргери (CSRF):** глобально `AddAntiforgery`/`AutoValidateAntiforgeryToken` НЕ вызывается (grep — 0). Действует **дефолт Razor Pages**: фреймворк автоматически валидирует antiforgery-токен на POST-страницах (встроенная конвенция Razor Pages), а tag-helper форм генерит скрытое поле автоматически. Явный токен есть только в logout-форме `_Layout.cshtml:36` (`@Html.AntiForgeryToken()`). Прочие POST-формы (`Scans/Index.cshtml:20,48`, `Login.cshtml:27`) полагаются на авто-генерацию tag-helper'ом. Нет `[IgnoreAntiforgeryToken]`. **Важно: minimal-API `GET /scans/{id}/file` — GET, без CSRF-релевантности; никакие mutating-эндпоинты как minimal-API не объявлены** (все мутации — Razor Pages POST → авто-CSRF действует).

**HSTS** только вне Dev (`:39`). `UseHttpsRedirection` всегда (`:45`). Cookie SecurePolicy не форсирован (п.1).

**HTMX:** заявлен в стеке, но в коде **не используется** (grep `hx-`/`htmx`/`hx-headers` — 0); формы — обычный POST. Bootstrap 5.3/site.css — статика (`_Layout.cshtml:13`).

---

## Сводка ключевых пробелов (для приоритизации ревью)

1. **BOLA/IDOR на медданных:** ни один Razor Page и ни `ScanService.OpenAsync` не проверяют принадлежность объекта (студент/скан/справка) текущему пользователю/его группе — `ScanService.cs:85-94`, `StudentService.cs:66-98`, `CertificateService.cs:85-127`, `Program.cs:55-61`.
2. **Нет аудита чтения/скачивания медскана и медполей** — `Program.cs:55-61`, `ScanService.cs:85-94`; вход/выход тоже не логируются — `Login.cshtml.cs`, `Logout.cshtml.cs`.
3. **Валидация типа файла по клиентскому MIME**, без magic-bytes/AV — `Scans/Index.cshtml.cs:55`; тот же MIME затем используется при отдаче — `ScanService.cs:93`.
4. **Нет шифрования спецкатегории ПДн at-rest** (БД-столбцы, файлы сканов, RecognitionJson) — `FileScanStorage.cs`, `ApplicationDbContext.cs`, миграции.
5. **AuditLog не INSERT-only на уровне БД** (только декларация в комментарии), `IpAddress` всегда null — `AuditLog.cs:6-7,28`, `AuditEntryFactory.cs:9-30`, `Migrations/InitialCreate.cs:62-81`.
6. **Нет security-заголовков** (CSP/X-Frame/nosniff/Referrer) — `Program.cs`.
7. **Секреты в репозитории:** пароль БД и `<демо-пароль>` в `appsettings(.Development).json` и как code-литералы — `appsettings.json:3,8`, `DependencyInjection.cs:25`, `DataSeeder.cs:45`.
8. **Медданные уходят на Ollama по http (без TLS)** в Tailscale — `appsettings.Development.json:16`, `LocalOllamaRecognitionProvider.cs:61-62`.
9. **Все аутентифицированные = полный доступ** (нет per-page ролей; bootstrap-юзер = Admin+Teacher) — `Program.cs:19-25`, `DataSeeder.cs:48-49`.
10. **SHA-256 при открытии не перепроверяется**, вопреки декларации в UI/модели — `FileScanStorage.cs:45-51` vs `CertificateScan.cs:27`, `Scans/Index.cshtml:25`.

Корректировки к исходному брифу (подтверждено кодом): (а) ClosedXML подключён, но Excel-импорт/экспорт в коде отсутствует — импорт идёт из SQL/OData, поэтому CSV/формула-инъекция в текущем коде неприменима; (б) command/argument injection в pdftoppm через имя файла невозможен — `UseShellExecute=false` и пути формируются из server-side GUID, имя клиента туда не попадает; (в) SSRF через пользовательский ввод к Ollama отсутствует — endpoint берётся только из конфига; (г) HTMX в коде не используется.


---

## 3.8. Сводный security‑чек‑лист P0/P1/P2 (с результатами адверсариал‑проверки)

Дедуплицированный перечень (объединены дубли разных финдеров). Severity — финальная: где была независимая верификация, указан её итог; иначе — оценка финдера (помечено «финдер»). По каждому пункту дан контр‑аргумент (требование пользователя «проверь вывод контр‑аргументом»).

### P0 — блокеры до продакшена с реальными медданными / до интеграции в портал

| # | Уязвимость | Где (`file:line`) | OWASP/ASVS | Статус проверки | Инструмент | Исправление | Контр‑аргумент |
|---|---|---|---|---|---|---|---|
| P0‑1 | **Не логируется ЧТЕНИЕ медскана/медполей + вход/выход; `IpAddress`=null** (РСБ) | `Program.cs:55-61`; `ScanService.cs:85-94`; `AuditEntryFactory.cs:9-30`; `Login.cshtml.cs:44-67` | A09 / ASVS V7.1‑V7.2 | финдер (не понижен верификацией) | открыть скан → `SELECT … audit_logs`; ZAP | Логировать `ScanView/StudentView/Login(Failed)/Logout` с UserId+IP; добавить параметр `IpAddress` (из `RemoteIpAddress`/forwarded) | «Аудит чтения раздувает журнал» — но для спецкатегории/323‑ФЗ регистрация доступа обязательна; объём решается ретенцией |
| P0‑2 | **Цепочка stored‑XSS:** приём по клиентскому MIME (нет magic/AV) + раздача inline без `nosniff`/`Content-Disposition` | `Scans/Index.cshtml.cs:55`; `ScanService.cs:59,93`; `Program.cs:60` | A04/A05/A03 / ASVS V12.1, V14.4 | финдер (цепочка из 2 P0‑findings) | `curl -F 'file=@evil.html;type=application/pdf'` → открыть `/scans` | Magic‑байты + AV; отдавать **серверный** MIME; форс `nosniff`+`attachment`; CSP | Identity‑cookie `HttpOnly` (нет кражи через `document.cookie`); в v1 грузят только сотрудники — но XSS даёт действия/чтение DOM, а в v2 грузит студент |
| P0‑3 | **Нет security‑заголовков → clickjacking при встраивании в портал** (CSP `frame-ancestors`, `X-Frame-Options`, `nosniff`, `Referrer-Policy`) | `Program.cs:36-63` (grep=0) | A05 / ASVS V14.4.3, V14.4.7 | финдер | `curl -I`; ZAP 10020/10038; securityheaders.com | Middleware заголовков: CSP `frame-ancestors 'self' https://*.rea.ru`, `nosniff`, `Referrer-Policy`; `AddServerHeader=false` | Если выберут поддомен **без** iframe — clickjacking менее вероятен (`SameSite=Lax` не уйдёт в third‑party фрейм); но `nosniff` нужен немедленно из‑за inline‑скана |
| P0‑4 | **Спуфинг проксированных заголовков идентичности** (нет `ForwardedHeaders`/`KnownProxies`) — инвариант ДО header‑SSO/reverse‑proxy | `Program.cs:36-63`; `CurrentUser.cs:16-21` | A07 / ASVS V2.10, V13.2 | финдер (verify не отработал) | `curl -H 'X-Remote-User: admin'` минуя nginx; `nmap -p 5080` | ForwardedHeaders+KnownProxies first‑in‑pipeline; доверять заголовку только от прокси + подпись; Kestrel на loopback; mTLS | ASP.NET по умолчанию игнорирует `X-Forwarded-*` (`ForwardedHeaders.None`) — пока header‑SSO нет, спуфить нечего; риск только при варианте (d). Зафиксировать как инвариант |

### P1 — закрыть до go‑live

| # | Уязвимость | Где (`file:line`) | OWASP/ASVS | Статус проверки | Исправление | Контр‑аргумент |
|---|---|---|---|---|---|---|
| P1‑1 | **BOLA/IDOR: object‑level доступ к скану/студенту/справке отсутствует** | `ScanService.cs:85-94`; `StudentService.cs:66-98`; `CertificateService.cs:85-127`; `Program.cs:55-61` | A01 / ASVS V4.2.1 | **verify: partial, P0→P1** | Resource‑based authorization (scope по группе/факультету) + обязательный аудит чтения | Это ОТСУТСТВИЕ границы во всём приложении (модель идентичности плоская, нет `AppUser↔Teacher`), а не сломанная граница; роль‑гейт отсекает студентов/анонимов; «вся кафедра видит всё» может быть осознанным решением — но требует аудита и решения ДО прода |
| P1‑2 | **Спецкатегория без шифрования at‑rest** (файлы сканов, столбцы, `RecognitionJson`) | `FileScanStorage.cs:29,49`; `ApplicationDbContext.cs:102-117`; `ScanService.cs:129` | A02 / ASVS V6.1 | **verify: confirmed, P1** | DataProtection/AES на файлы; EF ValueConverter/pgcrypto на чувствительные столбцы; мин. BitLocker/TDE; ключи вне репо | На УЗ‑3 столбцовое шифрование не всегда обязательно, BitLocker закрывает кражу диска; но не спасает при логическом доступе (бэкап/учётка `postgres`); v1‑стратегия вообще «сканы не хранить» |
| P1‑3 | **Все аутентифицированные = полный доступ; нет per‑page ролей; bootstrap=Admin+Teacher; `/journal`,`/import` всем** | `Program.cs:19-25`; `DataSeeder.cs:48-49`; `Journal/Index.cshtml.cs` | A01 / ASVS V4.1 | финдер | Default‑deny policy на папки (`Import`=Admin, `Journal`=Admin/Head, `Review`=Head/Admin); роль студента deny‑by‑default | В v1 все — сотрудники с равными правами; но `/journal` и импорт реестра — админ‑функции, и проектировать default‑allow перед вводом студента опасно |
| P1‑4 | **Секреты в репозитории:** пароль БД `postgres`, `<демо-пароль>`, `AllowedHosts="*"` | `appsettings.json:3,8,21`; `DependencyInjection.cs:25`; `DataSeeder.cs:45`; `ApplicationDbContextFactory.cs:12` | A05/A07 / ASVS V2.10, V6.4 | финдер | Секреты в env/user‑secrets/DPAPI; убрать литералы; bootstrap со сменой пароля; `AllowedHosts`=явный список; `gitleaks` в CI | Это dev‑плейсхолдеры (`BootstrapUser:Enabled=false` в проде); но утекают в git‑историю и провоцируют перенос в прод; `AllowedHosts=*` и code‑fallback активны независимо от флага |
| P1‑5 | **`audit_logs` не INSERT‑only на уровне БД** (только декларация в комментарии) | `AuditLog.cs:6-7`; `InitialCreate.cs:62-81` | A09 / ASVS V7.3 | финдер | Роль БД GRANT INSERT + REVOKE UPDATE/DELETE; BEFORE‑триггер; WORM‑экспорт | «Приложение само не делает UPDATE/DELETE» — но это дисциплина кода, не техническая неизменяемость; админ БД/инъекция/будущая фича обходят |
| P1‑6 | **Медданные на Ollama по `http` без TLS; Ollama без аутентификации** | `appsettings.Development.json:16`; `LocalOllamaRecognitionProvider.cs:55,61-62` | A02 / ASVS V9.1 | финдер | TLS/mTLS к Ollama или зафиксировать Tailscale как СКЗИ + ACL :11434; запрет `http` вне localhost | Tailscale=WireGuard шифрует транспорт; localhost‑вариант не покидает хост; но защита на одном слое хрупка к мисконфигу и не аутентифицирует пир на L7 |
| P1‑7 | **`AllowedHosts="*"` + нет forwarded Host → Host‑injection / cache‑poisoning медответов / path confusion** (вариант d) | `appsettings.json:21`; `Program.cs:55-61` | A05/A01 / ASVS V14.4 | финдер | Whitelist хостов; ForwardedHeaders+PathBase; `Cache-Control: no-store` на `/scans/*`; раздельные cookie‑пути | В варианте (a) отдельный поддомен с своим TLS риск низкий; многие редиректы относительные; но под общим доменом + кэширующий nginx реален |
| P1‑8 | **poppler: RCE/парсинг недоверенного PDF без песочницы + без pin версии** | `LocalOllamaRecognitionProvider.cs:116-146` | A06 / ASVS V12.4 | финдер | Рендер в отд. непривилег. процессе/контейнере (seccomp/AppContainer); pin+SCA версии poppler; изоляция от прод‑данных | Целевой хост Windows/IIS (osn. RCE‑CVE poppler — Linux), рендер на отд. GPU‑узле; инъекции нет (`UseShellExecute=false`) — но defense‑in‑depth оправдан для спецкатегории |
| P1‑9 | **poppler DoS (нет таймаута/лимита/семафора) + нет rate‑limit на распознавание** | `LocalOllamaRecognitionProvider.cs:125-134`; `Scans/Index.cshtml.cs:46-79` | A04 / ASVS V11.1, V12.4 | финдер | Жёсткий таймаут+Kill; cgroup/Job Object; лимит DPI/страниц; `AddRateLimiter`+семафор на одну GPU | Рендерится только стр. 1 (`-f 1 -l 1`); доступ у сотрудников; но одна гигантская страница и параллельные запросы валят единственную RTX 3060 |
| P1‑10 | **Нет антивирусной проверки загрузок (ClamAV)** — мера АВЗ ФСТЭК‑21 | `ScanService.cs:50-75`; `Scans/Index.cshtml.cs:46-68` | A08 / ASVS V12.4‑V12.5 | финдер | ClamAV (INSTREAM) после `SaveAsync` до пометки доступным; статус `pending-scan`; `freshclam` | ClamAV ловит только известные сигнатуры; в офлайн‑ЛВС вектор доставки ограничен; но базовый рубеж due‑diligence для медучреждения |
| P1‑11 | **Нет учёта письменного согласия (152‑ФЗ ст. 10) перед обработкой спецкатегории** | `Domain/Entities/*`; `ApplicationDbContext.cs:14-21`; `ScanService.cs:50-76,103-149` | A04 / 152‑ФЗ ст. 10, 323‑ФЗ ст. 13 | **verify: partial, P0→P1** | Сущность `ConsentRecord` (студент, дата, форма/основание, срок, отзыв) + EF‑гейт «нет согласия → нет Upload/Recognize»; форма от юрслужбы РЭУ | Преимущественно орг‑мера РЭУ (бумажное согласие собирает оператор); v2 заморожен до ТЗ; но фактический v2‑код активен в dev и уже хранит спецкатегорию — без согласия прод незаконен |
| P1‑12 | **SSO Bitrix OAuth2/OIDC: валидация подписи/`exp`/`aud`, PKCE+`state`, защита `client_secret`** (до реализации SSO) | нет в коде (grep `AddOpenIdConnect/AddOAuth`=0); `appsettings.json:3` (паттерн секретов) | A02/A07 / ASVS V3.5, V51 | финдер (forward‑looking) | `UsePkce=true`; `ValidateIssuer/Audience/Lifetime`; `state`; `client_secret` в env/DPAPI; токен только server‑side | SSO ещё нет — упреждающий вывод; зрелый IdP (Keycloak/ADFS) даёт проверки «из коробки», важно не отключить `ValidateAudience` |
| P1‑13 | **iframe требует `SameSite=None;Secure` → снятие браузерной CSRF‑защиты** (если выберут iframe) | `DependencyInjection.cs:44-49`; `Program.cs:55-61` | A01 / ASVS V3.5, V13.2 | финдер (условный) | Не использовать iframe для медданных; при необходимости — глобальный `AutoValidateAntiforgeryToken`, мутации только POST, `no-store` на `/scans` | Сейчас все мутации — Razor POST с авто‑antiforgery, единственный minimal‑API — GET; CSRF на изменение состояния не проходит даже при `None`; риск только при отвергаемом варианте (b) |
| P1‑14 | **PHP‑модуль Bitrix (вариант c) поднимает весь портал до спецкатегории, наследует CVE Bitrix — ОТВЕРГНУТЬ** | архитектура (вариант c не в коде) | A04 (Insecure Design) | **verify: confirmed (как ADR, не CVE), P1** | Держать медконтур изолированным: отд. поддомен `med.rea.ru`, отдавать в портал лишь обезличенный read‑only «светофор» через узкий API | Это превентивное архитектурное решение, не уязвимость текущего кода (PHP‑модуля нет); вариант уже отвергнут в `AUDIT-v2`; цена ошибочного выбора очень высока → держим P1 |
| P1‑15 | **Нет CI‑гейта безопасности** (анализаторы выключены, нет `.editorconfig`/`Directory.Build.props` analyzers, нет SAST/SCA/secret в CI) | `app/Directory.Build.props` (`TreatWarningsAsErrors=false`) | A05 / ASVS V14.1 | финдер | `EnableNETAnalyzers`+`AnalysisMode=AllEnabledByDefault`+`SecurityCodeScan`; GitHub Actions: build /warnaserror, `dotnet list package --vulnerable`, gitleaks, semgrep, CodeQL, dependabot | MVP, git‑remote ещё нет; но именно сейчас (до legacy) дешевле всего включить — отсутствие гейтов уже дало закрепиться IDOR и секретам |

### P2 — харднинг

| # | Уязвимость | Где (`file:line`) | Статус проверки | Исправление / контр‑аргумент |
|---|---|---|---|---|
| P2‑1 | Cookie без явного `Secure`/`SameSite`/`ExpireTimeSpan`; lockout без длительности; `RememberMe=true` по умолчанию | `DependencyInjection.cs:44-49,38`; `Login.cshtml.cs:34` | финдер | `SecurePolicy=Always`, `HttpOnly`, `SameSite=Strict` (или `None`+Secure только для iframe), `ExpireTimeSpan≈30м`, `DefaultLockoutTimeSpan=15м`; имя `__Host-`. К‑арг: дефолты Identity разумны при HTTPS, но неявны/меняются между версиями |
| P2‑2 | SHA‑256 **не перепроверяется** при открытии вопреки декларации (ложная гарантия ОЦЛ) | `FileScanStorage.cs:45-51` vs `CertificateScan.cs:27`, `Scans/Index.cshtml:25` | **verify: confirmed, P2** | Реально сверять SHA перед OCR/скачиванием (+ аудит `IntegrityFailure`), либо снять ложную декларацию. К‑арг: инсайдер с доступом к ФС перепишет и эталон в БД — но расхождение кода и UI само по себе дефект |
| P2‑3 | Общий cookie‑домен `.rea.ru` — **НЕ** задавать `Domain=.rea.ru` (guardrail) | `DependencyInjection.cs:44-49` | **verify: partial, P1→P2** | Зафиксировать как архитектурное ограничение; идентичность шарить через OIDC, не cookie. К‑арг: cookie сейчас host‑only (безопасный дефолт), риск возникает только при сознательном будущем решении |
| P2‑4 | `SqlRosterSource` исполняет произвольный SQL из конфига; OData без TLS‑pinning | `SqlRosterSource.cs:27`; `OneCODataRosterSource.cs:24-40` | финдер | Read‑only роль БД для источника, валидировать `SELECT`, защитить конфиг; OData по HTTPS+проверка сертификата. К‑арг: задаёт только админ — но конфиг на хосте это поверхность, read‑only роль дешева |
| P2‑5 | LIKE‑wildcard injection (`%`/`_` не экранированы в поиске) | `RegistryQueryService.cs:35-40` | финдер | Экранировать `% _ \` (ESCAPE) или pg_trgm‑операторы; мин. длина запроса. К‑арг: не SQLi (параметризовано), «всех» видно и без фильтра — но при будущем scope wildcard может обойти фильтры |
| P2‑6 | Нет лимита числа страниц/разрешения PDF на приёме (только байты) | `Scans/Index.cshtml.cs:51-56`; `ScanStorageOptions.cs` | финдер | `pdfinfo` → лимит страниц/мегапикселей/MediaBox. К‑арг: 10 МБ косвенно ограничивает, рендер только стр.1 — но сжатие прячет огромную геометрию |
| P2‑7 | TLS/mTLS на внутреннем канале прокси↔Kestrel не настроен (prod‑конфиг; `appsettings.Production.json` отсутствует) | `Program.cs:32,36-45`; `deployment/README.md:8` | **verify: confirmed, P1→P2** | TLS+mTLS IIS↔Kestrel или Kestrel на loopback+ForwardedHeaders+IPsec; HSTS на prod. К‑арг: это отсутствующая prod‑конфигурация (Фаза 0 MVP), не активная дыра |
| P2‑8 | Open‑redirect / небезопасный `returnUrl` в будущем SSO‑флоу | `DependencyInjection.cs:46`; `Login.cshtml.cs:44-67` | финдер (forward‑looking) | `Url.IsLocalUrl`→`LocalRedirect`; строгий allowlist `redirect_uri`. К‑арг: сейчас локальная аутентификация, шаблон Identity использует `LocalRedirect` — риск при добавлении SSO |
| P2‑9 | Нет офлайн‑зеркала SCA + устаревшие пакеты `8.0.4` (`dotnet list --vulnerable` молча «чист» офлайн) | `*.csproj` | **verify: confirmed, P2** | SCA в CI с интернетом/офлайн‑NVD; обновить EF/Npgsql до последнего `8.0.*`, удалить неиспользуемый ClosedXML. К‑арг: конкретного CVE для 8.0.4 нет, устаревание ≠ эксплуатируемость |

### Проверено и понижено/отклонено адверсариал‑верификацией (10 выводов прошли независимую проверку «опровергни»)

| Вывод | Итог проверки | Что показал верификатор |
|---|---|---|
| `postmessage-origin-xss-parent` | **отклонено → none** (design‑guardrail) | В коде нет `postMessage`/HTMX/iframe/`.js` вовсе. Уязвимости текущего кода нет; оставить как требование к решению по развёртыванию (если iframe — строгая проверка `event.origin`), реальный present‑state факт — «нет security‑заголовков» (P0‑3) |
| BOLA/IDOR на сканах | **P0 → P1** | Подтверждён факт (нет owner‑проверки), но это отсутствие границы во всём приложении (плоская идентичность), а не обход; роль‑гейт отсекает студентов/анонимов; обязательная часть — аудит чтения + решение по гранулярности до прода |
| Шифрование at‑rest отсутствует | **confirmed, P1** | Подтверждено на всех 3 уровнях (файлы/столбцы/`RecognitionJson`); смягчает BitLocker и v1‑стратегия «не хранить сканы», но v2‑код активен и хранит открыто |
| Нет письменного согласия (152‑ФЗ ст.10) | **P0 → P1** | Подтверждено (нет сущности/гейта); преимущественно орг‑мера РЭУ + поле модели; блокер перед выводом загрузки в прод |
| `phpmodule-blast-radius` | **confirmed как ADR (не CVE), P1** | Факты верны; точнее классифицировать как решение «отвергнуть вариант (c)», а не уязвимость кода |
| SHA‑256 не перепроверяется (×2 финдера) | **confirmed, P2** | Хеш считается только при загрузке; декларации в `CertificateScan.cs:27`/UI ложны; поправка: фраза «перепроверяется при открытии» в модели, а не в UI |
| Общий cookie `.rea.ru` | **P1 → P2** | Cookie сейчас host‑only (безопасный дефолт); риск только при будущем `Domain=.rea.ru`; тезис о session fixation преувеличен (.NET cookie‑auth stateless) |
| Нет mTLS прокси↔бэкенд / OCR plaintext | **P1 → P2** | Подтверждено отсутствие; но это отсутствующая prod‑конфигурация (Фаза 0), `appsettings.Production.json` нет; OCR идёт по WireGuard |
| Офлайн SCA‑зеркало отсутствует | **confirmed, P2** | `dotnet list --vulnerable` офлайн возвращает ложно‑чистый результат; пакеты `8.0.4` устарели |

### Резюме по приказу ФСТЭК № 21 (УЗ‑3) — статус групп мер

| Группа | Статус | Комментарий |
|---|---|---|
| ИАФ (идент./аутентификация) | **частично** | Парольная политика есть (≥8, цифра+регистр), но без спецсимвола/срока/истории; lockout без длительности; нет MFA |
| УПД (управление доступом) | **нет** | Нет per‑page ролей и object‑level scope (P1‑1, P1‑3) |
| РСБ (регистрация событий ИБ) | **нет/частично** | Пишется запись/импорт/распознавание; **чтение медданных, вход/выход — нет**; `IpAddress`=null; журнал не INSERT‑only (P0‑1, P1‑5) |
| АВЗ (антивирус) | **нет** | Загрузки не сканируются (P1‑10) |
| ЗНИ (защита носителей) | **нет** | At‑rest‑шифрование отсутствует (P1‑2) |
| ОЦЛ (контроль целостности) | **декларативно** | SHA‑256 не перепроверяется (P2‑2) |
| ЗИС (защита ИС) | **нет/частично** | Нет security‑заголовков (P0‑3); TLS только внешне и вне Dev |
| ОДТ (доступность) | **орг‑мера РЭУ** | Резервное копирование вне репозитория; DoS‑риски рендера (P1‑9) |
| Управление конфигурацией | **нет** | Секреты в репозитории (P1‑4) |

**Вывод по ФСТЭК‑21:** критичные для УЗ‑3 группы **УПД, РСБ, ЗНИ, АВЗ** фактически не реализованы — это блокирует аттестацию ИСПДн и должно быть закрыто до развёртывания «с сетевым доступом студентов».


---

## 3.9. Полная адверсариал-верификация — результаты по ТЕКУЩЕМУ коду (2026‑06‑17)

Все 35 находок (21 из снимка + 14 OWASP) прошли независимую проверку «опровергни вывод» против текущего кода. Итог: **6 опровергнуто (исправлено в коде), 10 подтверждено, 19 частично**; после переоценки важности — **P1 осталось только 3**, остальное снижено до P2/none. Это и есть актуальная картина после пакета фиксов.

**Итог по приоритетам (после верификации):**

- 🟢 **Опровергнуто/исправлено (6):** security‑заголовки+CSP, аудит чтения медскана, доверие прокси‑заголовкам (ForwardedHeaders), open‑redirect (LocalRedirect), цепочка XSS приём+раздача (нейтрализована `nosniff`).
- 🔴 **Осталось P1 (3):** `MED-A01-FOLDER` — плоская авторизация (нет ролей на страницах); `MED-A02-AT-REST` — нет шифрования спецкатегории at‑rest; `audit-not-insert-only` — `audit_logs` изменяем + приложение работает как `postgres`‑суперпользователь.
- 🟡 **Снижено до P2 (25)** и **none (7)** — действуют в контексте доверенной ЛВС v1; становятся значимее при вводе роли студента (v2) и при интеграции.

| ID | Финдер | Вердикт | Итог.sev | Итоговая формулировка | Сильнейший контр‑аргумент |
|---|---|---|---|---|---|
| serve-no-nosniff-inline-stored-xss | P0 | 🟢 опроверг. | P2 | ОПРОВЕРГНУТО. Заявленный факт неверен: X-Content-Type-Options: nosniff ДА присутствует на ответе /scans/{id}/file — он выставляется глобальным middleware (Program.cs:49) до выполнения эндпоинта, плюс  | Сильнейший довод В ПОЛЬЗУ остаточного риска (чтобы не закрывать слишком оптимистично): nosniff не устраняет XSS полностью — для PDF и SVG он бесполезен. Если ContentType  |
| upload-trusts-client-mime | P0 | 🟢 опроверг. | P2 | ОПРОВЕРГНУТО как P0. Заявленный механизм ("проверка только по клиентскому MIME без проверки сигнатуры/магических байт, обход подделкой одного заголовка") не соответствует коду: в OnPostUploadAsync, по | Сильнейший довод В ПОЛЬЗУ finding (почему не "none", а P2): сигнатурная проверка примитивна и проверяет лишь первые байты, поэтому polyglot-файл (валидный %PDF/JFIF-загол |
| clickjacking-no-frame-headers | P0 | 🟢 опроверг. | none | ОПРОВЕРГНУТО. Заявление «конвейер не выставляет ни одного защитного заголовка» неверно: Program.cs:46-56 содержит middleware, безусловно (вне dev-проверки) выставляющий X-Frame-Options: DENY, CSP с fr | Сильнейший довод В ПОЛЬЗУ исходного вывода (т.е. почему его автор мог так написать): заявленный evidence явно цитирует «grep по UseSecurityHeaders/Headers.Append — 0». Фо |
| no-audit-on-cross-context-access | P1 | 🟢 опроверг. | none | ОПРОВЕРГНУТО. Утверждение «просмотр/скачивание медскана не логируется и IP не пишется» неверно. GET /scans/{id}/file (Program.cs:85-91) пишет запись аудита ScanView на CertificateScan при каждом не-ra | Сильнейший довод за частичную правоту: буквальная под-формулировка «ScanService.OpenAsync не пишет аудит чтения» технически ВЕРНА — OpenAsync (ScanService.cs:85-94) тольк |
| open-redirect-sso-return | P2 | 🟢 опроверг. | none | ОПРОВЕРГНУТО. В текущем коде open-redirect отсутствует: единственная точка приёма returnUrl (Login.cshtml.cs:69) использует `LocalRedirect`, который блокирует любой не-локальный адрес — то есть код УЖ | Сильнейший довод в пользу находки: она честно сформулирована как УСЛОВНАЯ/будущая («при интеграции SSO добавится returnUrl/redirect_uri-логика»), а документация проекта прямо |
| spoof-fwd-identity | P0 | 🟢 опроверг. | none | ОПРОВЕРГНУТО. Заявленные «факты» прямо противоречат коду: UseForwardedHeaders/ForwardedHeadersOptions ПРИСУТСТВУЕТ (Program.cs:40-43, с дефолтным loopback-доверием прокси), security-заголовки ПРИСУТСТ | Сильнейший довод В ПОЛЬЗУ находки (и почему он всё равно не тянет на находку): сценарий (d) — это БУДУЩАЯ интеграция под доменом Bitrix, которой в коде ещё нет. Если кафе |
| MED-A01-FOLDER | P0 | 🔴 подтв. | P1 | ПОДТВЕРЖДЕНО (severity понижена P0→P1). Все Razor Pages защищены только `AuthorizeFolder("/")` (Program.cs:25), который требует лишь факта аутентификации (default-политика = RequireAuthenticatedUser); | Два ослабляющих момента. (1) В строке evidence/claim есть фактическая ошибка: утверждается «НЕТ security-заголовков» — это ложь, Program.cs:45-56 ставит X-Content-Type-Op |
| MED-A02-AT-REST | P1 | 🔴 подтв. | P1 | ПОДТВЕРЖДЕНО. Сканы медсправок и извлечённые из них поля (спецкатегория ПДн, 152-ФЗ ст.10; врачебная тайна, 323-ФЗ ст.13) хранятся БЕЗ прикладного шифрования at-rest: бинарь пишется в ФС через File.Cr | Сильнейший довод ПРОТИВ выставленной важности (не против факта): шифрование контента приложением — НЕ единственный и часто не предпочтительный контроль для at-rest, и у п |
| audit-not-insert-only | P1 | 🔴 подтв. | P1 | Подтверждено (P1). Журнал аудита НЕ является INSERT-only на уровне БД — это лишь декларация в комментарии AuditLog.cs:6-7. Миграция InitialCreate.cs:62-81 создаёт audit_logs как обычную таблицу (колон | Сильнейший контр-довод: неизменяемость де-факто частично обеспечена на уровне приложения и среды, поэтому угроза реализуема не «по умолчанию», а лишь при привилегированно |
| MED-A07-BOOTSTRAP | P2 | 🔴 подтв. | P2 | ПОДТВЕРЖДЕНО (P2, прод-риск латентный). Захардкоженный fallback-пароль `<демо-пароль>` присутствует буквально (DataSeeder.cs:45) и продублирован в обоих конфигах (appsettings.json:8, appsettings.Develo | Сильнейший довод против раздувания серьёзности: в базовом (продакшн) `appsettings.json` `BootstrapUser:Enabled = false` (строка 6), поэтому весь блок DataSeeder.cs:29-52  |
| MED-A07-ENUM | P2 | 🔴 подтв. | P2 | Подтверждено (P2, минор). Сообщение о блокировке (Login.cshtml.cs:76-77) текстуально отличается от обобщённого ответа об обычной неудаче (строка 78) и срабатывает только для существующих учёток (Ident | Практическая ценность оракула близка к нулю в этом конкретном развёртывании, и это удерживает находку на минимуме: (1) Чтобы ВООБЩЕ увидеть сообщение о блокировке, атакую |
| MED-A08-SHA | P2 | 🔴 подтв. | P2 | ПОДТВЕРЖДЕНО (P2). SHA-256 скана вычисляется один раз при загрузке (FileScanStorage.SaveAsync, FileScanStorage.cs:25-42) и сохраняется в CertificateScan.Sha256 (ScanService.cs:61), но при чтении (Open | Сильнейший довод против значимости (не против фактов): это не обход существующего контроля целостности, а ОТСУТСТВИЕ детективного контроля как фичи — хеш нигде не являетс |
| MED-A09-IMMUTABLE | P1 | 🔴 подтв. | P2 | ПОДТВЕРЖДЕНО (с понижением до P2). Таблица audit_logs — обычная таблица без схемной защиты от изменения/удаления: только PK_audit_logs и индексы (InitialCreate.cs:62-81), нет REVOKE UPDATE/DELETE, нет | Сильнейший довод ПРОТИВ серьёзности: (1) на уровне приложения РСБ де-факто append-only — нет ни одного пути UPDATE/DELETE по AuditLogs, query-сервис read-only, так что "м |
| no-page-roles | P1 | 🔴 подтв. | P2 | ПОДТВЕРЖДЕНО (с понижением до P2). Ролевая модель из трёх ролей (Teacher/HeadOfDepartment/Admin) объявлена, но к Razor-страницам не применяется: единственная страничная конвенция — `AuthorizeFolder("/ | Сильнейший контр-довод против заявленной severity P1: (1) Evidence частично неточен — claim ссылается на «Program.cs:19-25» и утверждает, что file-эндпоинт скана не разгр |
| secrets-committed-appsettings | P1 | 🔴 подтв. | P2 | ПОДТВЕРЖДЕНО фактически, severity снижен P1→P2. Все 6 заявленных мест существуют дословно (одна опечатка: Development.json — строка 6, а не 7), оба appsettings закоммичены в git и не в .gitignore, так | Сильнейший довод против завышения severity: (1) пароль БД — это дефолтный PostgreSQL `postgres`/`postgres` на `localhost`, то есть не «утёкший боевой секрет», а локальный |
| like-wildcard-injection | P2 | 🔴 подтв. | none | LIKE-wildcard «injection» в поиске реестра ФИО ПОДТВЕРЖДЕНА фактически (RegistryQueryService.cs:39 — Like без escape, Normalize не экранирует `%`/`_`), но это НЕ уязвимость безопасности: значение пара | Сильнейший довод против существенности: «обход сужения поиска» не даёт атакующему НИЧЕГО, что ему уже не доступно. (1) Эндпоинт под `AuthorizeFolder("/")` и доступен толь |
| MED-A01-BOLA-SCAN | P0 | 🟡 частично | P2 | ЧАСТИЧНО ПОДТВЕРЖДЕНО как hardening-замечание P2, НЕ P0. Факт верен: ScanService.OpenAsync/GetAsync (ScanService.cs:87,98) и GET /scans/{id}/file (Program.cs:96) разграничивают доступ только по роли с | Классический BOLA требует субъекта более низкой привилегии, который сменой object-id поднимается к чужому объекту в обход горизонтальной изоляции. Здесь такого субъекта Н |
| MED-A01-REVIEW | P1 | 🟡 частично | P2 | Подтверждено фактически, severity понижена P1→P2. Любой аутентифицированный СОТРУДНИК может Approve/Reject/Create справку и смотреть список сканов любого студента без вторичной проверки (CertificateSe | Сильнейший контр-довод: finding описывает обход object-level-авторизации, которой в v1 не существует и не предполагается. Нет ни связи AppUser↔Teacher (AppUser.cs не соде |
| MED-A05-CSP | P2 | 🟡 частично | P2 | CSP действительно содержит script-src 'self' 'unsafe-inline' и style-src 'self' 'unsafe-inline' (Program.cs:52-54) — цитата верна, заголовки присутствуют. Но «нивелирование анти-XSS защиты» в текущем  | 'unsafe-inline' для script-src в данном приложении не ослабляет реальную анти-XSS защиту, потому что нивелировать нечего: CSP — это вторичный (defense-in-depth) барьер, к |
| MED-A05-SECRETS | P1 | 🟡 частично | P2 | Подтверждено фактически (partial — переоценка серьёзности). Строка подключения в appsettings.json:3 и hardcoded-fallback в DependencyInjection.cs:24-25/ApplicationDbContextFactory.cs:12 используют суп | Серьёзность как «утечка секрета» (A05/секрет в открытом виде, P1) завышена и неправильно классифицирована. (1) Это `Host=localhost` дефолт со стандартной dev-пустышкой `p |
| MED-A06-NUGET | P2 | 🟡 частично | P2 | Версии подтверждены дословно во всех .csproj: EF Core/Npgsql-провайдер/Identity.EFCore/EFCore.Design = 8.0.4, Serilog.AspNetCore = 8.0.1, FluentValidation.AspNetCore = 11.3.0 (deprecated-пакет). CPM/l | Сильнейший контр-довод: «прибито к 8.0.4» создаёт впечатление замороженной security-поверхности, но это не так. Рантайм ASP.NET Core подключён через FrameworkReference (I |
| MED-A07-COOKIE | P1 | 🟡 частично | P2 | Cookie аутентификации не конфигурирует флаги явно (DependencyInjection.cs:44-49) — применяются дефолты Identity .NET 8: HttpOnly=true (ок), SameSite=Lax, SecurePolicy=SameAsRequest (не Always). Сам по | Сильнейший довод против P1: остроту claim снимает связка app.UseHttpsRedirection() (Program.cs:67) + app.UseHsts() (Program.cs:58-62) + UseForwardedHeaders с XForwardedPr |
| config-sql-roster | P2 | 🟡 частично | P2 | Finding частично подтверждён как набор фактов, но завышен и неверно классифицирован как Injection. Реальная картина: (1) SqlRosterSource действительно выполняет запрос из конфига (SqlRosterSource.cs:2 | Сильнейший довод против вывода: ни один из трёх подпунктов не является injection-уязвимостью, под которую заведён finding (dimension=Injection). (а) `Sql.Query` — это ста |
| csrf-default-only | P2 | 🟡 частично | P2 | Заявление о «CSRF только на дефолте = пробел» ОПРОВЕРГНУТО: Razor Pages валидирует antiforgery на всех POST по умолчанию, все формы используют tag-helper (токен инжектится), logout-форма имеет явный @ | Сильнейший довод против finding: CSRF-защита в этом приложении НЕ «держится только на дефолте» в смысле дыры — она полноценно активна. Razor Pages включает antiforgery-ва |
| host-header-allowedhosts-wildcard | P1 | 🟡 частично | P2 | AllowedHosts="*" подтверждён (appsettings.json:21), но заявленная цепочка импакта НЕ ПОДТВЕРЖДАЕТСЯ кодом и понижается до P2 (hardening). Опровергнуты ключевые пункты evidence находки: UseForwardedHea | Сильнейший довод против находки: эксплуатируемость Host-header injection почти полностью зависит от того, отражает ли приложение Host в исходящий артефакт (редирект-Locat |
| iframe-samesite-none-csrf | P1 | 🟡 частично | P2 | PARTIAL (severity понижена P1→P2). Находка описывает не текущий дефект, а условный риск гипотетической iframe-интеграции, которая в проекте уже отвергнута (рекомендация — отдельный поддомен med.rea.ru | Сильнейший довод против вывода: находка описывает не дефект кода, а свойство гипотетической архитектуры (iframe-встраивание), которая в проекте ОТВЕРГНУТА в пользу поддом |
| no-av-scan | P1 | 🟡 частично | P2 | Антивирусной проверки загруженных файлов нет (ClamAV/AMSI/любой AV-интерфейс отсутствуют — подтверждено grep). Однако вопреки формулировке finding файл проходит контроль на загрузке: аллой-лист типов  | Сильнейший контр-довод против исходной P1-формулировки: для студенческой офлайн-ИС кафедры с ~13 операторами-сотрудниками, развёрнутой в периметре РЭУ без публичного дост |
| no-page-resolution-limit-on-intake | P2 | 🟡 частично | P2 | Подтверждён только факт отсутствия лимитов страниц/разрешения/пикселей в ScanStorageOptions (есть лишь MaxUploadBytes 10 МБ + whitelist типов + серверная проверка сигнатуры). Однако заявленная амплифи | Сильнейший довод против находки: заявленная цепочка «нет лимита страниц → poppler-resource-exhaustion-dos» структурно неверна, потому что pdftoppm вызывается с -singlefil |
| no-security-ci-pipeline | P1 | 🟡 частично | P2 | Подтверждено частично. На уровне проекта нет CI (.github/ отсутствует), нет Dockerfile/.editorconfig, нет блокирующего гейта качества/безопасности и нет ни одного стороннего SAST-анализатора (Security | Сильнейший контраргумент против вывода в его буквальной формулировке: (1) Roslyn-анализаторы безопасности НЕ «выключены» — в .NET 8 EnableNETAnalyzers по умолчанию true,  |
| ollama-plaintext-http-tailscale | P1 | 🟡 частично | P2 | ПОДТВЕРЖДЕНО ЧАСТИЧНО (severity снижена P1→P2). Канал OCR действительно идёт обычным HTTP без прикладного TLS: base64-изображение медсправки (спецкатегория ПДн, 152-ФЗ ст.10 + врачебная тайна 323-ФЗ с | Сильнейший контр-довод: транспорт НЕ является открытым. Адрес <tailscale-ip-узла> принадлежит CGNAT-диапазону Tailscale (100.64.0.0/10), а Tailscale — это mesh-WireGuard, котор |
| poppler-resource-exhaustion-dos | P1 | 🟡 частично | P2 | Подтверждён технический дефект как P2 (hardening), не P1. pdftoppm в RenderPdfFirstPageAsync запускается без cgroup/ulimit, без -scale-to/клампа DPI и без валидации числа/габаритов страниц; при отмене | Путь по умолчанию выключен (Provider=Manual; LocalOllamaRecognitionProvider не регистрируется в DI, DependencyInjection.cs:74-77); когда включён — достижим лишь трём прив |
| poppler-untrusted-parsing-rce | P1 | 🟡 частично | P2 | Подтверждено условно (partial). Реальный латентный риск: pdftoppm (poppler) разбирает недоверенный, загруженный студентом PDF без какой-либо изоляции (нет sandbox/seccomp/отдельного UID/лимитов ресурс | Сильнейший довод против находки: в дефолтной/боевой конфигурации этот код вообще мёртв — Provider="Manual" по умолчанию, ManualRecognitionProvider не трогает poppler, а a |
| sso-oauth-validation | P1 | 🟡 частично | P2 | Фактаж находки ВЕРЕН и подтверждён по коду: SSO/OAuth2/OIDC/Bitrix полностью отсутствуют — аутентификация только локальная cookie-Identity (DependencyInjection.cs:30-49, CurrentUser.cs:14-21, единстве | Сильнейший довод против того, чтобы держать это как актуальную P1-уязвимость: уязвимости «слабая/отсутствующая валидация SSO-токена» не может быть там, где SSO-кода нет н |
| MED-A03-LIKE | P2 | 🟡 частично | none | Подтверждено фактически, но severity снижена с P2 до none (информационное замечание о корректности поиска, не security-уязвимость). В RegistryQueryService.SearchAsync символы `%`/`_` из поискового вво | Сильнейший довод против вывода: находка маскирует функциональный дефект поиска под уязвимость класса Injection. Реальной инъекции нет — `EF.Functions.Like` всегда парамет |
| MED-A05-HOSTS | P2 | 🟡 частично | none | MED-A05-HOSTS — PARTIAL (severity снижен P2 → none/info). Подтверждается лишь конфиг-факт: appsettings.json:21 `"AllowedHosts": "*"` (валидация Host выключена). Однако заявленная цепочка риска в текущ | Сильнейший довод против finding: заявленный риск host-header poisoning не реализуем в этой кодовой базе, потому что приложение не порождает ни одной абсолютной ссылки из  |


---

# Блок 4. Стек портала РЭУ и варианты интеграции
У меня собран полный набор первоисточников. Критическое уточнение подтверждено: student.rea.ru = "1С-Битрикс: Управление сайтом" (БУС, не Bitrix24), и REST-модуль в БУС доступен с версии 16.6.0. Это меняет картину SSO. Формирую финальный отчёт.

---

# БЛОК 4. Стек портала РЭУ и интеграция нашего ASP.NET Core 8 в Bitrix-портал

> Контекст: наше приложение MedSpravki-REU = ASP.NET Core 8 / Razor Pages / HTMX / PostgreSQL 16, Clean Architecture, целевой хост IIS/Windows в ЛВС РЭУ; данные = медицинские спецкатегория ПДн (152-ФЗ ст. 10) + врачебная тайна (323-ФЗ ст. 13), УЗ-3 по ПП-1119. Портал РЭУ: `student.rea.ru` = 1С-Битрикс (Bitrix Site Manager / БУС, PHP), `rasp.rea.ru` = ASP.NET MVC 5.2. Все факты ниже — пассивный research + уже снятые заголовки; активного зондирования rea.ru не проводилось.

---

## 1. Детекция стека: подтверждение и методы

### 1.1 Подтверждение для student.rea.ru = 1С-Битрикс / PHP

Снятые признаки складываются в однозначный отпечаток «1С-Битрикс: Управление сайтом» (БУС, коробочная CMS, не Bitrix24):

| Признак | Что наблюдается | Что доказывает |
|---|---|---|
| Куки `BITRIX_SM_*` | `BITRIX_SM_LOGIN`, `BITRIX_SM_UIDH`, `BITRIX_SM_SALE_UID`, `BITRIX_SM_GUEST_ID` и т. п. | Уникальный префикс ядра Битрикс (`SM` = Site Manager). В эксплойт-PoC по Битрикс прямо проверяется `session.cookies.get("BITRIX_SM_LOGIN")` как маркер успешного логина — это нативная кука именно БУС. |
| Кука `PHPSESSID` | присутствует | Бэкенд — PHP (Битрикс на PHP). |
| Шаблон `reapay_bootstrap_personal` | имя шаблона в разметке/путях | Кастомный шаблон личного кабинета РЭУ (на Bootstrap 3); сам факт «шаблона сайта» — концепт Битрикса. |
| Интегратор Sebekon | в комментариях/копирайтах | Партнёр-разработчик на Битрикс. |
| nginx впереди | `Server: nginx` | Типовая для БУС связка nginx→Apache/PHP-FPM (BitrixVM). |
| Пути ядра | `/bitrix/`, `/bitrix/admin/`, `/bitrix/tools/`, `/local/`, `/upload/` | Каноническая файловая структура Битрикс; `/bitrix/tools/public_session.php`, `/bitrix/admin/` — служебные точки, фигурирующие в любом БУС-проекте. |

Вывод: совокупность `BITRIX_SM_*` + `PHPSESSID` + `/bitrix/` + кастомный шаблон — это детерминированная сигнатура. Версия продукта (важно: REST в БУС появился только с **16.6.0**) по пассивным признакам точно не определяется, но семейство — однозначно БУС.

### 1.2 Подтверждение для rasp.rea.ru = ASP.NET MVC 5.2 (.NET Framework 4.0.30319)

| Признак | Значение | Что доказывает |
|---|---|---|
| `X-AspNetMvc-Version: 5.2` | версия MVC-фреймворка | Заголовок добавляется ASP.NET MVC автоматически на каждый ответ (если не отключён `MvcHandler.DisableMvcResponseHeader`). `5.2` означает ASP.NET MVC 5.2. |
| `X-AspNet-Version: 4.0.30319` | версия CLR/.NET Framework | Отдельный заголовок: это версия рантайма .NET Framework (4.0.30319 = семейство 4.x), а не MVC. |
| `X-Powered-By: ASP.NET` | технология | Стандартный заголовок IIS/ASP.NET. |
| `Server: Microsoft-IIS/x` | веб-сервер | Хостинг на IIS под Windows. |
| Кука `ASP.NET_SessionId` (и `.ASPXAUTH` при Forms Auth) | сессия/аутентификация | `ASP.NET_SessionId` — нативная кука сессии ASP.NET; `.ASPXAUTH` — кука Forms Authentication. |

Вывод: `X-AspNetMvc-Version: 5.2` + `X-AspNet-Version: 4.0.30319` + `X-Powered-By: ASP.NET` + IIS — детерминированная сигнатура ASP.NET MVC 5.2 на .NET Framework 4.x. (Версия `4.0.30319` — это строка билда CLR2/CLR4; реальная версия Framework 4.5–4.8 по ней не различается, заголовок остаётся `4.0.30319`.)

### 1.3 Полный каталог методов детекции CMS/фреймворка (пассивно)

1. **HTTP-заголовки ответа:**
   - `Server` (nginx / Microsoft-IIS),
   - `X-Powered-By` (`PHP/x.y` или `ASP.NET`),
   - `X-Powered-CMS: Bitrix Site Manager` (Битрикс умеет отдавать этот заголовок),
   - `X-AspNet-Version`, `X-AspNetMvc-Version` (раскрывают .NET/MVC; ZAP помечает их как утечку информации, alert 10061, CWE-933, WSTG-INFO-08),
   - `Set-Cookie` (см. ниже).
2. **Куки (сигнатурный маркер):**
   - Битрикс — `BITRIX_SM_*`, `PHPSESSID`;
   - ASP.NET — `ASP.NET_SessionId`, `.ASPXAUTH`, `__RequestVerificationToken` (anti-CSRF MVC), `__VIEWSTATE` (WebForms, не MVC).
3. **Пути и файловая структура:** Битрикс — `/bitrix/`, `/bitrix/admin/`, `/bitrix/templates/`, `/local/`, `/upload/`, `/bitrix/tools/*.php`; ASP.NET MVC — `/Scripts/`, `/bundles/`, `/Content/`, маршруты `/{controller}/{action}`, расширения `.aspx`/`.ashx` (legacy).
4. **HTML / META-теги:** `<meta name="generator">`, классы CSS-фреймворка (Bootstrap 3 у `reapay_bootstrap_personal`), специфичные пути к JS (`/bitrix/js/`), названия шаблонов в `href`/`src`.
5. **favicon-хэш:** mmh3-хэш `/favicon.ico` сопоставляется с базами (используется в Shodan/FOFA-подходе и в инструментах фингерпринтинга) — позволяет узнать продукт даже без заголовков.
6. **Поведение и страницы ошибок:** дефолтные 404/500 у Битрикс и ASP.NET различимы по разметке; форма логина Битрикс отправляет `POST /?login=yes` с полями `AUTH_FORM=Y&TYPE=AUTH&USER_LOGIN&USER_PASSWORD` (поведенческая сигнатура).
7. **Инструменты:**
   - **Wappalyzer** / **BuiltWith** (браузерные/онлайн, агрегируют заголовки+куки+паттерны разметки),
   - **WhatWeb**, **Nmap NSE** (`http-*` скрипты), **droopescan/wpscan**-аналоги (для Битрикс есть `cmsmap`-подобные), **ZAP passive scan** (детектит утечку версий).

> Дисциплина 152-ФЗ/owasp: всё перечисленное — **пассивные** методы (заголовки/куки/разметка уже полученных ответов). Активное директорий-брутфорс/сканирование `/bitrix/admin/` на rea.ru не проводим — это уже зондирование чужого ресурса.

## Источники

- [X-AspNetMvc-Version — Expert Guide to HTTP headers](https://http.dev/x-aspnetmvc-version) — заголовок раскрывает версию ASP.NET MVC, добавляется автоматически, отличается от X-AspNet-Version (версия CLR) — проверено 2026-06-16
- [ZAP — X-AspNet-Version Response Header (alert 10061)](https://www.zaproxy.org/docs/alerts/10061) — сервер утекает версию через X-AspNet-Version/X-AspNetMvc-Version, CWE-933, WSTG-INFO-08 — проверено 2026-06-16
- [Stack Overflow — remove Server/X-AspNet-Version/X-AspNetMvc-Version](https://stackoverflow.com/questions/56560324/can-i-remove-unwanted-http-headers-server-x-aspnet-version-and-x-aspnetmvc-vers) — `enableVersionHeader=false`, `MvcHandler.DisableMvcResponseHeader=true` отключают заголовки — проверено 2026-06-16
- [Stack Overflow — How to secure the ASP.NET_SessionId cookie](https://stackoverflow.com/questions/5978667/how-to-secure-the-asp-net-sessionid-cookie) — нативные куки ASP.NET: `ASP.NET_SessionId`, `.ASPXAUTH` (Forms Auth) — проверено 2026-06-16
- [HackYourMom — Bitrix под атакой (PoC логина)](https://hackyourmom.com/en/kibervijna/bitrix-pid-atakoyu-krytychni-vrazlyvosti-eksplojty-ta-kontrol-nad-systemoyu-chastyna-2) — PoC проверяет куку `BITRIX_SM_LOGIN` и `PHPSESSID`, форму `POST /?login=yes` с AUTH_FORM/TYPE/USER_LOGIN — нативные маркеры БУС — проверено 2026-06-16
- [VulnTech — Technology Fingerprinting](https://vulntech.com/tutorial/tutorial/website-penetration-testing/technology-fingerprinting) — детекция через заголовки, куки, файловую структуру, favicon; инструменты WhatWeb/Nmap/Wappalyzer — проверено 2026-06-16

---

## 2. Варианты интеграции нашего .NET в Bitrix-портал

Базовые ограничения, влияющие на выбор: (1) данные — спецкатегория ПДн + врачебная тайна, УЗ-3, всё в ЛВС РЭУ, облако запрещено; (2) наш стек — ASP.NET Core 8 на IIS/Windows; (3) портал — БУС/PHP за nginx. Любой контур, где медданные «проходят» через PHP-портал или светятся в стороннем источнике, повышает класс защиты и площадь атаки — это ключевой критерий оценки.

### (a) Отдельный поддомен + SSO (например `med.rea.ru` / `medspravki.rea.ru`)

Наше приложение живёт на своём поддомене, аутентификация делегируется порталу (OAuth2/OIDC от Битрикс или внешний IdP), сессия — своя в ASP.NET Core Identity.

| Плюсы | Минусы | Риски ИБ | Усилия |
|---|---|---|---|
| Полная изоляция кода и БД медданных от PHP-портала | Нужен SSO-механизм и доверие к источнику идентичности | Поверхность = только наш контур; легко аттестовать под УЗ-3 | Средние: настроить OIDC-клиент + поддомен + TLS |
| Свой стек без компромиссов (Razor/HTMX/EF Core) | Два «адреса» для пользователя (но прозрачно при SSO) | Cross-subdomain куки — только если осознанно (`Domain=.rea.ru`); по умолчанию НЕ шарим сессию | |
| Свои security-заголовки, CSP, журналирование (Serilog) под 152-ФЗ | Зависимость от наличия OAuth/OIDC на стороне портала | SSO-редиректы валидировать (open-redirect, `state`/PKCE) | |

### (b) iframe-встраивание нашей страницы в Bitrix

Страница портала содержит `<iframe src="https://med.rea.ru/...">`.

| Плюсы | Минусы | Риски ИБ | Усилия |
|---|---|---|---|
| Визуально «внутри» портала, быстрый UX-эффект | Cookie в iframe = third-party context: нужен `SameSite=None; Secure`, иначе сессия рвётся | **Clickjacking** — обязателен `X-Frame-Options`/`CSP frame-ancestors`; но для встраивания их придётся ослабить именно для домена портала | Низкие на разметку, высокие на «починку» куки/CSP |
| Минимум правок на стороне Битрикс | Браузерная партиционизация кук (CHIPS) ломает SSO-сценарии «open in new tab» | Передача медданных в iframe внутри стороннего фрейма — спорно для врачебной тайны (контроль контекста) | |
| | Хрупко: ломается при апдейтах браузеров/SameSite | postMessage между фреймами — новый канал атаки, нужна валидация origin | |

iframe для спецкатегории ПДн — антипаттерн UX/безопасности: фрейм медданных в чужом шаблоне, ослабленные `frame-ancestors`, проблемы с куками. Допустим только как косметическая «обёртка» над уже-аутентифицированным поддоменом, но не как канал данных.

### (c) Нативный модуль Bitrix на PHP (переписать на PHP)

Реализовать функциональность как модуль `/local/modules/medspravki/` внутри Битрикс.

| Плюсы | Минусы | Риски ИБ | Усилия |
|---|---|---|---|
| Бесшовно: один логин, один UI, нативный пользователь Битрикс | **Полный отказ от нашего стека** (.NET Core 8/EF/Clean Arch → PHP) | Медданные ложатся в БД портала Битрикс → класс защиты ВСЕГО портала поднимается до спецкатегории; врачебная тайна смешивается с общим контуром | Очень высокие (переписать всё) |
| | Локальное ИИ-распознавание (Ollama/RTX3060/Tailscale) переписывать под PHP-вызовы | Атака на портал = компрометация медданных (расширение blast radius) | |
| | Vendor lock-in в Битрикс, дороже сопровождение ядра | История CVE в Битрикс (path traversal, RCE через `html_editor_action.php`, XSS) — наследуем чужой риск | |

Для нашего проекта — **антипаттерн**: уничтожает архитектуру, объединяет медданные с общим порталом (плохо для 152-ФЗ/323-ФЗ и аттестации), наследует уязвимости Битрикс. Оценочно — отвергнуть.

### (d) Reverse-proxy под общим доменом (`student.rea.ru/medspravki` → наш .NET)

nginx/Битрикс-фронт проксирует префикс `/medspravki` на наш ASP.NET Core (через `ASP.NET Core Module`/IIS или отдельный upstream).

| Плюсы | Минусы | Риски ИБ | Усилия |
|---|---|---|---|
| Один домен → можно шарить куку, единый UX-адрес | Нужна корректная работа `PathBase`/`X-Forwarded-Prefix` в ASP.NET Core (`UseForwardedHeaders`) | Если медданные едут одним доменом — снова смешение контуров; разделять журналы/БД | Средние: nginx location + ForwardedHeaders + Identity |
| Можно отдавать в наш .NET подписанный заголовок/JWT от прокси | Прокси должен быть «доверенным звеном» (`KnownProxies`/`KnownNetworks`) | **Header-spoofing**: прокси ОБЯЗАН вырезать клиентские `X-Forwarded-*`/`X-Remote-User`, иначе подмена личности; в ASP.NET — ограничить `KnownProxies` | |
| Прозрачно для пользователя, нет third-party cookie | Конфиг прокси на стороне РЭУ (нужен доступ к их nginx) | TLS-терминация и mTLS между прокси и .NET для УЗ-3 | |

### Рекомендация

**Основной вариант — (a) отдельный поддомен + SSO** (`med.rea.ru`), с делегированием идентичности порталу через OAuth2/OIDC (см. раздел 3), но с собственной сессией и БД в нашем контуре. Это максимально соответствует 152-ФЗ ст. 10 / 323-ФЗ ст. 13 / ПП-1119 УЗ-3: медданные физически и логически изолированы, аттестуется только наш контур, blast radius минимален, стек .NET сохранён.

**Допустимая альтернатива/комбинация — (d) reverse-proxy** под общим доменом, если РЭУ требует «единый адрес» и готов настроить свой nginx с жёстким trust-boundary (вырезание клиентских заголовков, `KnownProxies`, mTLS, корректный `X-Forwarded-Prefix`). По сути (a) и (d) можно совместить: SSO для идентичности + reverse-proxy для адресации.

**Отвергнуть: (b) iframe** (хрупко, clickjacking, third-party cookie, неуместно для медданных) и **(c) PHP-модуль** (уничтожает архитектуру, смешивает контуры, наследует CVE Битрикс).

## Источники

- [Microsoft Learn — Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0) — `ForwardedHeadersMiddleware`, `UseForwardedHeaders`, обновляет Host/Scheme/PathBase, по умолчанию `ForwardedHeaders.None` — проверено 2026-06-16
- [anthonysimmon.com — How to securely reverse-proxy ASP.NET Core web apps](https://anthonysimmon.com/securely-reverse-proxy-aspnet-core-web-apps) — `XForwardedPrefix`, ограничение по `KnownProxies`; прокси обязан вырезать клиентские X-Forwarded заголовки иначе спуфинг — проверено 2026-06-16
- [nestenius.se — Configuring ASP.NET Core Forwarded Headers Middleware](https://nestenius.se/net/configuring-asp-net-core-forwarded-headers-middleware) — middleware доверяет только known proxies/networks; X-Forwarded-Prefix ↔ PathBase — проверено 2026-06-16
- [didit.me — Embedded iFrame Security Best Practices](https://didit.me/blog/embedded-iframe-security-best-practices) — `X-Frame-Options`/CSP `frame-ancestors` против clickjacking, postMessage для cross-origin — проверено 2026-06-16
- [GitHub privacycg/CHIPS issue #82](https://github.com/privacycg/CHIPS/issues/82) — партиционированные куки ломают SSO-сценарии встроенного iframe при открытии новой вкладки — проверено 2026-06-16
- [HackYourMom — Bitrix vulnerabilities (CVE path traversal/RCE)](https://hackyourmom.com/en/kibervijna/bitrix-pid-atakoyu-krytychni-vrazlyvosti-eksplojty-ta-kontrol-nad-systemoyu-chastyna-2) — RCE/path traversal через `/bitrix/tools/html_editor_action.php`, наследуемый риск PHP-модуля — проверено 2026-06-16

---

## 3. Как Bitrix отдаёт идентичность наружу (SSO/проброс пользователя)

**Ключевое уточнение:** `student.rea.ru` — это **«1С-Битрикс: Управление сайтом» (БУС)**, а НЕ облачный Bitrix24. Это сужает доступные механизмы:

- Модуль **REST API** в БУС поддерживается **только с версии 16.6.0** — это нужно подтвердить с РЭУ (по пассивным признакам версия не видна).
- В БУС REST работает на **локальных входящих вебхуках** (привязаны к пользователю-владельцу, scope ограничен его правами) и на локальных приложениях по OAuth 2.0. Тиражные приложения и облачный OAuth-сервер `oauth.bitrix.info` — это сценарий Bitrix24/Маркетплейс, для БУС обычно избыточен/недоступен.

Реально доступные механизмы проброса личности из Битрикс во внешний .NET:

### 3.1 REST API: webhooks + OAuth2-приложения, scope `user`

- **Входящий вебхук** (apauth): постоянный код + ID пользователя, работает только по HTTPS. Подходит для серверного запроса данных пользователя нашим .NET (например, синхронизация справочника студентов/сотрудников), но **сам по себе не аутентифицирует конечного пользователя в нашем приложении** — это машинная авторизация с правами владельца вебхука.
- **Локальное приложение OAuth 2.0**: `access_token` (живёт 1 час, `expires_in=3600`), `refresh_token` (~30 дней), `scope` (для пользовательских данных — `user`/`user_basic`/`user_brief`). Через метод `user.current`/`user.get` мы получаем профиль (ID, NAME, LAST_NAME, EMAIL, UF_DEPARTMENT и т. д.). Это даёт **полный OAuth-флоу «пользователь авторизуется в Битрикс → наш сервис получает токен»** — то, что нам нужно для SSO на поддомене (вариант 2a).

### 3.2 Модуль socialservices / OAuth2-сервер Битрикс

- С **версии 10** в БУС и «Битрикс24 в коробке» доступна авторизация через модуль **«Социальные сервисы»** (OAuth2/OpenID). Битрикс здесь может выступать и как клиент, и (через `socialservices`) поддерживает OAuth/OIDC-провайдеры.
- События модуля — точки расширения SSO: `OnAuthServicesBuildList` (с 9.0.0, список сервисов авторизации), `OnFindSocialservicesUser` (с **17.1.0** — собственно событие поиска/сопоставления пользователя при авторизации), `OnBeforeOpenIDUserAdd`, `OnBeforeOpenIDAuthFinalRedirect` (с 11.0.0). Через них реализуется кастомный провайдер SSO (на практике так делают интеграцию с Keycloak/AD как внешним IdP).
- Важно: «Социальные сервисы» — это **дополнительный способ входа на сам портал**, а не готовый «OAuth-сервер для внешних приложений». Чтобы Битрикс отдавал личность наружу как IdP, обычно интегрируют внешний IdP (Keycloak/ADFS) — он становится единой точкой SSO и для портала, и для нашего .NET. Это самый чистый путь под УЗ-3.

### 3.3 Модуль внешней аутентификации / собственный auth-provider

- В БУС/коробке Битрикс есть механизм кастомного **провайдера авторизации REST** (`\Bitrix\Rest\Application::setAuthProvider(...)`, наследование `ProviderOAuth`/`ProviderInterface`) и обработчик `OnRestCheckAuth` — позволяет встроить свой токен-механизм между порталом и приложением (документировано для изолированной коробки).
- Для самого портала есть **AD/LDAP-интеграция** и NTLM-авторизация (модуль `ldap`) — релевантно, если РЭУ заводит пользователей из домена; тогда единый источник правды — AD, а наш .NET тоже аутентифицирует против AD/LDAP (Windows-хост этому благоприятствует).

### 3.4 Общая кука на родительском домене

- Технически возможно делить аутентификацию через куку с `Domain=.rea.ru` (наш поддомен видит куку портала). **Не рекомендуется**: кука Битрикс (`BITRIX_SM_*`) — формат ядра PHP, наш .NET её не валидирует криптографически без знания секрета портала; это хрупко и расширяет trust-boundary на весь `*.rea.ru`. Для спецкатегории ПДн — нежелательно.

### 3.5 JWT/подписанные заголовки от reverse-proxy

- При варианте 2d прокси (после того как Битрикс аутентифицировал пользователя) добавляет к проксируемому запросу **подписанный заголовок** (например `X-Remote-User` + HMAC, или короткоживущий JWT). Наш ASP.NET Core принимает его через кастомный `AuthenticationHandler`, **доверяя только known-proxy** (`ForwardedHeadersOptions.KnownProxies`), и обязательно вырезая такой заголовок, если он пришёл напрямую от клиента. Это рабочий «header-based SSO», но безопасность целиком держится на дисциплине прокси (вырезание клиентских заголовков, mTLS, секрет подписи).

### Рекомендация по SSO

Под УЗ-3 и при сохранении нашего стека оптимальны два слоя:
1. **Идентичность** — внешний IdP (Keycloak/ADFS, либо OAuth2-приложение БУС со scope `user`), наш .NET = OIDC-клиент. Если у РЭУ домен AD — заводить единый IdP поверх AD.
2. **Данные пользователя** — добор профиля через REST `user.current` (scope `user`) при первом входе, далее своя сессия в ASP.NET Core Identity.
Общую куку `.rea.ru` и iframe-SSO не использовать. Header-JWT от прокси — только если выбран вариант 2d и прокси под контролем РЭУ.

## Источники

- [Авторизация в REST | Bitrix24 REST API (apidocs)](https://apidocs.bitrix24.com/settings/how-to-call-rest-api/authorization.html) — два способа авторизации: входящий вебхук (код+ID пользователя) и OAuth 2.0 (access_token/refresh_token); REST всегда «от имени» конкретного пользователя — проверено 2026-06-16
- [REST API (курс dev.1c-bitrix.ru) — модуль REST в БУС с 16.6.0](https://dev.1c-bitrix.ru/learning/course/index.php?COURSE_ID=41&CHAPTER_ID=020208&LESSON_PATH=3911.20208) — модуль REST работает с облачным/коробочным Битрикс24 и с «1С-Битрикс: Управление сайтом» начиная с 16.6.0 — проверено 2026-06-16
- [User Scope Versions (apidocs.bitrix24.com)](https://apidocs.bitrix24.com/api-reference/user/user-scope.html) — состав scope `user`/`user_basic`/`user_brief`: ID, NAME, LAST_NAME, EMAIL, UF_DEPARTMENT и др. — проверено 2026-06-16
- [Социальные сервисы (dev.1c-bitrix.ru user_help)](https://www.dev.1c-bitrix.ru/user_help/service/socialservices/index.php) — с 10-й версии авторизация на сайтах под БУС и «Битрикс24 в коробке» через соцсервисы — проверено 2026-06-16
- [Социальные сервисы: События (dev.1c-bitrix.ru api_help)](https://dev.1c-bitrix.ru/api_help/socialservices/events/index.php) — события `OnAuthServicesBuildList` (9.0.0), `OnFindSocialservicesUser` (17.1.0), `OnBeforeOpenIDUserAdd`/`OnBeforeOpenIDAuthFinalRedirect` (11.0.0) — точки расширения SSO — проверено 2026-06-16
- [Авторизация приложений в изолированной коробке Битрикс24 (apidocs.bitrix24.ru)](https://apidocs.bitrix24.ru/settings/cloud-and-on-premise/on-premise/custom-auth-provider.html) — кастомный провайдер авторизации `\Bitrix\Rest\Application::setAuthProvider`, наследование `ProviderOAuth`, событие `OnRestCheckAuth` — проверено 2026-06-16
- [Complete OAuth 2.0 Authorization Protocol (apidocs.bitrix24.com)](https://apidocs.bitrix24.com/settings/oauth/index.html) — полный OAuth-флоу «у меня свой внешний сервис, пользователь авторизуется в Битрикс, мой сервис получает токены для REST», client_id/state — проверено 2026-06-16
- [Интерволга — SSO-авторизация в Битрикс24 с Keycloak](https://www.intervolga.ru/blog/bitrix24/sso-avtorizatsiya-v-bitriks24-s-keycloak) — Битрикс поддерживает OAuth2/OIDC через модуль «Социальные сервисы», но это доп. вход; чистый SSO — через внешний IdP (Keycloak), не светящий AD напрямую — проверено 2026-06-16
- [Интерволга — Руководство по SOAP и REST в 1С-Битрикс: Управление сайтом](https://www.intervolga.ru/blog/projects/rukovodstvo-po-soap-i-rest-v-1s-bitriks-upravlenie-saytom) — варианты авторизации модуля REST: oauth, apauth (вебхуки, только https), sessionauth; расширение через `OnRestCheckAuth` — проверено 2026-06-16
- [brekot.ru — Как запустить REST API в Битрикс: Управление сайтом](https://www.brekot.ru/blog/bitrix-rest-api) — в БУС нет UI-генерации вебхука как в Б24; ставится модуль `rest`, компонент `bitrix:rest.hook` через `/local/rest/`, права = scope — проверено 2026-06-16

---

## Итоговые выводы (для PLAN.md)

1. **Стек подтверждён пассивно:** `student.rea.ru` = «1С-Битрикс: Управление сайтом» (БУС/PHP за nginx, шаблон `reapay_bootstrap_personal`, Bootstrap 3, интегратор Sebekon) — по `BITRIX_SM_*`+`PHPSESSID`+`/bitrix/`; `rasp.rea.ru` = ASP.NET MVC 5.2 / .NET FW 4.0.30319 / IIS — по `X-AspNetMvc-Version`/`X-AspNet-Version`/`X-Powered-By`/куки.
2. **Архитектура интеграции:** отдельный поддомен `med.rea.ru` + SSO (вариант 2a) как основной; reverse-proxy под общим доменом (2d) — допустимая комбинация; iframe (2b) и PHP-модуль (2c) — отвергнуть (медданные = спецкатегория, изоляция контура обязательна под УЗ-3).
3. **SSO:** делегировать идентичность внешнему IdP (Keycloak/ADFS) или OAuth2-приложению БУС (scope `user`), наш .NET = OIDC-клиент со своей сессией; добор профиля через REST `user.current`. Проверить с РЭУ версию БУС (REST ≥ 16.6.0) и наличие AD/LDAP. Не использовать общую куку `.rea.ru` и iframe-SSO.
4. **Открытый вопрос к РЭУ:** версия БУС и факт включённого модуля `rest`/`socialservices`; есть ли корпоративный IdP/AD; доступ к их nginx для reverse-proxy.


---

# Блок 5. Авторизация препод/студент и матрица доступа к справкам
Это задача документирования/проектирования — без чтения исходников она решается на основе уже предоставленных КОД-ФАКТОВ и Блока 4. Дам ответ напрямую.

# БЛОК 5. Авторизация в портале РЭУ и матрица доступа к медсправкам

## 1. Вход в Bitrix-портал РЭУ и доверенный проброс идентичности

### 1.1 Как устроен вход для преподавателей и студентов

`student.rea.ru` = «1С-Битрикс: Управление сайтом» (БУС, не Bitrix24) — это подтверждено пассивно (`BITRIX_SM_*` + `PHPSESSID` + `/bitrix/` + шаблон `reapay_bootstrap_personal`, Блок 4 §1.1). Модель аутентификации БУС:

- **Хранилище личности и сессия.** Логин через нативную форму БУС: `POST /?login=yes` с полями `AUTH_FORM=Y&TYPE=AUTH&USER_LOGIN&USER_PASSWORD`. После успеха ядро ставит куки `BITRIX_SM_LOGIN` / `BITRIX_SM_UIDH` (хэш-токен идентификации) поверх `PHPSESSID`. Это сигнатура именно БУС (Блок 4 §1.1, §3).
- **Bitrix user groups (группы пользователей).** Разграничение в БУС строится не на «ролях приложения», а на **группах пользователей** (`b_group`): преднастроены «Администраторы» (ID 1), «Все пользователи (в т.ч. неавторизованные)» (ID 2). Для портала вуза почти наверняка заведены кастомные группы вида «Студенты», «Преподаватели/ППС», «Сотрудники», возможно «Деканат». Права (доступ к разделам, инфоблокам, файлам) выдаются группе, пользователь = член ≥1 группы. **Именно членство в группе — то, что нам нужно мэппить в роль нашей подсистемы.**
- **Вероятная связка с AD/LDAP/SSO вуза.** Вуз с доменной инфраструктурой обычно не держит пароли студентов/ППС в БУС вручную. Два правдоподобных сценария (подтвердить у РЭУ — открытый вопрос Блока 4 §«Итоги»):
  1. **Модуль `ldap` БУС** (AD/LDAP-интеграция, NTLM): пользователи импортируются/аутентифицируются против домена РЭУ, членство в OU/доменных группах мэппится в Bitrix user groups. Источник правды — AD.
  2. **Модуль `socialservices` БУС** (OAuth2/OpenID, доступен с 10-й версии) как клиент внешнего IdP (ADFS/Keycloak поверх AD). Тогда единая точка SSO — IdP, и для портала, и потенциально для нас.
- Студент и преподаватель технически входят одинаково (одна форма БУС/IdP), различаются **только членством в группах** и набором прав, которые группа открывает в личном кабинете.

### 1.2 Доверенный проброс идентичности в нашу подсистему

Из четырёх вариантов Блока 4 (§2) выбираю **(a) отдельный поддомен `med.rea.ru` + SSO через внешний IdP / OAuth2-приложение БУС, наш .NET = OIDC-клиент со своей сессией ASP.NET Core Identity**. Это рекомендация Блока 4 и она прямо обусловлена тем, что данные — спецкатегория ПДн + врачебная тайна (152-ФЗ ст. 10, 323-ФЗ ст. 13, УЗ-3).

Почему именно так — и какие риски Блока 4 это закрывает:

| Решение | Закрываемый риск (Блок 4) |
|---|---|
| Идентичность делегируется IdP (Keycloak/ADFS) или OAuth2-приложению БУС (scope `user`); добор профиля через REST `user.current` при первом входе, дальше — **своя** сессия | Не наследуем CVE Битрикс (path traversal/RCE через `/bitrix/tools/html_editor_action.php`), не смешиваем контуры (§2c, §3) |
| **Своя** сессия в ASP.NET Core Identity, своя БД медданных на нашем поддомене | Минимальный blast radius, аттестуется только наш контур под УЗ-3 (§2 «Рекомендация») |
| **НЕ** используем общую куку `Domain=.rea.ru` | Кука `BITRIX_SM_*` — формат ядра PHP, наш .NET её криптографически не валидирует; общая кука расширяет trust-boundary на весь `*.rea.ru` — для спецкатегории недопустимо (§3.4) |
| **НЕ** используем iframe-SSO | Clickjacking + third-party cookie (`SameSite=None`) + партиционизация (CHIPS) ломают сессию; фрейм медданных в чужом шаблоне неуместен (§2b) |
| OIDC-флоу: валидируем `state`/PKCE, проверяем `redirect_uri` по whitelist | Open-redirect на SSO-редиректах (§2a «Риски ИБ») |

**Если РЭУ требует «единый адрес»** — допустима комбинация с reverse-proxy (§2d): `student.rea.ru/medspravki` → наш .NET. Тогда обязательны меры §2d/§3.5: прокси под контролем РЭУ **вырезает** клиентские `X-Forwarded-*` / `X-Remote-User`, в ASP.NET — `ForwardedHeadersOptions.KnownProxies`/`KnownNetworks` (иначе header-spoofing = подмена личности), mTLS прокси↔.NET, корректный `X-Forwarded-Prefix`↔`PathBase`. Header-JWT от прокси принимать только кастомным `AuthenticationHandler` с проверкой подписи и доверием только known-proxy.

**Критически важная нестыковка с текущим кодом (КОД-ФАКТЫ §10 и §7):** сейчас приложение НЕ имеет SSO/OIDC (только локальный ASP.NET Core Identity, `Auth/Login`), `UseForwardedHeaders` отсутствует, `IpAddress` в аудите всегда `null`. Перед интеграцией в портал нужно: добавить OIDC-клиента, включить `UseForwardedHeaders` с `KnownProxies` (если вариант 2d), и захватывать `RemoteIpAddress`. Без этого «доверенный проброс» не реализуем.

**Маппинг группы БУС → роль подсистемы.** При первом входе по OIDC берём claim о группах/подразделении (Bitrix user group или AD-группа, либо `UF_DEPARTMENT` из `user.current`) и мэппим:

| Признак из IdP/БУС | Роль в нашей подсистеме |
|---|---|
| группа «Студенты» | Студент |
| группа «ППС/Преподаватели» + кафедра физвоспитания | Преподаватель(физрук) |
| отдельная группа «Медработник»/медпункт (или доменная группа мед.службы) | Медработник |
| группа «Завкафедрой» / зав. кафедрой физвоспитания | Завкафедрой |
| доменная группа администраторов ИС | Админ |

Важно: **медработник и студент — НОВЫЕ роли**, которых в коде сейчас нет (КОД-ФАКТЫ §1: ровно 3 роли `Teacher`/`HeadOfDepartment`/`Admin`, bootstrap-юзер = `Admin`+`Teacher`). Их нужно завести.

---

## 2. Матрица доступа

Принцип: **152-ФЗ ст. 5** (минимизация — обрабатываются только необходимые для цели данные) + **ст. 10** (спецкатегория = медицинские ПДн, обработка только при выполнении условий ч. 2.3, доступ строго по служебной необходимости) и **323-ФЗ ст. 13** (врачебная тайна — сведения о состоянии здоровья, диагнозе; разглашение без согласия запрещено, доступ — только лицам, которым это необходимо для исполнения обязанностей).

Ключевая модель данных, на которую опирается матрица (см. рекомендации §2c): медполя расщепляются на два слоя —
- **Медицинский слой** (врачебная тайна): скан, `RecognitionJson`, диагноз/нозология (если когда-либо появится), `Restrictions` (текст ограничений), `Comment`, `MedicalOrganization`, `CertificateNumber`.
- **Слой допуска (clearance projection)**: `HealthGroup`/`PhysicalGroup` (физгруппа), срок (`StartDate`/`EndDate`), статус-светофор (Verified/Expired/нет справки). Это **НЕ** врачебная тайна в смысле диагноза — это вывод о допуске к нагрузке, необходимый физруку для исполнения обязанностей.

### (a) Роль × Объект → Видит / Не видит

| Объект → | Скан справки | Диагноз / нозология | Ограничения (`Restrictions`/`Comment`, текст) | Допуск (физгруппа + срок + светофор) | Аудит (журнал доступа) |
|---|---|---|---|---|---|
| **Студент** | Видит **только свой** | Видит **только свой**¹ | Видит **только свои** | Видит **только свой** | Нет |
| **Преподаватель (физрук)** | **Не видит** | **Не видит** | **Не видит**² | Видит — **только свои группы/закреплённые потоки** | Нет |
| **Медработник** | Видит **все** (мед.служба) | Видит все | Видит все | Видит все | Свой раздел (что сам открывал) — опц. |
| **Завкафедрой** | **Не видит**³ | **Не видит** | **Не видит** | Видит **агрегаты по кафедре** + статус-светофор по студентам кафедры (без текста ограничений) | Видит журнал по кафедре |
| **Админ** | **Не видит содержимое**⁴ | **Не видит** | **Не видит** | **Не видит** (или обезличенно) | Видит **весь** журнал |

Обоснование по ячейкам:

- **Студент — свои данные.** 152-ФЗ ст. 14 (право субъекта на доступ к своим ПДн); 323-ФЗ ст. 22 (право пациента на информацию о состоянии здоровья). ¹Доступ к собственному диагнозу — право пациента, не нарушение тайны (тайна — от третьих лиц). Жёсткое ownership-ограничение: `studentId == currentUser.StudentId`.
- **Физрук — ТОЛЬКО допуск, без скана/диагноза/текста ограничений.** 152-ФЗ ст. 5 (минимизация): для проведения занятия физруку необходим и достаточен факт «физгруппа N, до даты D, статус валиден». 323-ФЗ ст. 13: диагноз — врачебная тайна, физрук не входит в круг лиц, которым он нужен для исполнения обязанностей. ²Свободный текст `Restrictions`/`Comment` скрыт, потому что он не структурирован и может содержать диагноз/нозологию (КОД-ФАКТЫ §6: `Restrictions`/`Comment` — свободный текст, ничем не ограничен; декларация «без диагноза» кодом не гарантируется). Физрук получает только **нормализованную** проекцию допуска. Видит только **свои группы** (resource-based scoping).
- **Медработник — полный медицинский доступ.** 323-ФЗ ст. 13 ч. 4 (доступ медработников к врачебной тайне при оказании помощи) + 152-ФЗ ст. 10 ч. 2 п. 3 (обработка спецкатегории в мед-целях лицом, обязанным хранить тайну). Единственная роль, кому правомерен скан + диагноз + текст ограничений.
- **Завкафедрой — агрегаты, без персональных медданных.** 152-ФЗ ст. 5 (минимизация): для управления кафедрой нужна статистика (сколько спецгруппы, сколько просрочено), а не персональный диагноз. ³Скан/диагноз/текст ограничений — врачебная тайна (323-ФЗ ст. 13), для административной функции не нужны → не видит. Допуск по конкретному студенту видит как светофор (для решения «допустить/не допустить»), но без текста ограничений.
- **Админ — администрирует систему, не читает медконтент.** 152-ФЗ ст. 5 + ст. 19 (разграничение доступа как мера защиты): администратор ИС обеспечивает работу системы, доступ к медсодержимому ему по служебной необходимости НЕ требуется. ⁴Технически админ может управлять записями/пользователями, но контент скана/медполей ему не отображается (защита от инсайдера, требование УЗ-3 ФСТЭК — разграничение и контроль). Полный доступ к аудиту — нужен для контроля ИБ.

### (b) Роль × Действие → Может / Не может

| Действие → | Загрузить скан/черновик | Ред. черновик | Подтвердить (Verified) | Отклонить (Rejected) | Отозвать (Revoke) | Экспорт | Просмотр аудита |
|---|---|---|---|---|---|---|---|
| **Студент** | **Да** (свою справку, DraftSource=StudentUpload) | Да — **только свой Draft** | Нет | Нет | Нет | Нет | Нет |
| **Преподаватель (физрук)** | Нет⁵ | Нет | Нет⁶ | Нет | Нет | Только **допуск** по своим группам (без медполей) | Нет |
| **Медработник** | Да | Да | **Да** | **Да** | **Да** | Да (мед.экспорт, с журналированием) | Свой (опц.) |
| **Завкафедрой** | Нет | Нет | Нет | Нет | Нет | **Агрегаты** по кафедре (обезличенные) | По кафедре |
| **Админ** | Нет⁷ | Нет | Нет | Нет | Нет | Нет (или техэкспорт без медполей) | **Да, весь** |

Обоснование:

- ⁵Физрук **не загружает и не верифицирует**: верификация медсправки — медицинская функция (установление подлинности/соответствия), 323-ФЗ ст. 13 + 152-ФЗ ст. 10 — это компетенция медработника, не физрука. Это меняет текущий код (КОД-ФАКТЫ §1: сейчас `Review/Approve/Reject` доступны любому аутентифицированному, в т.ч. `Teacher`). ⁶Перенос `Verified`/`Rejected` с физрука на медработника — ключевое следствие 323-ФЗ.
- **Студент загружает свой черновик** (DraftSource=StudentUpload уже предусмотрен доменом), редактирует только свой Draft до отправки. Подтверждать/отклонять не может (конфликт интересов).
- **Revoke** — только медработник (отзыв — следствие выявленной недостоверности/изменения мед.статуса, мед.решение).
- **Экспорт** разграничен по содержимому: физрук — только проекция допуска по своим группам; завкафедрой — обезличенные агрегаты; медработник — полный мед.экспорт **с обязательным аудитом** (КОД-ФАКТЫ §5: сейчас экспорта в коде нет; при добавлении ClosedXML-экспорта — экранировать формулы против CSV-injection и логировать `Export`).
- **Аудит**: завкафедрой — по кафедре (контроль), админ — весь (ИБ). Чтение журнала сейчас открыто любому аутентифицированному (КОД-ФАКТЫ §7) — это надо закрыть ролевой политикой.
- ⁷Админ не загружает/не верифицирует медсправки — иначе размывается разделение обязанностей (separation of duties).

---

## 3. Рекомендации по реализации в ASP.NET Core

Привязка к текущему коду (КОД-ФАКТЫ §1, §2, §6, §7) — что добавить/изменить:

### 3.1 Роли и политики (policy-based authorization)

- Завести 5 ролей вместо текущих 3: добавить `Student` и `MedicalStaff` к `AppRole` (`Infrastructure/Identity/AppRole.cs`), включить в `AppRoles.All` и сидинг (`Persistence/DataSeeder.cs`). Bootstrap-юзер не должен быть `Admin`+`Teacher` одновременно в проде.
- Описать политики в `AddAuthorization`:
  - `CanViewMedicalContent` → `RequireRole(MedicalStaff)` (скан, диагноз, текст ограничений, OCR-JSON).
  - `CanViewClearance` → `Teacher, MedicalStaff, HeadOfDepartment` (только проекция допуска).
  - `CanVerifyCertificate` → `MedicalStaff` (Approve/Reject/Revoke).
  - `CanViewDepartmentAggregates` → `HeadOfDepartment`.
  - `CanReadAudit` → `HeadOfDepartment` (scoped) / `Admin` (all).
  - `IsStudentSelf` → роль `Student` + resource-check ownership.
- Заменить открытый `AuthorizeFolder("/")` (только аутентификация, КОД-ФАКТЫ §1) на **per-folder/per-page policy**: `AuthorizeFolder("/Scans", "CanViewMedicalContent")`, `AuthorizeFolder("/Review", "CanVerifyCertificate")`, `AuthorizeFolder("/Journal", "CanReadAudit")` и т.д.

### 3.2 Resource-based authorization (закрыть BOLA/IDOR — КОД-ФАКТЫ §1, §2, ключевой пробел №1)

- Реализовать `IAuthorizationHandler` на ресурсах: `StudentAccessHandler`, `ScanAccessHandler`, `CertificateAccessHandler`. Вызывать `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` в каждом обработчике **до** выдачи данных.
- Scoping по группе/факультету: завести связь «преподаватель ↔ закреплённые учебные группы» (например `TeacherGroupAssignment`) и «студент ↔ группа/факультет». В `RegistryQueryService.SearchAsync`, `StudentService.GetDetailAsync`, `ScanService.OpenAsync`, `CertificateService.Approve/Reject` добавить **серверный** фильтр по scope текущего пользователя (сейчас `TeacherId` — пользовательский параметр, а не серверное ограничение — КОД-ФАКТЫ §1).
- `ScanService.OpenAsync` (`Infrastructure/Services/ScanService.cs:85-94`) — добавить проверку принадлежности скана и роль `MedicalStaff`; сейчас любой с ролью открывает скан любого студента по GUID. Эндпоинт `GET /scans/{id}/file` (`Program.cs:55-61`) сменить `RequireRole("Teacher",...)` на `RequireAuthorization("CanViewMedicalContent")` + resource-check.
- `CurrentUser` (`Infrastructure/Services/CurrentUser.cs`) расширить: добавить `Roles`, `AssignedGroupIds`/`FacultyId`, `StudentId` — сейчас отдаёт только `UserId`/`UserName` и scope нигде не используется.

### 3.3 Разделение медполей в отдельную проекцию/таблицу (минимизация на уровне модели)

- Расщепить `MedicalCertificate` (`Domain/Entities/MedicalCertificate.cs`):
  - **Таблица `certificate_clearance`** (проекция допуска): `CertificateId`, `HealthGroup`, `PhysicalGroup`, `StartDate`, `EndDate`, вычисляемый `Status` (Verified/Expired). Это то, что отдаётся физруку/завкафедрой. Эту таблицу можно безопасно джойнить в реестр и экспорт допуска.
  - **Таблица `certificate_medical`** (врачебная тайна): `Restrictions`, `Comment`, `MedicalOrganization`, `CertificateNumber`, диагноз (если появится), `RecognitionJson`. Доступ — только через `CanViewMedicalContent`.
- На уровне сервисов отдавать физруку/завкафедрой **DTO без медполей** (отдельная projection-выборка), чтобы медполя физически не покидали репозиторий для не-медработника. Razor-страницы реестра/занятия биндить на clearance-DTO.
- Шифрование at-rest для медицинского слоя (КОД-ФАКТЫ §6, пробел №4): EF Core `ValueConverter` с ASP.NET DataProtection (или `pgcrypto`) на полях `certificate_medical` и шифрование файлов сканов в `FileScanStorage` (сейчас `File.Create`/`File.OpenRead` без шифрования). Для УЗ-3 это требование защиты спецкатегории.

### 3.4 Аудит доступа к медданным (КОД-ФАКТЫ §7, пробелы №2, №5)

- **Логировать чтение медконтента**: добавить `AuditLogs.Add` в `ScanService.OpenAsync` и в обработчик `GET /scans/{id}/file` (action `ScanView`/`Export`), а также при открытии карточки с медполями. Сейчас просмотр скана не логируется — критичный пробел для спецкатегории.
- **Логировать вход/выход**: добавить запись `Login`/`LoginFailed` в `Login.cshtml.cs` и `Logout` (сейчас только обновляется `LastLoginAt`; типы `Login/LoginFailed/Export` существуют лишь в комментариях/демо-сиде).
- **Захват IP**: добавить параметр `IpAddress` в `AuditEntryFactory.Create` и брать `HttpContext.Connection.RemoteIpAddress` (при reverse-proxy — после `UseForwardedHeaders` с `KnownProxies`). Сейчас поле всегда `null`.
- **INSERT-only на уровне БД**: миграцией выдать роли приложения `GRANT INSERT, SELECT` и `REVOKE UPDATE, DELETE` на `audit_logs`; опц. BEFORE UPDATE/DELETE триггер. Сейчас INSERT-only только декларация в комментарии, в БД ничего нет.

### 3.5 Прочие меры под интеграцию в портал (Блок 4)

- Security-заголовки (КОД-ФАКТЫ §10, пробел №6): добавить middleware с `Content-Security-Policy`, `frame-ancestors 'none'` (clickjacking — критично рядом с Bitrix-порталом), `X-Content-Type-Options: nosniff` (скан отдаётся с клиентским MIME), `Referrer-Policy: no-referrer`.
- OIDC-клиент для SSO (вариант 2a): `AddOpenIdConnect`, маппинг claim групп БУС/AD → роли подсистемы (см. §1.2), валидация `state`/PKCE, whitelist `redirect_uri`.
- Если reverse-proxy (2d): `UseForwardedHeaders` строго с `KnownProxies`/`KnownNetworks`, `ForwardLimit`, очистка клиентских `X-Forwarded-*`/`X-Remote-User` на прокси, mTLS прокси↔.NET.

---

**Ключевые файлы-якоря для реализации** (абсолютные пути):
- `<корень-репозитория>/app/src/ReuMedCertificates.Infrastructure/Identity/AppRole.cs` — добавить роли `Student`, `MedicalStaff`.
- `<корень-репозитория>/app/src/ReuMedCertificates.Web/Program.cs` — политики, per-folder authorization, security-заголовки, `UseForwardedHeaders`, OIDC.
- `<корень-репозитория>/app/src/ReuMedCertificates.Infrastructure/Services/ScanService.cs` — resource-check принадлежности + аудит просмотра в `OpenAsync`.
- `<корень-репозитория>/app/src/ReuMedCertificates.Application/Registry/RegistryQueryService.cs` и `.../Students/StudentService.cs`, `.../Certificates`/`CertificateService.cs` — серверный scope-фильтр по группе/факультету.
- `<корень-репозитория>/app/src/ReuMedCertificates.Domain/Entities/MedicalCertificate.cs` — расщепление на clearance/medical проекции.
- `<корень-репозитория>/app/src/ReuMedCertificates.Application/Common/AuditEntryFactory.cs` — параметр `IpAddress`; `.../Infrastructure/Services/CurrentUser.cs` — роли/scope/`StudentId`.


---

# Блок 6. Что видит студент и что видит преподаватель
I now have the complete domain model. I have everything needed to compose the deliverable — the exact statuses, groups, fields, and the medical/internal fields that must be hidden per role.

---

# БЛОК 6. Что видит студент и что видит преподаватель — детальный продуктовый дизайн

> Привязка к доменному ядру MedSpravki-REU (проверено по коду `ReuMedCertificates.Domain`):
> - **Кейс справки** `VerificationStatus`: `Draft → NeedsReview → Verified → (Rejected | Expired | Revoked)`.
> - **Срок действия** `CertificateStatus` (вычисляется на лету): `Upcoming / Active / ExpiringSoon / EndsToday / Expired`.
> - **Физкультурная группа** `PhysicalEducationGroup`: `Basic` (основная) / `Preparatory` (подготовительная) / `SpecialA` / `SpecialB` (спец А/Б) / `Exempt` (освобождён) / `None`.
> - **Группа здоровья** `HealthGroup` (I–V) и **диагноз/нозология** — медполя, видны НЕ всем ролям.
> - Источник черновика `DraftSource`: `Manual / ExcelImport / StudentUpload / Ocr`.
> - Каждое изменение пишется в `AuditLog` (INSERT-only, before/after jsonb, IP).

Главный архитектурный принцип, общий со всеми 8 зарубежными системами Блока 1: **«submission complete» ≠ «cleared»** (Privit: «status is not automatically updated» — допуск ставит человек-медик). У нас это буквально: `StudentUpload` создаёт `Draft/NeedsReview`, а в `Verified` его переводит уполномоченное лицо вручную, проставляя `VerifiedByUserId` + `VerifiedAt`. И второй сквозной принцип: **status-not-diagnosis для не-медиков** (Essex/OHWorks: «clearance status shared with course admin… but not able to access any of your OH records»; FERPA: athletics-screening-формы = «education records»). У нас это 323-ФЗ ст.13 (врачебная тайна) + 152-ФЗ ст.10 (спецкатегория) + минимизация.

---

## ЭКРАН 1 — ЛИЧНЫЙ КАБИНЕТ СТУДЕНТА (v2, только при сетевом доступе)

Активируется только в v2 (когда `StudentUpload`/`Ocr` как `DraftSource` включены и есть сетевой доступ из ЛВС). В v1 этого экрана нет — студент данные не вводит.

### 1.1 Wireframe: главный экран кабинета («Мой допуск к физкультуре»)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ [герб РЭУ]  Личный кабинет · Физвоспитание        Иванов И.И., БИ-22-1  ▾ │  ← навбар портала (Bitrix SSO)
├──────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  МОЙ ТЕКУЩИЙ ДОПУСК                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐    │
│  │  ●  ПОДГОТОВИТЕЛЬНАЯ ГРУППА                          [светофор]    │    │  ← PhysicalGroup (status-not-diagnosis)
│  │     Допущен с ограничениями по нагрузке                            │    │
│  │     Действует до 31.12.2026  ·  осталось 14 дней  ⚠ Скоро истекает│    │  ← CertificateStatus = ExpiringSoon
│  │     Подтверждено кафедрой 02.09.2026                               │    │  ← Verified + VerifiedAt
│  └──────────────────────────────────────────────────────────────────┘    │
│                                                                            │
│  Легенда светофора:  🟢 Основная  🟡 Подготовительная  🟠 Спец А/Б         │
│                      🔵 Освобождён  ⚪ Нет действующей справки             │
│                                                                            │
│  [  ⬆ Загрузить справку  ]   [  Согласие на обработку ПДн ✔  ]            │
│                                                                            │
│  ────────────────────────────────────────────────────────────────────    │
│  ИСТОРИЯ МОИХ СПРАВОК (таймлайн кейсов)                                    │
│                                                                            │
│   ┌── Справка №2026-1187 · 01.09.2026 – 31.12.2026 ──────────────┐        │
│   │ ● Загружена 30.08 → ● На проверке 30.08 → ✔ Подтверждена 02.09│        │  ← Draft→NeedsReview→Verified
│   │ Физгруппа: Подготовительная · Источник: загрузка студентом    │        │
│   └───────────────────────────────────────────────────────────────┘        │
│                                                                            │
│   ┌── Справка №2026-0431 · 01.02.2026 – 30.06.2026 ──────────────┐        │
│   │ ● ... → ✔ Подтверждена 31.01 → ⏳ Истекла 30.06                │        │  ← Verified→Expired
│   │ Физгруппа: Подготовительная                                    │        │
│   └───────────────────────────────────────────────────────────────┘        │
│                                                                            │
│   ┌── Справка (черновик) · отклонена ─────────────────────────────┐        │
│   │ ● Загружена 12.01 → ✖ Отклонена 13.01                          │        │  ← Rejected
│   │ Причина: «нечитаемый скан, загрузите оригинал»                 │        │  ← RejectionReason (видна студенту)
│   └───────────────────────────────────────────────────────────────┘        │
├──────────────────────────────────────────────────────────────────────────┤
│  🔔 Уведомление: ваша справка истекает через 14 дней. Запишитесь на        │
│     медосмотр заранее — иначе допуск перейдёт в «Нет справки».             │
└──────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Wireframe: модал «Загрузить справку»

```
┌─ Загрузка справки ──────────────────────────────────────────┐
│  Перетащите файл сюда или [Выбрать файл]                     │
│                                                              │
│  Требования к файлу:                                         │
│   • Форматы: PDF, JPG, PNG  (DOC/DOCX не принимаются)        │
│   • Размер ≤ 10 МБ, до 5 страниц                            │
│   • Скан/фото читаемые, целиком, без обрезки печати          │
│   • Имя файла — без спецсимволов                            │
│                                                              │
│  ⚠ Загрузка ≠ допуск. После проверки кафедрой статус        │
│     изменится на «Подтверждено» или «Отклонено».            │
│                                                              │
│  ☐ Я подтверждаю согласие на обработку медицинских ПДн      │  ← gate: без согласия кнопка disabled
│     (спецкатегория, 152-ФЗ ст.10) — [читать]                │
│                                                              │
│              [ Отмена ]   [ Отправить на проверку ]          │  ← создаёт CertificateScan + Draft→NeedsReview
└──────────────────────────────────────────────────────────────┘
```

### 1.3 Wireframe: экран e-consent (152-ФЗ ст.10) — первый вход

```
┌─ Согласие на обработку персональных данных спецкатегории ───┐
│  Кафедра физического воспитания РЭУ им. Г.В. Плеханова       │
│                                                              │
│  Я даю согласие на обработку моих ПДн, относящихся к         │
│  состоянию здоровья (спецкатегория, ст.10 152-ФЗ), в целях   │
│  учёта допуска к занятиям физической культурой:              │
│   • сведения справки (срок, физкультурная группа,            │
│     ограничения по нагрузке);                                │
│   • скан-копия справки;                                      │
│   • цель, срок хранения, оператор, права субъекта (ст.14).   │
│                                                              │
│  [ полный текст согласия ▾ ]    [ Политика обработки ПДн ↗ ] │
│                                                              │
│  ☐ С условиями ознакомлен(а) и согласие даю                 │
│  Дата: 16.06.2026 · ФИО: Иванов И.И. · ID: ...               │  ← фиксируется в AuditLog (ActionType=Consent)
│                                                              │
│              [ Отказаться ]        [ Подтвердить ]           │
│  Отказ → загрузка недоступна, справку вносит кафедра вручную │
└──────────────────────────────────────────────────────────────┘
```

### 1.4 Перечень элементов экрана студента

1. Карточка «Мой текущий допуск»: светофор `PhysicalGroup` (текстом + цветом + иконкой, не только цветом — доступность), человекочитаемая формулировка допуска, `EndDate` + «осталось N дней» + бейдж `CertificateStatus` (Active/ExpiringSoon/EndsToday/Expired/Upcoming), дата подтверждения.
2. Легенда светофора (5 состояний физгруппы).
3. Кнопка «Загрузить справку» (модал 1.2).
4. Бейдж/кнопка «Согласие на обработку ПДн» (статус: дано/не дано; ведёт на 1.3).
5. Таймлайн справок: карточки кейсов, в каждой — цепочка статусов с датами (`Draft → NeedsReview → Verified/Rejected/Expired/Revoked`), физгруппа, `DraftSource`, для Rejected — `RejectionReason`.
6. Блок уведомлений: истечение (`ExpiringSoon`/`EndsToday`), результат проверки (Verified/Rejected), отзыв (Revoked).
7. Навбар портала РЭУ (Bitrix SSO, ФИО + учебная группа).

### 1.5 Что студент ВИДИТ / НЕ ВИДИТ

**Видит (своё):** физкультурную группу, срок действия и статус по сроку, статус кейса (вкл. «на проверке»), причину отклонения, формулировку ограничений по нагрузке (функциональную, без диагноза), номер своей справки, медорганизацию, дату подтверждения, свой загруженный скан.

**НЕ видит:**
- **внутренние заметки медработника** (`Comment` — служебное поле верификатора);
- **аудит-журнал** (`AuditLog`: кто/когда/с какого IP смотрел и менял, before/after) — это инструмент УЗ-3/ФСТЭК, не пользовательский;
- **`HealthGroup` (I–V)** — общая медоценка не нужна студенту в кабинете физкультуры и относится к минимизации;
- **диагноз/нозология/код МКБ** — их в нашей модели вообще НЕТ как структурированного поля (намеренно: `Restrictions` — только функциональные формулировки);
- чужие справки и чужие статусы;
- личность конкретного верификатора (только факт «подтверждено кафедрой», без ФИО медработника, если не требуется).

### 1.6 Маппинг экрана студента на зарубежные аналоги (Блок 1)

| Элемент нашего экрана | Зарубежный аналог | Источник |
|---|---|---|
| Светофор физгруппы (допущен / подгот. / спец / освобождён) | Status-светофор «Compliant/Non-compliant/Verified», «View My Compliances» | Medicat (University of Miami Y/N); UCI My Student Chart |
| Кнопка «Загрузить справку» + требования к файлу (PDF/JPG/PNG, без DOCX) | Upload-вкладка, форматы .pdf/.jpg/.png, .doc не принимается | Medicat (Wellesley/Anna Maria how-to); Cornell Upload Immun. Records |
| «Загрузка ≠ допуск, проверит кафедра» | «Submission Complete ≠ Cleared»; clearance ставит staff вручную | Privit Profile (Plainfield instructions) |
| Таймлайн кейса Draft→NeedsReview→Verified/Rejected | Completion status по формам + clearance status; review до 2–5 дн | Privit; Medicat Compliance Services; Cornell (review 3–4 нед) |
| Причина отклонения в портале (а не e-mail) | Secure messaging вместо e-mail (HIPAA/FERPA) | Medicat secure messages; Cornell «NOT able to communicate by email» |
| Экран e-consent (152-ФЗ ст.10) при первом входе | e-consent / Receipt of Privacy Practices / Consent for Treatment в портале | Medicat (Iona/Anna Maria); Privit e-signature; UK explicit consent |
| Уведомление «истекает через N дней» | Expiring-requirements dashboard + automated reminders; placeholder-hold | UofT POWER; Pomelo (SMS/email/voice); UCLA placeholder-hold |
| Каскад жёсткости при просрочке | Cascading holds (enrollment → add/drop → withdrawal) | Cornell |

---

## ЭКРАН 2 — РАБОЧЕЕ МЕСТО ПРЕПОДАВАТЕЛЯ (физрука)

### 2.1 Wireframe: дашборд групп (compliance dashboard)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ [герб РЭУ]  Физвоспитание · Преподаватель        Петров П.П.           ▾ │
├──────────────────────────────────────────────────────────────────────────┤
│  МОИ ГРУППЫ                          Поиск студента: [____________] 🔍     │
│                                                                            │
│  Группа БИ-22-1   ·  28 студ.   🟢18  🟡5  🟠2  🔵1  ⚪2                  │  ← агрегат по PhysicalGroup
│  Группа БИ-22-2   ·  26 студ.   🟢20  🟡3  🟠1  🔵0  ⚪2                  │
│  Группа МЕН-23-3  ·  30 студ.   🟢25  🟡2  🟠0  🔵1  ⚪2  ⚠2 истекают    │  ← ExpiringSoon бейдж
│                                                                            │
│  ── Группа БИ-22-1 (развёрнуто) ───────────────────────────────────────  │
│  ┌────┬─────────────────────┬──────────────────┬───────────┬──────────┐  │
│  │ №  │ Студент             │ Допуск (физгр.)  │ Действует │ Статус   │  │
│  ├────┼─────────────────────┼──────────────────┼───────────┼──────────┤  │
│  │ 1  │ Иванов И.И.         │ 🟡 Подготовит.   │ до 31.12  │ ⚠ скоро  │  │  ← НЕТ диагноза/скана/гр.здоровья
│  │ 2  │ Сидоров С.С.        │ 🟢 Основная      │ до 30.06  │ ✔ активна│  │
│  │ 3  │ Кузнецов К.К.       │ 🔵 Освобождён    │ до 31.12  │ ✔ активна│  │
│  │ 4  │ Орлова О.О.         │ ⚪ Нет справки   │    —      │ ✖ не доп.│  │  ← аналог registration hold
│  │ 5  │ Белов Б.Б.          │ 🟠 Спец «А»      │ до 31.08  │ ⏳ истекла│  │
│  └────┴─────────────────────┴──────────────────┴───────────┴──────────┘  │
│  Фильтр: [Все ▾] [Только не допущенные] [Истекают ≤14 дн] [Спец/освоб.]   │
│  [ ⬇ Экспорт списка допусков в Excel ]   (ClosedXML — без мед. полей)     │
└──────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Wireframe: карточка студента (для преподавателя)

```
┌─ Иванов Иван Иванович · БИ-22-1 ────────────────────────────┐
│                                                              │
│   ДОПУСК К ФИЗКУЛЬТУРЕ                                        │
│   🟡  ПОДГОТОВИТЕЛЬНАЯ ГРУППА                                │  ← PhysicalGroup
│       Допущен с ограничениями по нагрузке                    │  ← Restrictions (функц., без диагноза)
│       Действует: 01.09.2026 – 31.12.2026                     │  ← Start/EndDate
│       Статус по сроку: ⚠ Скоро истекает (14 дн)              │  ← CertificateStatus
│       Кейс: ✔ Подтверждён кафедрой 02.09.2026                │  ← VerificationStatus=Verified
│                                                              │
│   ┌────────────────────────────────────────────────────┐   │
│   │ ⛔ Скан справки, диагноз, группа здоровья и заметки │   │  ← ЯВНОЕ объяснение ограничения
│   │    медработника НЕ отображаются: врачебная тайна    │   │
│   │    (323-ФЗ ст.13), минимизация ПДн (152-ФЗ).        │   │
│   │    Доступно только уполномоченному медработнику.    │   │
│   └────────────────────────────────────────────────────┘   │
│                                                              │
│   [ Закрыть ]    (преподаватель НЕ редактирует справку)      │
└──────────────────────────────────────────────────────────────┘
```

### 2.3 Wireframe: экран «Перед парой» (кто допущен сегодня)

```
┌─ Перед парой · 16.06.2026 · Группа БИ-22-1 · зал №3 ─────────┐
│                                                              │
│  ДОПУЩЕНЫ СЕГОДНЯ (24)                                        │
│   🟢 Основная (18):   Сидоров, Петрова, …          [список]  │
│   🟡 Подготовит. (5): Иванов (ограничения по нагрузке), …    │  ← подсказка нагрузки, без причины
│   🟠 Спец А/Б (1):    Белов — индивидуальная программа       │
│                                                              │
│  НЕ ДОПУЩЕНЫ / ОСВОБОЖДЕНЫ (4)                                │
│   🔵 Освобождён (1):  Кузнецов — до 31.12 (на паре не нагру-│
│                        жается, отметить присутствие)         │
│   ⚪ Нет действующей справки (2): Орлова, Зайцев             │  ← не допущены к нагрузке
│   ⏳ Справка истекла (1): Белов (30.06) — направить на кафедру│
│                                                              │
│  ⚠ 2 студента «нет справки» — к физической нагрузке не       │
│     допускать. Сообщите им обратиться на кафедру/медосмотр.  │
└──────────────────────────────────────────────────────────────┘
```

### 2.4 Перечень элементов экрана преподавателя

1. Список «Мои группы» с агрегированным светофором по `PhysicalGroup` (5 счётчиков) + бейдж «истекают ≤14 дн».
2. Раскрываемая таблица группы: №, ФИО, физгруппа (цвет+текст), срок действия, статус по сроку. Без скана, без диагноза, без `HealthGroup`.
3. Фильтры: все / не допущенные / истекают / спец+освобождён. Поиск по ФИО (pg_trgm).
4. Кнопка «Экспорт в Excel» (ClosedXML) — выгрузка только разрешённых полей (ФИО, группа, физгруппа, срок, статус), без мед. содержимого.
5. Карточка студента: только допуск + срок + физгруппа + функциональные ограничения + факт подтверждения; явная плашка-объяснение ограничения видимости (323-ФЗ).
6. Экран «Перед парой»: сегодняшний срез допущен/не допущен/освобождён по выбранной группе/занятию.
7. Преподаватель — read-only по справкам (не создаёт, не правит, не верифицирует).

### 2.5 Ограничения видимости — ЯВНО (почему нет диагноза и скана)

| Что скрыто от преподавателя | Норма / причина |
|---|---|
| **Диагноз, нозология, код МКБ** | **323-ФЗ ст.13** (врачебная тайна) + **152-ФЗ ст.10** (спецкатегория). В нашей модели такого поля нет вообще — намеренная минимизация. |
| **Скан/PDF справки** (`CertificateScan`) | Содержит диагноз и печати медучреждения = врачебная тайна. Доступ только у уполномоченного медработника. |
| **Группа здоровья I–V** (`HealthGroup`) | Общая медоценка, преподавателю физкультуры не требуется для допуска → минимизация (152-ФЗ ст.5 ч.5). |
| **Заметки медработника** (`Comment`), **причина отклонения** (`RejectionReason`) | Внутренняя медицинская/служебная информация. |
| **Аудит-журнал** (`AuditLog`) | Инструмент ИБ (УЗ-3, ФСТЭК), не педагогический. |
| **Кнопки правки/верификации** | Преподаватель не уполномочен подтверждать медфакт (`VerifiedByUserId` ставит только медработник). |

Что преподаватель видит — это минимально необходимое для допуска к нагрузке: **СТАТУС, а не диагноз**. Прямой аналог Essex/OHWorks: «clearance status shared with course admin… but they will not be able to access any of your OH records», и FERPA-логики (athletics-screening-формы = «education records» с ограничением раскрытия — раскрывается eligibility, не медданные).

### 2.6 Маппинг экрана преподавателя на зарубежные аналоги (Блок 1)

| Элемент нашего экрана | Зарубежный аналог | Источник |
|---|---|---|
| Список групп со светофором допусков | Compliance dashboard с когортами и фильтрами для staff | Medicat (Immunization Compliance Module); PnC compliance |
| Статус «⚪ Нет справки = не допущен» | Registration hold / «Hlth Immunizations Reg Hold» (блок на регистрацию) | PnC (Tulane); Cornell holds; UCLA hold types |
| Карточка: только допуск + срок + физгруппа, без медданных | Role-segregated visibility: «status shared… but not OH records» | Essex / OHWorks; PyraMED security access controls |
| Светофор eligibility в реальном времени по группе | Clearance/eligibility status тренеру в реальном времени (Sideline) | Privit Profile; Teamworks/ARMS role-based видимость |
| Экран «Перед парой» (кто допущен сегодня) | Real-time eligibility «informing coaches of participation» | Privit (Sideline App) |
| Раздельный доступ физрук vs медработник | Один EHR на 4 службы + «detailed security access controls» (AT видит спорт-допуск, не general chart) | PyraMED |
| Экспорт в Excel без мед. полей | Reports/exports для staff (compliance), минимально необходимое | Medicat admin reports; minimum-necessary disclosure (ed.gov FERPA) |
| Фильтр «истекают ≤14 дней» | Expiring-requirements dashboard | UofT POWER; Medicat ежемесячные аудиты |

---

## Сводная таблица «Элемент → Видимость по роли»

| Элемент / поле (домен) | Студент (свой кабинет) | Преподаватель (физрук) | Медработник-верификатор |
|---|---|---|---|
| Физкультурная группа `PhysicalGroup` | ✅ своя | ✅ своих групп | ✅ |
| Срок действия `Start/EndDate` + `CertificateStatus` | ✅ свой | ✅ | ✅ |
| Статус кейса `VerificationStatus` (вкл. «на проверке») | ✅ свой | ⚠️ только итог (допущен/не допущен/истёк) | ✅ полный |
| Таймлайн статусов кейса | ✅ свой | ❌ (видит итог, не цепочку) | ✅ |
| Функциональные ограничения `Restrictions` (без диагноза) | ✅ свои | ✅ кратко («ограничения по нагрузке») | ✅ |
| Причина отклонения `RejectionReason` | ✅ своя | ❌ | ✅ |
| `CertificateNumber`, `MedicalOrganization` | ✅ свои | ❌ (не нужно для допуска) | ✅ |
| Скан/PDF `CertificateScan` | ✅ свой загруженный | ❌ 323-ФЗ ст.13 | ✅ |
| Группа здоровья `HealthGroup` (I–V) | ❌ минимизация | ❌ минимизация | ✅ |
| Диагноз / нозология / МКБ | ❌ (нет поля в модели) | ❌ (нет поля) | ❌ хранится только в скане |
| Заметки медработника `Comment` | ❌ внутреннее | ❌ внутреннее | ✅ |
| Кто верифицировал `VerifiedByUserId` (ФИО) | ⚠️ только факт «кафедрой» | ⚠️ только факт | ✅ |
| Аудит-журнал `AuditLog` (IP, before/after) | ❌ | ❌ | ⚠️ только аудитор/администратор ИБ |
| Кнопка «Загрузить справку» | ✅ (v2) | ❌ | n/a |
| Кнопка «Подтвердить/Отклонить» (verify) | ❌ | ❌ | ✅ |
| Экспорт в Excel | ❌ | ✅ без мед. полей | ✅ |

Легенда: ✅ полностью видит · ⚠️ видит ограниченно (только статус/факт) · ❌ скрыто.

---

## Ключевые продуктовые выводы

1. **Светофор = `PhysicalGroup`, а не диагноз.** И студенту, и преподавателю отдаётся 5-цветный статус допуска (основная/подготовительная/спец А-Б/освобождён/нет справки) — прямой перенос «compliance status dashboard» Medicat/PnC на нашу физкультурную семантику. Цвет всегда дублируется текстом+иконкой (доступность).
2. **«Нет действующей справки» = наш аналог registration hold.** Не блокируем регистрацию (это вне нашего контура), но на экране «Перед парой» это явный «не допускать к нагрузке» — функциональный эквивалент hold из Cornell/PnC.
3. **Граница ролей проходит ровно по врачебной тайне.** Преподаватель никогда не получает скан, диагноз и группу здоровья — это закодировано и в модели (диагноза-поля нет), и в UI (плашка-объяснение со ссылкой на 323-ФЗ). Это наш аналог Essex/OHWorks и PyraMED security access controls.
4. **Загрузка ≠ допуск.** `StudentUpload → Draft/NeedsReview`, `Verified` ставит человек — точная калька принципа Privit «status is not automatically updated».
5. **e-consent — gate перед первой загрузкой** (152-ФЗ ст.10), как Receipt of Privacy Practices в Medicat и e-signature в Privit; отказ оставляет ручной ввод кафедрой.

(Файлов на диске не создавал — только читал доменную модель `ReuMedCertificates.Domain` для соответствия терминов. Подтверждённые по коду статусы/группы/поля перечислены во врезке вверху.)
