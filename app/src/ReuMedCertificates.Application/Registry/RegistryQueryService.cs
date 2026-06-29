using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Registry;

public sealed class RegistryQueryService : IRegistryQueryService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly CertificateOptions _options;

    public RegistryQueryService(IApplicationDbContext db, IDateTimeProvider clock, CertificateOptions options)
    {
        _db = db;
        _clock = clock;
        _options = options;
    }

    public async Task<RegistryPage> SearchAsync(RegistryFilter filter, CancellationToken cancellationToken = default)
    {
        var query = _db.Students.AsNoTracking().Where(s => s.IsActive);

        if (filter.DepartmentId is { } departmentId)
            query = query.Where(s => s.DepartmentId == departmentId);
        if (filter.Course is { } course)
            query = query.Where(s => s.Course == course);
        if (filter.StudyGroupId is { } groupId)
            query = query.Where(s => s.StudyGroupId == groupId);
        if (filter.TeacherId is { } teacherId)
            query = query.Where(s => s.TeacherId == teacherId);

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var normalized = Student.Normalize(filter.Query);
            // Колонка нормализована (lower); pg_trgm GIN-индекс ускоряет LIKE с обоими wildcard.
            query = query.Where(s => EF.Functions.Like(s.NormalizedFullName, $"%{normalized}%"));
        }

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        var projected = await query
            .OrderBy(s => s.FullName)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(s => new
            {
                s.Id,
                s.FullName,
                Department = s.Department!.Name,
                s.Course,
                Group = s.StudyGroup!.Name,
                Teacher = s.Teacher != null ? s.Teacher.FullName : null,
                // Все ПОДТВЕРЖДЁННЫЕ человеком справки (допуск + недопуск).
                Certs = s.Certificates
                    .Where(c => !c.IsDeleted && c.VerificationStatus == VerificationStatus.Verified)
                    .Select(c => new { c.Type, c.HealthGroup, c.PhysicalGroup, c.StartDate, c.EndDate, c.Admitted, c.Restrictions })
                    .ToList(),
                // Есть ли неподтверждённая справка в работе (черновик/на проверке).
                HasPendingReview = s.Certificates.Any(c => !c.IsDeleted &&
                    (c.VerificationStatus == VerificationStatus.NeedsReview || c.VerificationStatus == VerificationStatus.Draft))
            })
            .ToListAsync(cancellationToken);

        // Отклонённые по качеству заявки-сканы (без созданной справки) → показать «Заявка отклонена».
        var ids = projected.Select(p => p.Id).ToList();
        var rejectedScanStudents = (await _db.Scans.AsNoTracking()
                .Where(sc => ids.Contains(sc.StudentId) && sc.CertificateId == null && sc.RejectionReason != null)
                .Select(sc => sc.StudentId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var today = _clock.Today;
        var threshold = _options.ExpiringSoonThresholdDays;

        var rows = projected.Select(x =>
        {
            var certs = x.Certs
                .Select(c => new CertView(c.Type, c.HealthGroup, c.PhysicalGroup, c.StartDate, c.EndDate, c.Admitted, c.Restrictions))
                .ToList();
            var types = certs.Select(c => c.Type).Distinct().OrderBy(t => t).ToList();

            CertView? primary = null;
            CertificateStatus? status = null;
            var notAdmitted = false;

            // 1) Действующая справка-ДОПУСК (вкл. «скоро истекает»/«будет активна»).
            var curAdm = certs.Where(c => c.Admitted)
                .Select(c => new { c, st = DateStatus(c.StartDate, c.EndDate, today, threshold) })
                .Where(z => IsCurrent(z.st))
                .OrderByDescending(z => z.c.EndDate)
                .ToList();
            if (curAdm.Count > 0)
            {
                primary = curAdm[0].c;
                status = curAdm[0].st;
            }
            else
            {
                // 2) Справка-НЕДОПУСК (валидный вердикт «не допущен»). Это флаг безопасности —
                //    показываем его всегда при отсутствии действующего допуска, даже если срок истёк.
                var notAdm = certs.Where(c => !c.Admitted)
                    .OrderByDescending(c => c.EndDate)
                    .ToList();
                if (notAdm.Count > 0)
                {
                    primary = notAdm[0];
                    notAdmitted = true;
                }
                else if (certs.Count > 0)
                {
                    // 3) Остались только справки-допуски, и все истекли.
                    primary = certs.OrderByDescending(c => c.EndDate).First();
                    status = CertificateStatus.Expired;
                }
            }

            var healthGroup = primary?.HealthGroup ?? HealthGroup.Unknown;
            if (healthGroup == HealthGroup.Unknown && certs.Count > 0)
                healthGroup = certs.Select(c => c.HealthGroup).Max();

            return new RegistryRow(
                x.Id, x.FullName, x.Department, x.Course, x.Group, x.Teacher,
                primary?.PhysicalGroup ?? PhysicalEducationGroup.None,
                healthGroup,
                types,
                primary?.Restrictions,
                primary?.StartDate,
                primary?.EndDate,
                status,
                x.HasPendingReview,
                notAdmitted,
                rejectedScanStudents.Contains(x.Id));
        }).ToList();

        return new RegistryPage(rows, total, page, size);
    }

    private sealed record CertView(
        CertificateType Type, HealthGroup HealthGroup, PhysicalEducationGroup PhysicalGroup,
        DateOnly StartDate, DateOnly EndDate, bool Admitted, string? Restrictions);

    private static CertificateStatus DateStatus(DateOnly start, DateOnly end, DateOnly today, int threshold)
    {
        if (today > end) return CertificateStatus.Expired;
        if (today == end) return CertificateStatus.EndsToday;
        if (today < start) return CertificateStatus.Upcoming;
        if (end.DayNumber - today.DayNumber <= threshold) return CertificateStatus.ExpiringSoon;
        return CertificateStatus.Active;
    }

    private static bool IsCurrent(CertificateStatus s) =>
        s is CertificateStatus.Active or CertificateStatus.ExpiringSoon
          or CertificateStatus.EndsToday or CertificateStatus.Upcoming;
}
