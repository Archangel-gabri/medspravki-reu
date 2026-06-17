using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Scans;

public sealed record ScanUploadRequest(Guid StudentId, Stream Content, string OriginalFileName, string ContentType);

public sealed record ScanListItem(
    Guid Id, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, Guid? CertificateId, string? RejectionReason);

public sealed record ScanContent(Stream Stream, string ContentType, string OriginalFileName);

public sealed record ScanDetail(
    Guid Id, Guid StudentId, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, string? RecognitionModel, string? RecognitionJson);

/// <summary>Элемент очереди медработника: загруженный студентом скан, по которому ещё не создана справка.</summary>
public sealed record PendingScanItem(
    Guid ScanId, Guid StudentId, string StudentName, string Group,
    string OriginalFileName, DateTime UploadedAt, string RecognitionStatus);

public interface IScanService
{
    Task<Guid> UploadAsync(ScanUploadRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanListItem>> ListForStudentAsync(Guid studentId, CancellationToken cancellationToken = default);
    Task<ScanContent?> OpenAsync(Guid scanId, CancellationToken cancellationToken = default);
    Task<ScanDetail?> GetAsync(Guid scanId, CancellationToken cancellationToken = default);
    Task<RecognitionResult?> RecognizeAsync(Guid scanId, CancellationToken cancellationToken = default);

    /// <summary>Очередь заявок студентов (сканы без созданной справки и не отклонённые) — для медработника.</summary>
    Task<IReadOnlyList<PendingScanItem>> ListPendingStudentScansAsync(CancellationToken cancellationToken = default);
    Task<int> CountPendingStudentScansAsync(CancellationToken cancellationToken = default);

    /// <summary>Медработник создаёт подтверждённую справку по скану студента и связывает скан с ней.
    /// Студент берётся из скана (целостность), факт подтверждается человеком (Verified).</summary>
    Task<Guid> CreateCertificateFromScanAsync(Guid scanId, AddCertificateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Медработник отклоняет заявку-скан с обязательной причиной (студент увидит её в кабинете).</summary>
    Task RejectScanAsync(Guid scanId, string reason, CancellationToken cancellationToken = default);
}

public sealed class ScanService : IScanService
{
    private readonly IApplicationDbContext _db;
    private readonly IScanStorage _storage;
    private readonly IDocumentRecognitionService _recognition;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;
    private readonly IFieldProtector _protector;

    public ScanService(
        IApplicationDbContext db, IScanStorage storage, IDocumentRecognitionService recognition,
        ICurrentUser user, IDateTimeProvider clock, IFieldProtector protector)
    {
        _db = db;
        _storage = storage;
        _recognition = recognition;
        _user = user;
        _clock = clock;
        _protector = protector;
    }

    public async Task<Guid> UploadAsync(ScanUploadRequest request, CancellationToken cancellationToken = default)
    {
        var stored = await _storage.SaveAsync(request.Content, cancellationToken);

        var scan = new CertificateScan
        {
            StudentId = request.StudentId,
            OriginalFileName = request.OriginalFileName,
            StoredName = stored.StoredName,
            ContentType = request.ContentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            Source = DraftSource.StudentUpload,
            UploadedByUserId = _user.UserId,
            RecognitionStatus = "None",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        _db.Scans.Add(scan);
        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(CertificateScan), scan.Id, "ScanUpload",
            $"Загружен скан «{scan.OriginalFileName}» ({scan.SizeBytes} байт, sha256:{scan.Sha256[..12]}…)"));

        await _db.SaveChangesAsync(cancellationToken);
        return scan.Id;
    }

    public async Task<IReadOnlyList<ScanListItem>> ListForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .Where(s => s.StudentId == studentId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ScanListItem(s.Id, s.OriginalFileName, s.SizeBytes, s.CreatedAt, s.RecognitionStatus, s.CertificateId, s.RejectionReason))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PendingScanItem>> ListPendingStudentScansAsync(CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .Where(s => s.CertificateId == null && s.RejectionReason == null && s.Source == DraftSource.StudentUpload)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new PendingScanItem(
                s.Id, s.StudentId, s.Student!.FullName, s.Student.StudyGroup!.Name,
                s.OriginalFileName, s.CreatedAt, s.RecognitionStatus))
            .ToListAsync(cancellationToken);

    public async Task<int> CountPendingStudentScansAsync(CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .CountAsync(s => s.CertificateId == null && s.RejectionReason == null && s.Source == DraftSource.StudentUpload, cancellationToken);

    public async Task<Guid> CreateCertificateFromScanAsync(Guid scanId, AddCertificateRequest request, CancellationToken cancellationToken = default)
    {
        var scan = await _db.Scans.FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken)
            ?? throw new InvalidOperationException("Скан не найден.");
        if (scan.CertificateId is not null)
            throw new InvalidOperationException("По этому скану справка уже создана.");
        if (request.EndDate < request.StartDate)
            throw new InvalidOperationException("Дата окончания не может быть раньше даты начала.");

        var now = _clock.UtcNow;
        var cert = new MedicalCertificate
        {
            // Студента берём из скана, а не из формы — нельзя оформить справку чужому студенту.
            StudentId = scan.StudentId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IssueDate = request.IssueDate,
            HealthGroup = request.HealthGroup,
            PhysicalGroup = request.PhysicalGroup,
            Restrictions = request.Restrictions,
            Comment = request.Comment,
            CertificateNumber = request.CertificateNumber,
            MedicalOrganization = request.MedicalOrganization,
            // Источник — загрузка студента; факт подтверждён медработником (человеком) → Verified.
            Source = DraftSource.StudentUpload,
            VerificationStatus = VerificationStatus.Verified,
            VerifiedByUserId = _user.UserId,
            VerifiedAt = now,
            CreatedByUserId = _user.UserId,
            UpdatedByUserId = _user.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Certificates.Add(cert);

        scan.CertificateId = cert.Id;
        scan.RejectionReason = null;   // если скан был отклонён, создание справки снимает отклонение
        scan.RejectedAt = null;
        scan.UpdatedAt = now;

        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(MedicalCertificate), cert.Id, "Create",
            $"Справка создана из скана {scan.Id:N} студента {scan.StudentId:N} " +
            $"({request.StartDate:dd.MM.yyyy}–{request.EndDate:dd.MM.yyyy}), подтверждена медработником"));

        await _db.SaveChangesAsync(cancellationToken);
        return cert.Id;
    }

    public async Task RejectScanAsync(Guid scanId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Укажите причину отклонения.");

        var scan = await _db.Scans.FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken)
            ?? throw new InvalidOperationException("Скан не найден.");
        if (scan.CertificateId is not null)
            throw new InvalidOperationException("По скану уже создана справка — отклонить нельзя.");

        var now = _clock.UtcNow;
        scan.RejectionReason = reason.Trim();
        scan.RejectedAt = now;
        scan.UpdatedAt = now;

        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(CertificateScan), scan.Id, "ScanRejected",
            $"Заявка-скан студента {scan.StudentId:N} отклонена: {reason.Trim()}"));

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScanContent?> OpenAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var scan = await _db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken);
        if (scan is null) return null;

        var stream = await _storage.OpenReadAsync(scan.StoredName, cancellationToken);
        if (stream is null) return null;

        return new ScanContent(stream, scan.ContentType, scan.OriginalFileName);
    }

    public async Task<ScanDetail?> GetAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var d = await _db.Scans.AsNoTracking()
            .Where(s => s.Id == scanId)
            .Select(s => new ScanDetail(s.Id, s.StudentId, s.OriginalFileName, s.SizeBytes, s.CreatedAt,
                s.RecognitionStatus, s.RecognitionModel, s.RecognitionJson))
            .FirstOrDefaultAsync(cancellationToken);
        return d?.RecognitionJson is { } json ? d with { RecognitionJson = _protector.Unprotect(json) } : d;
    }

    public async Task<RecognitionResult?> RecognizeAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var scan = await _db.Scans.FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken);
        if (scan is null) return null;

        var stream = await _storage.OpenReadAsync(scan.StoredName, cancellationToken);
        if (stream is null)
        {
            scan.RecognitionStatus = "Failed";
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }

        byte[] bytes;
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        try
        {
            var result = await _recognition.RecognizeAsync(
                new ScanInput(bytes, scan.ContentType, scan.OriginalFileName), cancellationToken);

            // Шифруем извлечённые поля at-rest (спецкатегория ПДн, P1 MED-A02).
            scan.RecognitionJson = _protector.Protect(JsonSerializer.Serialize(result));
            scan.RecognitionStatus = result.RequiresManualReview && result.Fields.Count == 0 ? "Skipped" : "Done";
            scan.RecognizedAt = _clock.UtcNow;
            scan.UpdatedAt = _clock.UtcNow;

            _db.AuditLogs.Add(AuditEntryFactory.Create(
                _user, _clock, nameof(CertificateScan), scan.Id, "ScanRecognized",
                $"ИИ-распознавание скана: полей {result.Fields.Count}, требуется ручная проверка: {result.RequiresManualReview}"));

            await _db.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            scan.RecognitionStatus = "Failed";
            scan.RecognitionJson = _protector.Protect(JsonSerializer.Serialize(new { error = ex.Message }));
            scan.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }
    }
}
