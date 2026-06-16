using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Domain.Entities;

namespace ReuMedCertificates.Application.Common;

/// <summary>Сборка записи бизнес-аудита со снимком пользователя и времени.</summary>
public static class AuditEntryFactory
{
    public static AuditLog Create(
        ICurrentUser user,
        IDateTimeProvider clock,
        string entityType,
        Guid? entityId,
        string actionType,
        string? description = null,
        string? beforeJson = null,
        string? afterJson = null) =>
        new()
        {
            EntityType = entityType,
            EntityId = entityId,
            ActionType = actionType,
            UserId = user.UserId,
            UserNameSnapshot = user.UserName,
            OccurredAt = clock.UtcNow,
            Description = description,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = user.IpAddress
        };
}
