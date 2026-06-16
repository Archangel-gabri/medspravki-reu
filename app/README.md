# ReuMedCertificates

Каркас локальной системы учета медицинских справок кафедры физической культуры РЭУ.

## Что уже создано

- модульный монолит на `ASP.NET Core 8`
- слои `Domain`, `Application`, `Infrastructure`, `Web`
- базовая модель предметной области
- конфигурация для `PostgreSQL`
- аутентификация на базе `ASP.NET Core Identity`
- стартовые Razor Pages для входа и реестра
- тестовый проект для доменной логики

## Структура

```text
src/
  ReuMedCertificates.Domain/
  ReuMedCertificates.Application/
  ReuMedCertificates.Infrastructure/
  ReuMedCertificates.Web/
tests/
  ReuMedCertificates.UnitTests/
docs/
  architecture/
  deployment/
  excel-template/
```

## Предварительные требования

- установить `.NET SDK 8`
- установить `PostgreSQL 16`
- создать базу данных и обновить строку подключения в `src/ReuMedCertificates.Web/appsettings.json`

## Следующие шаги

1. Установить `.NET SDK 8` на рабочую машину.
2. Запустить восстановление пакетов и сборку решения.
3. Добавить миграции EF Core.
4. Реализовать CRUD-страницы студентов и справок.
5. Реализовать импорт и экспорт Excel.

