using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>Карточка студента.</summary>
public class Student : BaseEntity
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Нормализованное ФИО (нижний регистр, схлопнутые пробелы) для быстрого поиска (pg_trgm).</summary>
    public string NormalizedFullName { get; set; } = string.Empty;

    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }

    public short Course { get; set; }

    public Guid StudyGroupId { get; set; }
    public StudyGroup? StudyGroup { get; set; }

    /// <summary>Назначенный физрук — основа маршрутизации справок к преподавателю.</summary>
    public Guid? TeacherId { get; set; }
    public Teacher? Teacher { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency через системный столбец xmin (Npgsql).</summary>
    public uint Version { get; set; }

    public ICollection<MedicalCertificate> Certificates { get; set; } = new List<MedicalCertificate>();

    /// <summary>Нормализация ФИО для поиска и проверки дублей.</summary>
    public static string Normalize(string fullName) =>
        string.Join(' ', (fullName ?? string.Empty).Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
