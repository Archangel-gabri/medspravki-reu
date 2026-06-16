namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Источник текущего времени/даты — вынесен для тестируемости статусов справок.</summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}
