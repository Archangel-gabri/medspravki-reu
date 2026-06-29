using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Students;

public sealed record CertificateView(
    Guid Id,
    DateOnly? IssueDate,
    DateOnly StartDate,
    DateOnly EndDate,
    string? CertificateNumber,
    string? MedicalOrganization,
    HealthGroup HealthGroup,
    PhysicalEducationGroup PhysicalGroup,
    string? Restrictions,
    string? Comment,
    CertificateStatus Status,
    VerificationStatus VerificationStatus,
    CertificateType Type,
    bool Admitted);

public sealed record StudentDetail(
    Guid Id,
    string FullName,
    string Department,
    short Course,
    string Group,
    string? Teacher,
    Guid DepartmentId,
    Guid StudyGroupId,
    Guid? TeacherId,
    IReadOnlyList<CertificateView> Current,
    IReadOnlyList<CertificateView> History);

public sealed record CreateStudentRequest(
    string FullName,
    Guid DepartmentId,
    short Course,
    Guid StudyGroupId,
    Guid? TeacherId);

public sealed record CreateStudentResult(Guid? StudentId, bool DuplicateWarning, Guid? ExistingStudentId)
{
    public static CreateStudentResult Created(Guid id) => new(id, false, null);
    public static CreateStudentResult Duplicate(Guid existingId) => new(null, true, existingId);
}

public interface IStudentService
{
    /// <summary>Создаёт студента. При найденном дубле (ФИО+группа) без подтверждения вернёт предупреждение.</summary>
    Task<CreateStudentResult> CreateAsync(CreateStudentRequest request, bool confirmDuplicate = false, CancellationToken cancellationToken = default);

    /// <summary>Карточка студента с текущими и архивными справками.</summary>
    Task<StudentDetail?> GetDetailAsync(Guid studentId, CancellationToken cancellationToken = default);
}
