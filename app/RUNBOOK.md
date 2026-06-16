# RUNBOOK — ReuMedCertificates (ИС медсправок РЭУ)

Рабочее решение v1 (`projects/MedSpravki-REU/app/`). Стек: ASP.NET Core 8 + Razor Pages +
PostgreSQL 16 + EF Core 8 + Identity. Clean Architecture: Domain ← Application ← Infrastructure ← Web.

## Что готово (Фаза 0, проверено сборкой)

- ✅ Все 4 проекта собираются (`dotnet build` — 0 ошибок, 0 предупреждений).
- ✅ Юнит-тесты статуса справки — 3/3 проходят.
- ✅ Доменная модель: 9 сущностей + enum + **case-lifecycle** (`VerificationStatus`/`DraftSource` — совет Codex).
- ✅ `ApplicationDbContext` (Identity + домен), миграция `InitialCreate` (pg_trgm, GIN-индекс по ФИО, xmin-concurrency).
- ✅ Pluggable распознавание за `IDocumentRecognitionService` — дефолт `ManualRecognitionProvider` (без ИИ/GPU).
- ✅ Сидинг: роли (Teacher/HeadOfDepartment/Admin), bootstrap-пользователь, 9 факультетов + 13 физруков.
- ⏳ **Нет UI** (Razor-страницы реестра/входа/CRUD) — следующий шаг. `/` пока редиректит на ещё не созданный `/registry`.

## Предусловия

- .NET SDK 8 (`dotnet --version` → 8.0.x). На Arch: `pacman -S dotnet-sdk-8.0 aspnet-runtime-8.0 aspnet-targeting-pack`.
- PostgreSQL 16 (для запуска; для сборки/миграций НЕ нужен).

## Сборка и тесты

```bash
cd projects/MedSpravki-REU/app
dotnet build ReuMedCertificates.sln      # зелёная сборка
dotnet test  ReuMedCertificates.sln      # 3/3
```

## База данных и миграции

```bash
# 1. Поднять Postgres (вариант для разработки — Docker):
docker run -d --name reu-pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=reu_med_certificates \
  -p 5432:5432 postgres:16

# 2. Строка подключения — в src/ReuMedCertificates.Web/appsettings.json (ConnectionStrings:DefaultConnection)
#    или через переменную REU_DB_CONNECTION (её читает design-time фабрика).

# 3. Применить схему:
dotnet ef database update \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web

# Добавить новую миграцию:
dotnet ef migrations add <Name> \
  --project src/ReuMedCertificates.Infrastructure \
  --startup-project src/ReuMedCertificates.Web \
  --output-dir Persistence/Migrations
```

> Приложение при старте само вызывает `MigrateAsync()` + сидинг (см. `Program.cs` → `SeedIdentityAsync`).
> Bootstrap-пользователь создаётся только если в `appsettings` `BootstrapUser:Enabled = true`.

## Запуск

```bash
cd src/ReuMedCertificates.Web
dotnet run        # слушает https://localhost:5xxx (порт см. в выводе)
```

## Развёртывание в РЭУ (целевое, по ТЗ) — TODO для следующих фаз

Codex верно отметил: «сетевая папка / флешка» — это не план развёртывания. Нужны:
- публикация `dotnet publish -c Release` → артефакт под IIS (Windows), `appsettings.Production.json`;
- PostgreSQL как служба в ЛВС РЭУ; бэкап `pg_dump` по расписанию (Task Scheduler), хранить ≥7 копий;
- bootstrap первого админа; HTTPS; офлайн-набор runtime/NuGet для машины без интернета;
- INSERT-only права для app-роли БД на `audit_logs` (revoke UPDATE/DELETE) — см. PLAN.md §7.

## Карта решения

```
src/
  ReuMedCertificates.Domain/         # сущности, enum, GetStatus, case-lifecycle
  ReuMedCertificates.Application/    # IApplicationDbContext, ICurrentUser, IDocumentRecognitionService,
                                     #   сервисы Registry/Students/Certificates
  ReuMedCertificates.Infrastructure/ # ApplicationDbContext, Identity (AppUser/AppRole), DI, DataSeeder,
                                     #   ManualRecognitionProvider, миграции
  ReuMedCertificates.Web/            # Program.cs (Razor Pages, Serilog) — UI в следующей фазе
tests/
  ReuMedCertificates.UnitTests/      # тесты статуса справки
```
