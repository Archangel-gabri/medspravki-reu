using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;

namespace ReuMedCertificates.Application.Roster;

public sealed class RosterImportService : IRosterImportService
{
    private readonly IRosterSource _source;
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;

    public RosterImportService(IRosterSource source, IApplicationDbContext db, ICurrentUser user, IDateTimeProvider clock)
    {
        _source = source;
        _db = db;
        _user = user;
        _clock = clock;
    }

    public async Task<RosterPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var records = await _source.FetchAsync(cancellationToken);

        var existing = (await _db.Students.AsNoTracking()
            .Select(s => new { s.NormalizedFullName, Group = s.StudyGroup!.Name })
            .ToListAsync(cancellationToken))
            .Select(x => x.NormalizedFullName + "|" + x.Group)
            .ToHashSet();

        var departments = await _db.Departments.AsNoTracking().Select(d => d.Name).ToListAsync(cancellationToken);

        var rows = new List<RosterPreviewRow>(records.Count);
        foreach (var r in records)
        {
            RosterRowAction action;
            string? note = null;

            if (!departments.Contains(r.DepartmentName))
            {
                action = RosterRowAction.Conflict;
                note = "Подразделение не найдено в справочнике";
            }
            else if (existing.Contains(Student.Normalize(r.FullName) + "|" + r.GroupName))
            {
                action = RosterRowAction.Exists;
                note = "Уже в реестре";
            }
            else
            {
                action = RosterRowAction.New;
            }

            rows.Add(new RosterPreviewRow(r.FullName, r.DepartmentName, r.Course, r.GroupName, r.TeacherName, action, note));
        }

        return new RosterPreview(
            _source.SourceName,
            rows,
            rows.Count(x => x.Action == RosterRowAction.New),
            rows.Count(x => x.Action == RosterRowAction.Exists),
            rows.Count(x => x.Action == RosterRowAction.Conflict));
    }

    public async Task<RosterImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var records = await _source.FetchAsync(cancellationToken);
        var now = _clock.UtcNow;

        var existing = (await _db.Students.AsNoTracking()
            .Select(s => new { s.NormalizedFullName, Group = s.StudyGroup!.Name })
            .ToListAsync(cancellationToken))
            .Select(x => x.NormalizedFullName + "|" + x.Group)
            .ToHashSet();

        int created = 0, skipped = 0, errors = 0;
        // Создаваемые в рамках этого батча группы — переиспользуем по имени.
        var newGroups = new Dictionary<string, StudyGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in records)
        {
            var key = Student.Normalize(r.FullName) + "|" + r.GroupName;
            if (existing.Contains(key))
            {
                skipped++;
                continue;
            }

            var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Name == r.DepartmentName, cancellationToken);
            if (dept is null)
            {
                errors++;
                continue;
            }

            Teacher? teacher = null;
            if (!string.IsNullOrWhiteSpace(r.TeacherName))
                teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.FullName == r.TeacherName, cancellationToken);

            var group = await _db.StudyGroups.FirstOrDefaultAsync(g => g.Name == r.GroupName && g.DepartmentId == dept.Id, cancellationToken);
            if (group is null && !newGroups.TryGetValue(r.GroupName, out group))
            {
                group = new StudyGroup
                {
                    Name = r.GroupName,
                    Course = r.Course,
                    DepartmentId = dept.Id,
                    TeacherId = teacher?.Id,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.StudyGroups.Add(group);
                newGroups[r.GroupName] = group;
            }

            _db.Students.Add(new Student
            {
                FullName = r.FullName,
                NormalizedFullName = Student.Normalize(r.FullName),
                DepartmentId = dept.Id,
                Course = r.Course,
                StudyGroup = group,
                TeacherId = teacher?.Id,
                IsActive = true,
                CreatedByUserId = _user.UserId,
                UpdatedByUserId = _user.UserId,
                CreatedAt = now,
                UpdatedAt = now
            });

            existing.Add(key);
            created++;
        }

        var batch = new ImportBatch
        {
            FileName = _source.SourceName,
            ImportedByUserId = _user.UserId,
            ImportedAt = now,
            Status = "Completed",
            TotalRows = records.Count,
            CreatedRows = created,
            UpdatedRows = 0,
            SkippedRows = skipped,
            ErrorRows = errors
        };
        _db.ImportBatches.Add(batch);

        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(ImportBatch), batch.Id, "Import",
            $"Импорт из «{_source.SourceName}»: создано {created}, пропущено {skipped}, ошибок {errors} (новых групп {newGroups.Count})"));

        await _db.SaveChangesAsync(cancellationToken);

        return new RosterImportResult(created, skipped, errors, newGroups.Count, batch.Id);
    }
}
