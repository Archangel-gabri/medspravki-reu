using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Application.Lookups;

public sealed record DepartmentLookup(Guid Id, string Name);
public sealed record TeacherLookup(Guid Id, string FullName);
public sealed record GroupLookup(Guid Id, string Name, short Course, Guid DepartmentId);

public interface ILookupService
{
    Task<IReadOnlyList<DepartmentLookup>> GetDepartmentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeacherLookup>> GetTeachersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupLookup>> GetGroupsAsync(Guid? departmentId = null, CancellationToken cancellationToken = default);
}

public sealed class LookupService : ILookupService
{
    private readonly IApplicationDbContext _db;

    public LookupService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<DepartmentLookup>> GetDepartmentsAsync(CancellationToken cancellationToken = default) =>
        await _db.Departments.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentLookup(d.Id, d.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TeacherLookup>> GetTeachersAsync(CancellationToken cancellationToken = default) =>
        await _db.Teachers.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.FullName)
            .Select(t => new TeacherLookup(t.Id, t.FullName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GroupLookup>> GetGroupsAsync(Guid? departmentId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.StudyGroups.AsNoTracking().Where(g => g.IsActive);
        if (departmentId is { } id)
            query = query.Where(g => g.DepartmentId == id);

        return await query
            .OrderBy(g => g.Course).ThenBy(g => g.Name)
            .Select(g => new GroupLookup(g.Id, g.Name, g.Course, g.DepartmentId))
            .ToListAsync(cancellationToken);
    }
}
