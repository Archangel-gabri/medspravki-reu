using ReuMedCertificates.Domain.Common;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Domain.Entities;

/// <summary>
/// Загруженный скан/PDF справки. Файл хранится ВНЕ wwwroot (файловое хранилище),
/// в БД — только метаданные + путь + SHA-256 (анти-подмена) + результат распознавания.
/// </summary>
public class CertificateScan : BaseEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>Привязка к созданной из скана справке (если уже создана).</summary>
    public Guid? CertificateId { get; set; }
    public MedicalCertificate? Certificate { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>Имя файла в хранилище (GUID), не угадывается.</summary>
    public string StoredName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/pdf";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 содержимого — фиксируется при загрузке, перепроверяется при открытии.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DraftSource Source { get; set; } = DraftSource.StudentUpload;

    public Guid? UploadedByUserId { get; set; }

    // --- Результат ИИ-распознавания (предзаполнение, не источник истины) ---
    /// <summary>None / Pending / Done / Failed / Skipped.</summary>
    public string RecognitionStatus { get; set; } = "None";
    public string? RecognitionJson { get; set; }
    public string? RecognitionModel { get; set; }
    public DateTime? RecognizedAt { get; set; }

    // --- Отклонение заявки преподавателем (v2): скан без справки и без причины = «на проверке». ---
    /// <summary>Причина отклонения заявки студента; заполнена → заявка отклонена.</summary>
    public string? RejectionReason { get; set; }
    public DateTime? RejectedAt { get; set; }

    // --- Срок действия, указанный студентом при загрузке (преподаватель подтверждает/правит). ---
    public DateOnly? ProposedStartDate { get; set; }
    public DateOnly? ProposedEndDate { get; set; }
}
