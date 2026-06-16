# Deployment

Базовая целевая схема развёртывания:

- Windows Server или выделенный ПК университета
- PostgreSQL 16
- ASP.NET Core 8
- IIS как reverse proxy

Перед первым запуском потребуется:

1. Установить `.NET SDK 8` и runtime.
2. Установить `PostgreSQL`.
3. Создать базу `reu_med_certificates`.
4. Прописать строку подключения.
5. Применить миграции.
6. Запустить приложение.

