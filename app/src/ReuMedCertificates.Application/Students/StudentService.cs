using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;

namespace ReuMedCertificates.Application.Students;

public sealed class StudentService : IStudentService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;
    private readonly CertificateOptions _options;

    public StudentService(IApplicationDbContext db, ICurrentUser user, IDateTimeProvider clock, CertificateOptions options)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _options = options;
    }

    public async Task<CreateStudentResult> CreateAsync(
        CreateStudentRequest request,
        bool confirmDuplicate = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new ArgumentException("ФИО не может быть пустым.", nameof(request));

        var normalized = Student.Normalize(request.FullName);

        var duplicate = await _db.Students.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.NormalizedFullName == normalized && s.StudyGroupId == request.StudyGroupId,
                cancellationToken);

        if (duplicate is not null && !confirmDuplicate)
            return CreateStudentResult.Duplicate(duplicate.Id);

        var now = _clock.UtcNow;
        var student = new Student
        {
            FullName = request.FullName.Trim(),
            NormalizedFullName = normalized,
            DepartmentId = request.DepartmentId,
            Course = request.Course,
            StudyGroupId = request.StudyGroupId,
            TeacherId = request.TeacherId,
            IsActive = true,
            CreatedByUserId = _user.UserId,
            UpdatedByUserId = _user.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Students.Add(student);
        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(Student), student.Id, "Create",
            $"Создан студент «{student.FullName}»"));

        await _db.SaveChangesAsync(cancellationToken);
        return CreateStudentResult.Created(student.Id);
    }

    public async Task<StudentDetail?> GetDetailAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var student = await _db.Students.AsNoTracking()
            .Include(s => s.Department)
            .Include(s => s.StudyGroup)
            .Include(s => s.Teacher)
            .Include(s => s.Certificates)
            .FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);

        if (student is null)
            return null;

        var today = _clock.Today;
        var threshold = _options.ExpiringSoonThresholdDays;

        var certificates = student.Certificates
            .Where(c => !c.IsDeleted)
            .OrderByDescending(c => c.EndDate)
            .Select(c => new CertificateView(
                c.Id, c.IssueDate, c.StartDate, c.EndDate, c.CertificateNumber, c.MedicalOrganization,
                c.HealthGroup, c.PhysicalGroup, c.Restrictions, c.Comment,
                c.GetStatus(today, threshold), c.VerificationStatus, c.Type))
            .ToList();

        var current = certificates.Where(c => c.EndDate >= today).ToList();
        var history = certificates.Where(c => c.EndDate < today).ToList();

        return new StudentDetail(
            student.Id, student.FullName, student.Department?.Name ?? "—", student.Course,
            student.StudyGroup?.Name ?? "—", student.Teacher?.FullName,
            student.DepartmentId, student.StudyGroupId, student.TeacherId,
            current, history);
    }
}
