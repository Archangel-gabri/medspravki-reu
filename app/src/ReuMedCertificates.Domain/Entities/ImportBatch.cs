using ReuMedCertificates.Domain.Common;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>История импортов из Excel — что и когда было загружено, с отчётом по строкам.</summary>
public class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FileName { get; set; } = string.Empty;
    public Guid? ImportedByUserId { get; set; }
    public DateTime ImportedAt { get; set; }

    /// <summary>Pending / Completed / Failed.</summary>
    public string Status { get; set; } = "Pending";

    public int TotalRows { get; set; }
    public int CreatedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }

    public string? ReportJson { get; set; }
}
