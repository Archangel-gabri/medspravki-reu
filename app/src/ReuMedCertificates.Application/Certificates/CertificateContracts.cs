using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Certificates;

public sealed record AddCertificateRequest(
    Guid StudentId,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly? IssueDate,
    HealthGroup HealthGroup,
    PhysicalEducationGroup PhysicalGroup,
    string? Restrictions,
    string? Comment,
    string? CertificateNumber,
    string? MedicalOrganization,
    CertificateType Type = CertificateType.Standard086);

/// <summary>Элемент очереди «На проверке» (неподтверждённая справка).</summary>
public sealed record ReviewItem(
    Guid CertificateId,
    Guid StudentId,
    string StudentName,
    string Department,
    string Group,
    string? Teacher,
    DateOnly StartDate,
    DateOnly EndDate,
    PhysicalEducationGroup PhysicalGroup,
    string? Restrictions,
    DraftSource Source,
    DateTime CreatedAt);

public interface ICertificateService
{
    /// <summary>Добавляет справку студенту (ручной ввод преподавателем = сразу Verified, источник Manual).</summary>
    Task<Guid> AddAsync(AddCertificateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Очередь справок со статусом «На проверке».</summary>
    Task<IReadOnlyList<ReviewItem>> GetReviewQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Количество справок «На проверке» (для бейджа в меню).</summary>
    Task<int> GetReviewQueueCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Подтвердить справку (NeedsReview → Verified) — официальный факт, ставит человек.</summary>
    Task ApproveAsync(Guid certificateId, CancellationToken cancellationToken = default);

    /// <summary>Отклонить справку (NeedsReview → Rejected) с обязательной причиной.</summary>
    Task RejectAsync(Guid certificateId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Отозвать действующую справку (Verified → Revoked) с причиной — физрук снимает допуск.</summary>
    Task RevokeAsync(Guid certificateId, string reason, CancellationToken cancellationToken = default);
}
