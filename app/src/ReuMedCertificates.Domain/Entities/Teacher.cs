using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>Преподаватель кафедры (физрук) как объект справочника.</summary>
public class Teacher : BaseEntity
{
    public string FullName { get; set; } = string.Empty;
    public string? ShortName { get; set; }

    /// <summary>Должность / примечание (напр. «доцент 0.5 внеш.совм»).</summary>
    public string? Position { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<StudyGroup> Groups { get; set; } = new List<StudyGroup>();
    public ICollection<Student> Students { get; set; } = new List<Student>();
}
