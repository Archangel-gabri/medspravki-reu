namespace ReuMedCertificates.Application.Common;

/// <summary>Настройки источника импорта реестра (1С OData / SQL). Раздел конфигурации "Roster".</summary>
public sealed class RosterOptions
{
    public const string SectionName = "Roster";

    /// <summary>Sql | OneC.</summary>
    public string Provider { get; set; } = "Sql";

    public SqlRosterOptions Sql { get; set; } = new();
    public OneCRosterOptions OData { get; set; } = new();
}

public sealed class SqlRosterOptions
{
    public string Description { get; set; } = "локальная демо-база (1С-подобная таблица onec_roster_demo)";

    /// <summary>Строка подключения к внешней SQL-базе. Пусто → используется база приложения (для демо).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>SQL-запрос, возвращающий колонки: external_id, full_name, department, course, group_name, teacher.</summary>
    public string Query { get; set; } =
        "SELECT external_id, full_name, department, course, group_name, teacher FROM onec_roster_demo ORDER BY full_name";
}

public sealed class OneCRosterOptions
{
    /// <summary>Базовый URL OData-интерфейса 1С, напр. https://1c.rea.ru/base/odata/standard.odata/Catalog_Студенты?$format=json</summary>
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Имя массива записей в ответе (в 1С OData это "value").</summary>
    public string ValueProperty { get; set; } = "value";

    // Имена полей в ответе 1С → наши поля.
    public string FullNameField { get; set; } = "Description";
    public string ExternalIdField { get; set; } = "Ref_Key";
    public string DepartmentField { get; set; } = "Подразделение";
    public string CourseField { get; set; } = "Курс";
    public string GroupField { get; set; } = "Группа";
    public string TeacherField { get; set; } = "Преподаватель";
}
