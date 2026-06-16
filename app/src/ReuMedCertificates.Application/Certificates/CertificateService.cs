using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Certificates;

public sealed class CertificateService : ICertificateService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;

    public CertificateService(IApplicationDbContext db, ICurrentUser user, IDateTimeProvider clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<Guid> AddAsync(AddCertificateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EndDate < request.StartDate)
            throw new InvalidOperationException("Дата окончания не может быть раньше даты начала.");

        var now = _clock.UtcNow;
        var certificate = new MedicalCertificate
        {
            StudentId = request.StudentId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IssueDate = request.IssueDate,
            HealthGroup = request.HealthGroup,
            PhysicalGroup = request.PhysicalGroup,
            Restrictions = request.Restrictions,
            Comment = request.Comment,
            CertificateNumber = request.CertificateNumber,
            MedicalOrganization = request.MedicalOrganization,
            // Ручной ввод уполномоченным преподавателем = подтверждённый факт.
            Source = DraftSource.Manual,
            VerificationStatus = VerificationStatus.Verified,
            VerifiedByUserId = _user.UserId,
            VerifiedAt = now,
            CreatedByUserId = _user.UserId,
            UpdatedByUserId = _user.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Certificates.Add(certificate);
        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(MedicalCertificate), certificate.Id, "Create",
            $"Добавлена справка для студента {request.StudentId:N} ({request.StartDate:dd.MM.yyyy}–{request.EndDate:dd.MM.yyyy})"));

        await _db.SaveChangesAsync(cancellationToken);
        return certificate.Id;
    }

    public async Task<IReadOnlyList<ReviewItem>> GetReviewQueueAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Certificates.AsNoTracking()
            .Where(c => !c.IsDeleted && c.VerificationStatus == VerificationStatus.NeedsReview)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ReviewItem(
                c.Id,
                c.StudentId,
                c.Student!.FullName,
                c.Student.Department!.Name,
                c.Student.StudyGroup!.Name,
                c.Student.Teacher != null ? c.Student.Teacher.FullName : null,
                c.StartDate,
                c.EndDate,
                c.PhysicalGroup,
                c.Restrictions,
                c.Source,
                c.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetReviewQueueCountAsync(CancellationToken cancellationToken = default) =>
        await _db.Certificates.AsNoTracking()
            .CountAsync(c => !c.IsDeleted && c.VerificationStatus == VerificationStatus.NeedsReview, cancellationToken);

    public async Task ApproveAsync(Guid certificateId, CancellationToken cancellationToken = default)
    {
        var cert = await _db.Certificates.FirstOrDefaultAsync(c => c.Id == certificateId && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Справка не найдена.");
        if (cert.VerificationStatus != VerificationStatus.NeedsReview)
            throw new InvalidOperationException("Подтвердить можно только справку со статусом «На проверке».");

        var now = _clock.UtcNow;
        cert.VerificationStatus = VerificationStatus.Verified;
        cert.VerifiedByUserId = _user.UserId;
        cert.VerifiedAt = now;
        cert.UpdatedByUserId = _user.UserId;
        cert.UpdatedAt = now;

        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(MedicalCertificate), cert.Id, "Approve",
            $"Справка подтверждена ({cert.StartDate:dd.MM.yyyy}–{cert.EndDate:dd.MM.yyyy})"));

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectAsync(Guid certificateId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Укажите причину отклонения.");

        var cert = await _db.Certificates.FirstOrDefaultAsync(c => c.Id == certificateId && !c.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Справка не найдена.");
        if (cert.VerificationStatus != VerificationStatus.NeedsReview)
            throw new InvalidOperationException("Отклонить можно только справку со статусом «На проверке».");

        var now = _clock.UtcNow;
        cert.VerificationStatus = VerificationStatus.Rejected;
        cert.RejectionReason = reason.Trim();
        cert.UpdatedByUserId = _user.UserId;
        cert.UpdatedAt = now;

        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(MedicalCertificate), cert.Id, "Reject",
            $"Справка отклонена: {reason.Trim()}"));

        await _db.SaveChangesAsync(cancellationToken);
    }
}
