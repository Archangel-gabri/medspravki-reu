using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Registry;

/// <summary>Параметры поиска и фильтрации реестра.</summary>
public sealed record RegistryFilter(
    string? Query = null,
    Guid? DepartmentId = null,
    short? Course = null,
    Guid? StudyGroupId = null,
    Guid? TeacherId = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>Строка реестра: студент + его допуск (агрегат по ПРОВЕРЕННЫМ справкам).</summary>
public sealed record RegistryRow(
    Guid StudentId,
    string FullName,
    string Department,
    short Course,
    string Group,
    string? Teacher,
    PhysicalEducationGroup PhysicalGroup,
    HealthGroup HealthGroup,
    IReadOnlyList<CertificateType> Types,
    string? Restrictions,
    DateOnly? StartDate,
    DateOnly? EndDate,
    CertificateStatus? Status,
    bool HasPendingReview,
    bool NotAdmitted,
    bool HasRejectedScan);

public sealed record RegistryPage(IReadOnlyList<RegistryRow> Rows, int Total, int Page, int PageSize);

public interface IRegistryQueryService
{
    Task<RegistryPage> SearchAsync(RegistryFilter filter, CancellationToken cancellationToken = default);
}
