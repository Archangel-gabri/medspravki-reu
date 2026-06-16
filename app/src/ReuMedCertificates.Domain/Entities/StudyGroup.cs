using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>Учебная группа (формат имени, напр. «15.27Д-БИ01/23Б»).</summary>
public class StudyGroup : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public short Course { get; set; }

    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }

    public Guid? TeacherId { get; set; }
    public Teacher? Teacher { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Student> Students { get; set; } = new List<Student>();
}
