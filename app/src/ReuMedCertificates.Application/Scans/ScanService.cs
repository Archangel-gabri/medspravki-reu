using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Scans;

public sealed record ScanUploadRequest(Guid StudentId, Stream Content, string OriginalFileName, string ContentType);

public sealed record ScanListItem(
    Guid Id, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, Guid? CertificateId);

public sealed record ScanContent(Stream Stream, string ContentType, string OriginalFileName);

public sealed record ScanDetail(
    Guid Id, Guid StudentId, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, string? RecognitionModel, string? RecognitionJson);

public interface IScanService
{
    Task<Guid> UploadAsync(ScanUploadRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanListItem>> ListForStudentAsync(Guid studentId, CancellationToken cancellationToken = default);
    Task<ScanContent?> OpenAsync(Guid scanId, CancellationToken cancellationToken = default);
    Task<ScanDetail?> GetAsync(Guid scanId, CancellationToken cancellationToken = default);
    Task<RecognitionResult?> RecognizeAsync(Guid scanId, CancellationToken cancellationToken = default);
}

public sealed class ScanService : IScanService
{
    private readonly IApplicationDbContext _db;
    private readonly IScanStorage _storage;
    private readonly IDocumentRecognitionService _recognition;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;

    public ScanService(
        IApplicationDbContext db, IScanStorage storage, IDocumentRecognitionService recognition,
        ICurrentUser user, IDateTimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _recognition = recognition;
        _user = user;
        _clock = clock;
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
            .Select(s => new ScanListItem(s.Id, s.OriginalFileName, s.SizeBytes, s.CreatedAt, s.RecognitionStatus, s.CertificateId))
            .ToListAsync(cancellationToken);

    public async Task<ScanContent?> OpenAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var scan = await _db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken);
        if (scan is null) return null;

        var stream = await _storage.OpenReadAsync(scan.StoredName, cancellationToken);
        if (stream is null) return null;

        return new ScanContent(stream, scan.ContentType, scan.OriginalFileName);
    }

    public async Task<ScanDetail?> GetAsync(Guid scanId, CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .Where(s => s.Id == scanId)
            .Select(s => new ScanDetail(s.Id, s.StudentId, s.OriginalFileName, s.SizeBytes, s.CreatedAt,
                s.RecognitionStatus, s.RecognitionModel, s.RecognitionJson))
            .FirstOrDefaultAsync(cancellationToken);

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

            scan.RecognitionJson = JsonSerializer.Serialize(result);
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
            scan.RecognitionJson = JsonSerializer.Serialize(new { error = ex.Message });
            scan.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }
    }
}
