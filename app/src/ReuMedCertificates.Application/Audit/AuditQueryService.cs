using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Application.Audit;

/// <summary>Строка журнала аудита для отображения.</summary>
public sealed record AuditEntryView(
    DateTime OccurredAt,
    string ActionType,
    string EntityType,
    string? Description,
    string UserName);

public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEntryView>> GetRecentAsync(
        int take = 200, string? actionType = null, CancellationToken cancellationToken = default);
}

public sealed class AuditQueryService : IAuditQueryService
{
    private readonly IApplicationDbContext _db;

    public AuditQueryService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<AuditEntryView>> GetRecentAsync(
        int take = 200, string? actionType = null, CancellationToken cancellationToken = default)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(actionType))
            query = query.Where(a => a.ActionType == actionType);

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new AuditEntryView(a.OccurredAt, a.ActionType, a.EntityType, a.Description, a.UserNameSnapshot))
            .ToListAsync(cancellationToken);
    }
}
