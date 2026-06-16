using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>
/// Журнал изменений (бизнес-аудит). INSERT-only: правка/удаление записей запрещены
/// на уровне прав БД (см. миграцию/деплой). Хранит снимок «до/после» в jsonb.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }

    /// <summary>Тип действия: Create / Update / SoftDelete / Import / Login / LoginFailed / Export …</summary>
    public string ActionType { get; set; } = string.Empty;

    public Guid? UserId { get; set; }
    public string UserNameSnapshot { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }
    public string? Description { get; set; }

    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    public string? IpAddress { get; set; }
}
