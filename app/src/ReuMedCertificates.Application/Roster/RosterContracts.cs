namespace ReuMedCertificates.Application.Roster;

/// <summary>
/// Запись о студенте из внешнего источника (1С / SQL-база деканата): студент ↔ группа ↔ преподаватель.
/// Источником истины для привязки остаётся внешняя система, наш реестр её отражает.
/// </summary>
public sealed record RosterRecord(
    string? ExternalId,
    string FullName,
    string DepartmentName,
    short Course,
    string GroupName,
    string? TeacherName);

/// <summary>Подключаемый внешний источник реестра (1С OData / SQL). Аналог IDocumentRecognitionService.</summary>
public interface IRosterSource
{
    /// <summary>Человекочитаемое имя источника (для UI и аудита).</summary>
    string SourceName { get; }

    Task<IReadOnlyList<RosterRecord>> FetchAsync(CancellationToken cancellationToken = default);
}

public enum RosterRowAction
{
    /// <summary>Новый студент — будет создан.</summary>
    New,
    /// <summary>Уже есть в реестре — будет пропущен.</summary>
    Exists,
    /// <summary>Не удаётся сопоставить (нет подразделения и т.п.) — будет пропущен с ошибкой.</summary>
    Conflict
}

public sealed record RosterPreviewRow(
    string FullName,
    string DepartmentName,
    short Course,
    string GroupName,
    string? TeacherName,
    RosterRowAction Action,
    string? Note);

public sealed record RosterPreview(
    string SourceName,
    IReadOnlyList<RosterPreviewRow> Rows,
    int NewCount,
    int ExistsCount,
    int ConflictCount);

public sealed record RosterImportResult(int Created, int Skipped, int Errors, int NewGroups, Guid BatchId);

public interface IRosterImportService
{
    /// <summary>Сухой прогон: что будет создано/пропущено (без изменения данных).</summary>
    Task<RosterPreview> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Применить импорт: создать новых студентов (и недостающие группы), записать ImportBatch + аудит.</summary>
    Task<RosterImportResult> ImportAsync(CancellationToken cancellationToken = default);
}
