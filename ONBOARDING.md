# MedSpravki-REU — онбординг для второго разработчика

Привет! Это совместный проект — **информационная система учёта медсправок** кафедры физкультуры
РЭУ им. Плеханова. Заменяет бумажный учёт допусков к физре живым реестром: препод/ИИ загружает
фото справки (086/у, бассейн, освобождение), система распознаёт и ведёт статус допуска студента.

Ты получаешь **такой же полный доступ, как у владельца** — тот же аккаунт `Castiel` на боевом ПК,
правишь и деплоишь всё так же. Ниже — как подключиться и где что лежит.

---

## 1. Как подключиться к ПК

Весь проект живёт на десктопе **`castiel-pc`** (Arch Linux, RTX 3060). Он доступен только через
**Tailscale** (прямой доступ из интернета режет РФ-DPI — см. §7).

1. Поставь **Tailscale** на макбук: https://tailscale.com/download (обычное приложение, залогинься своим аккаунтом).
2. Владелец расшарит тебе ноду `castiel-pc` в Tailscale (Admin → Machines → castiel-pc → Share) — примешь инвайт по почте.
3. Твой SSH-ключ уже добавлен в аккаунт `Castiel`. Подключение:
   ```bash
   ssh Castiel@<tailscale-ip-узла>
   ```
   **Важно:** заходи ОБЯЗАТЕЛЬНО под пользователем `Castiel` (с большой C), не под своим именем.
   **Про IP:** у владельца нода видна как `<tailscale-ip-узла>`, но у тебя (расшаренная нода) Tailscale может
   показать её под другим адресом (например `<tailscale-ip-узла>`) — используй тот IP, что показывает твой
   Tailscale-клиент для `castiel-pc`, но всегда с `Castiel@`.
   Удобно прописать в `~/.ssh/config`:
   ```
   Host pc          # или любое имя
       HostName <tailscale-ip-узла>
       User Castiel
   ```
   Тогда просто `ssh pc`. Для работы с кодом с макбука удобнее всего **VS Code / Cursor → Remote-SSH**
   на этот же хост — правишь файлы на ПК прямо из редактора.

У аккаунта `Castiel` есть **passwordless sudo** — можешь ставить пакеты, рулить сервисами и т.д.

---

## 2. Где что лежит (на ПК, под `/home/Castiel/`)

| Что | Путь | Заметки |
|-----|------|---------|
| **Исходники** (рабочая копия) | `/home/Castiel/app/` | `.sln`, `src/`, `tests/`. Правишь тут. ⚠️ пока НЕ под git (см. §8) |
| **Собранное/запущенное** | `/home/Castiel/reu-medspravki-pub/` | сюда `dotnet publish`; отсюда работает сервис |
| **Скрипт деплоя** | `/home/Castiel/deploy-on-pc.sh` | ставит зависимости + Postgres + Ollama + публикует + запускает |
| **Бэкапы БД** | `/home/Castiel/reu-pg-backup-*.sql.gz` | делай `pg_dump` перед рисковыми правками БД! |

**Сервисы на ПК:**
- **Веб-приложение** — systemd `reu-medspravki.service`, слушает `0.0.0.0:5080`, env=Development.
  - Статус/логи: `systemctl status reu-medspravki` · `journalctl -u reu-medspravki -f`
  - Рестарт: `sudo systemctl restart reu-medspravki`
- **База** — PostgreSQL 16 в docker-контейнере `reu-pg` (порт 5432, db `reu_med_certificates`, user/pass `postgres`/`postgres`).
  - Подключиться: `docker exec -it reu-pg psql -U postgres -d reu_med_certificates`
- **Локальная LLM** — **Ollama** на `localhost:11434`. Модели: `qwen2.5vl:7b` (vision, читает справки),
  `qwen2.5:14b-instruct`, `nomic-embed-text`. Проверить: `ollama list`. Всё локально (152-ФЗ, без облаков).

---

## 3. Стек и карта кода

**ASP.NET Core 8** (Razor Pages + HTMX + Bootstrap) · **PostgreSQL 16** (pg_trgm) · **EF Core 8** ·
**ASP.NET Identity** · Clean Architecture. .NET SDK уже стоит на ПК.

```
app/src/
  ReuMedCertificates.Domain/         # сущности, enum, жизненный цикл справки
                                     #   Draft→NeedsReview→Verified→(Rejected|Expired|Revoked)
  ReuMedCertificates.Application/    # сервисы Registry/Students/Certificates, интерфейсы
                                     #   (IDocumentRecognitionService и т.д.)
  ReuMedCertificates.Infrastructure/ # DbContext, Identity, DI, DataSeeder, LocalOllamaRecognitionProvider, миграции
  ReuMedCertificates.Web/            # Razor Pages (UI), Program.cs, appsettings*
app/tests/
  ReuMedCertificates.UnitTests/
```

Основные экраны: `/registry` (реестр допусков — поиск/фильтры/светофор/тип справки/группа здоровья),
`/students/{id}` (карточка: справки + история + кнопки «Изменить», «Отозвать»), «Перед парой»,
загрузка скана студентом, `/students/{id}/scans/{scanId}/zoom` (ручной зум по полю).

---

## 4. Как править и деплоить

Работаешь прямо на ПК в `/home/Castiel/app/`. После правок:

```bash
cd /home/Castiel/app

# собрать/протестировать (по желанию)
dotnet build ReuMedCertificates.sln
dotnet test  ReuMedCertificates.sln

# опубликовать новую версию туда, откуда работает сервис
dotnet publish src/ReuMedCertificates.Web/ReuMedCertificates.Web.csproj -c Release -o /home/Castiel/reu-medspravki-pub --nologo

# перезапустить сервис
sudo systemctl restart reu-medspravki

# проверить, что поднялось
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5080/   # ждём 302
```

Первичный/полный деплой с нуля (ставит и зависимости) — `bash /home/Castiel/deploy-on-pc.sh`.

**Миграции БД** (после изменения сущностей):
```bash
cd /home/Castiel/app
dotnet ef migrations add <Имя> \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web \
  --output-dir Persistence/Migrations
# применяется автоматически при старте приложения (MigrateAsync в Program.cs)
```

---

## 5. ИИ-распознавание справок (как устроено)

`LocalOllamaRecognitionProvider` гоняет `qwen2.5vl:7b`:
- Студент/препод грузит фото/PDF (можно несколько страниц — склеиваются в один PDF через ImageMagick).
- **Стадия 1** — модель читает всю справку; **стадия 2** — САМА перечитывает ключевые поля
  (дата выдачи / номер / группа здоровья) отдельным фокус-запросом по каждой странице (флаг `Recognition:TwoStage`).
- **Голосование** по дате (`VoteCount`, деф. 3) + **флаг неуверенности** (пишется в `CertificateScan.AiNotes`
  как «⚠ Проверьте…») + лёгкая предобработка фото (контраст/резкость).
- При успехе авто-создаёт справку `Verified` и сразу даёт допуск; препод контролирует постфактум
  (кнопка «Изменить» правит любые поля, «Отозвать» снимает допуск).
- Конфиг — секция `Recognition` в `appsettings.Development.json` (`Provider = Manual | LocalOllama`, `TwoStage`, `VoteCount`, `Preprocess`).

**Важно:** 7B-модель читает рукопись приблизительно (ФИО — нечёткая сверка Левенштейном), качественную
подделку не ловит — это первый фильтр + автозаполнение, финальная подлинность = глаз препода.

---

## 6. Вход и данные

- Логин преподавателя: **`teacher`**. Пароль задаётся переменной `BootstrapUser__Password`;
  без неё bootstrap-пользователь не создаётся и приложение скажет об этом явно.
- `SeedDemoData=false` → реестр стартует пустым, это норма (демо-студентов больше не подсовываем).
  Справочники (9 высших школ, ~76 физруков, группы) сохранены.
- Реальные студенты заводятся загрузкой фото справки + ИИ, либо ручным вводом.

---

## 7. ⚠️ КРИТИЧНО: публичный адрес и HubVPN

Публичный URL сайта (чтобы открыть с телефона / показать заказчику):
**https://<ваш-узел>.ts.net** (Tailscale Funnel).

**Он работает ТОЛЬКО пока на ПК подключён HAPP (HubVPN).** Причина: РФ-DPI душит соединения Tailscale
с его серверами (нода отваливается каждые ~2 минуты). Когда на ПК активен HAPP, трафик Tailscale идёт
через HubVPN (Германия) и DPI его не видит — тогда адрес стабилен.

**Если публичная ссылка не открывается** → зайди на ПК, проверь что HAPP подключён:
```bash
ip link show tun0            # должен существовать = VPN активен
tailscale funnel status      # Funnel on, proxy → 127.0.0.1:5080
```
Если `tun0` нет — открой HAPP на ПК и нажми «Подключить» к загран-ноде. (Внутри Tailscale/по SSH сайт
доступен всегда, независимо от этого — HAPP нужен только для ПУБЛИЧНОГО адреса.)

---

## 8. Правила совместной работы (важно не потерять данные)

- **`/home/Castiel/app` пока НЕ под git** — то есть нет истории и отката. Мы оба правим ОДНУ копию на
  ЭТОМ ПК, поэтому расходов версий нет, но и «ctrl-z через недели» нет. Рекомендуется завести приватный
  git-репозиторий только под этот проект (НЕ трогая личный vault владельца) — попроси, настроим.
- **Перед рисковыми правками БД делай дамп:** `docker exec reu-pg pg_dump -U postgres reu_med_certificates | gzip > ~/reu-pg-backup-$(date +%Y%m%d).sql.gz`.
  Однажды работу уже теряли при выключении ПК без бэкапа — не повторяем.
- **Реальные ПДн студентов** (сканы, диагнозы) — спецкатегория по 152-ФЗ + врачебная тайна (323-ФЗ):
  никаких облаков, только локальный ИИ; сейчас всё на ТЕСТ-стенде с демо-данными. Прод с реальными
  данными — только после подписи ТЗ v2 и РФ-хостинга.
- **Пароль задавайте только через окружение.** Медданные за слабым или общеизвестным логином —
  это нарушение, а не неудобство: 086/у относится к спецкатегории ПДн (ст. 10 152-ФЗ).
- Не путай: есть ещё дев-инстанс на ноуте владельца (`castiel-laptop`) со своим Postgres — **боевой только `castiel-pc`**.

---

## 9. Что почитать дальше (в репозитории проекта)

- `docs/PROJECT-STATUS.md` — текущий статус проекта (детально, что сделано в последних сессиях).
- `docs/PLAN.md` — большой план (11 разделов).
- `app/RUNBOOK.md` — сборка/миграции/запуск.
- `docs/SECURITY-COMPLIANCE-RESEARCH-2026-06-16.md` — разбор 152-ФЗ/323-ФЗ и модель безопасности.

Вопросы — пиши владельцу. Добро пожаловать в проект!
