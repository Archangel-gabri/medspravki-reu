using ReuMedCertificates.Domain.Common;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>
/// Медицинская справка студента. Срок действия даёт вычисляемый <see cref="CertificateStatus"/>,
/// а доверие к данным — отдельный жизненный цикл кейса (<see cref="VerificationStatus"/>).
/// </summary>
public class MedicalCertificate : BaseEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public DateOnly? IssueDate { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public string? CertificateNumber { get; set; }

    /// <summary>Медицинская организация, выдавшая справку.</summary>
    public string? MedicalOrganization { get; set; }

    public HealthGroup HealthGroup { get; set; } = HealthGroup.Unknown;
    public PhysicalEducationGroup PhysicalGroup { get; set; } = PhysicalEducationGroup.None;

    /// <summary>Ограничения — функциональные формулировки, БЕЗ диагноза (152-ФЗ минимизация).</summary>
    public string? Restrictions { get; set; }
    public string? Comment { get; set; }

    // --- Жизненный цикл кейса (case-lifecycle, Codex) ---
    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.Draft;
    public DraftSource Source { get; set; } = DraftSource.Manual;
    public Guid? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? RejectionReason { get; set; }

    // --- Служебные ---
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    // Логическое удаление (физическое удаление пользовательских справок запрещено в v1).
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }

    /// <summary>Optimistic concurrency через xmin (Npgsql).</summary>
    public uint Version { get; set; }

    /// <summary>Статус действия по сроку (без учёта порога «скоро истекает»).</summary>
    public CertificateStatus GetStatus(DateOnly today)
    {
        if (today > EndDate) return CertificateStatus.Expired;
        if (today == EndDate) return CertificateStatus.EndsToday;
        if (today < StartDate) return CertificateStatus.Upcoming;
        return CertificateStatus.Active;
    }

    /// <summary>Статус действия по сроку с учётом порога «скоро истекает».</summary>
    public CertificateStatus GetStatus(DateOnly today, int expiringSoonThresholdDays)
    {
        if (today > EndDate) return CertificateStatus.Expired;
        if (today == EndDate) return CertificateStatus.EndsToday;
        if (today < StartDate) return CertificateStatus.Upcoming;
        if (EndDate.DayNumber - today.DayNumber <= expiringSoonThresholdDays)
            return CertificateStatus.ExpiringSoon;
        return CertificateStatus.Active;
    }
}
