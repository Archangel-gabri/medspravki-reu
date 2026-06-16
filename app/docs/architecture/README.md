# Architecture

Текущее решение собрано как модульный монолит:

- `Domain` хранит сущности и инварианты
- `Application` хранит контракты и use case слой
- `Infrastructure` хранит EF Core, Identity, PostgreSQL и инфраструктурные сервисы
- `Web` хранит Razor Pages и HTTP-конфигурацию

Следующий архитектурный шаг:

- добавить миграции;
- реализовать CRUD для студентов и справок;
- вынести справочники и импорт Excel в отдельные application services.

