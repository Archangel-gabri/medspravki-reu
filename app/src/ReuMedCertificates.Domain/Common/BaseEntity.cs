namespace ReuMedCertificates.Domain.Common;

/// <summary>Базовая сущность: идентификатор и служебные метки времени.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
