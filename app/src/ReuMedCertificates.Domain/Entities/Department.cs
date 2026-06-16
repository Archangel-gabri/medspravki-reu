using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>Учебное подразделение (факультет / высшая школа).</summary>
public class Department : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<StudyGroup> Groups { get; set; } = new List<StudyGroup>();
}
