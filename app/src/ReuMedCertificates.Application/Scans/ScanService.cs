using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Application.Scans;

public sealed record ScanUploadRequest(
    Guid StudentId, Stream Content, string OriginalFileName, string ContentType,
    DateOnly? StartDate = null, DateOnly? EndDate = null);

public sealed record ScanListItem(
    Guid Id, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, Guid? CertificateId, string? RejectionReason,
    DateOnly? ProposedStartDate, DateOnly? ProposedEndDate,
    DateOnly? CertStartDate, DateOnly? CertEndDate, string? AiNotes);

public sealed record ScanContent(Stream Stream, string ContentType, string OriginalFileName);

public sealed record ScanDetail(
    Guid Id, Guid StudentId, string OriginalFileName, long SizeBytes, DateTime UploadedAt,
    string RecognitionStatus, string? RecognitionModel, string? RecognitionJson);

/// <summary>Элемент очереди медработника: загруженный студентом скан, по которому ещё не создана справка.</summary>
public sealed record PendingScanItem(
    Guid ScanId, Guid StudentId, string StudentName, string Group,
    string OriginalFileName, DateTime UploadedAt, string RecognitionStatus, string? AiNotes);

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

    /// <summary>Сотрудник отклоняет заявку-скан с обязательной причиной (студент увидит её в кабинете).</summary>
    Task RejectScanAsync(Guid scanId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Фоновая ИИ-проверка: распознать справку, сверить ФИО/печать/подпись/срок →
    /// авто-создать допуск (если всё сходится) либо пометить «нужна ручная проверка».</summary>
    Task AutoReviewAsync(Guid scanId, CancellationToken cancellationToken = default);
}

public sealed class ScanService : IScanService
{
    private readonly IApplicationDbContext _db;
    private readonly IScanStorage _storage;
    private readonly IDocumentRecognitionService _recognition;
    private readonly ICurrentUser _user;
    private readonly IDateTimeProvider _clock;
    private readonly IFieldProtector _protector;
    private readonly IScanProcessingQueue _queue;

    public ScanService(
        IApplicationDbContext db, IScanStorage storage, IDocumentRecognitionService recognition,
        ICurrentUser user, IDateTimeProvider clock, IFieldProtector protector, IScanProcessingQueue queue)
    {
        _db = db;
        _storage = storage;
        _recognition = recognition;
        _user = user;
        _clock = clock;
        _protector = protector;
        _queue = queue;
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
            RecognitionStatus = "Processing",   // ИИ проверит в фоне
            ProposedStartDate = request.StartDate,
            ProposedEndDate = request.EndDate,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        _db.Scans.Add(scan);
        _db.AuditLogs.Add(AuditEntryFactory.Create(
            _user, _clock, nameof(CertificateScan), scan.Id, "ScanUpload",
            $"Загружен скан «{scan.OriginalFileName}» ({scan.SizeBytes} байт, sha256:{scan.Sha256[..12]}…)"));

        await _db.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(scan.Id);   // фоновая ИИ-проверка
        return scan.Id;
    }

    public async Task<IReadOnlyList<ScanListItem>> ListForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .Where(s => s.StudentId == studentId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ScanListItem(
                s.Id, s.OriginalFileName, s.SizeBytes, s.CreatedAt, s.RecognitionStatus, s.CertificateId, s.RejectionReason,
                s.ProposedStartDate, s.ProposedEndDate,
                s.Certificate == null ? (DateOnly?)null : s.Certificate.StartDate,
                s.Certificate == null ? (DateOnly?)null : s.Certificate.EndDate,
                s.AiNotes))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PendingScanItem>> ListPendingStudentScansAsync(CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .Where(s => s.CertificateId == null && s.RejectionReason == null
                        && s.RecognitionStatus != "Processing" && s.Source == DraftSource.StudentUpload)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new PendingScanItem(
                s.Id, s.StudentId, s.Student!.FullName, s.Student.StudyGroup!.Name,
                s.OriginalFileName, s.CreatedAt, s.RecognitionStatus, s.AiNotes))
            .ToListAsync(cancellationToken);

    public async Task<int> CountPendingStudentScansAsync(CancellationToken cancellationToken = default) =>
        await _db.Scans.AsNoTracking()
            .CountAsync(s => s.CertificateId == null && s.RejectionReason == null
                             && s.RecognitionStatus != "Processing" && s.Source == DraftSource.StudentUpload, cancellationToken);

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
            Type = request.Type,
            Restrictions = request.Restrictions,
            Comment = request.Comment,
            CertificateNumber = request.CertificateNumber,
            MedicalOrganization = request.MedicalOrganization,
            // Источник — загрузка студента; факт подтверждён человеком (физрук/ИИ-автопроверка) → Verified.
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

    public async Task AutoReviewAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var scan = await _db.Scans.Include(s => s.Student)
            .FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken);
        if (scan is null || scan.CertificateId is not null) return;

        RecognitionResult? rec = null;
        try { rec = await RecognizeAsync(scanId, cancellationToken); }
        catch { /* ниже — как неуспех */ }

        var now = _clock.UtcNow;
        if (rec is null)
        {
            scan.RecognitionStatus = "Rejected";
            scan.RejectionReason = "Справку не удалось распознать. Сфотографируйте чётче (чтобы было видно ФИО, печать, подпись и срок) и пришлите снова.";
            scan.RejectedAt = now;
            scan.AiNotes = "ИИ не распознал документ.";
            scan.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var p = ParseRecognized(rec.RawJson);
        var flags = new List<string>();
        if (!NameMatches(p.FullName, scan.Student?.FullName))
            flags.Add($"ФИО на справке не совпадает с вашим (распознано: «{p.FullName ?? "—"}»)");
        if (p.HasStamp != true) flags.Add("не видно печати медучреждения");
        if (p.HasSignature != true) flags.Add("не видно подписи врача");
        if (p.StartDate is null || p.EndDate is null) flags.Add("не распознан срок действия");
        else if (p.EndDate < p.StartDate) flags.Add("срок указан неверно (окончание раньше начала)");

        if (flags.Count == 0)
        {
            await CreateCertificateFromScanAsync(scanId, new AddCertificateRequest(
                scan.StudentId, p.StartDate!.Value, p.EndDate!.Value, p.IssueDate,
                p.HealthGroup, p.PhysicalGroup, p.Restrictions, "Автопроверка ИИ",
                p.CertNumber, p.Organization, p.Type), cancellationToken);
            scan.RecognitionStatus = "AutoApproved";
            scan.AiNotes = $"ИИ: ФИО совпало, печать и подпись на месте; срок {p.StartDate:dd.MM.yyyy}–{p.EndDate:dd.MM.yyyy}.";
        }
        else
        {
            // Один из трёх статусов — «Отклонено» с причиной (никакой ручной очереди).
            scan.RecognitionStatus = "Rejected";
            scan.RejectionReason = string.Join("; ", flags) + ". Пришлите корректную справку.";
            scan.RejectedAt = now;
            scan.AiNotes = "Отклонено ИИ: " + string.Join("; ", flags);
        }
        scan.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private sealed record Recognized(
        string? FullName, DateOnly? StartDate, DateOnly? EndDate, DateOnly? IssueDate,
        HealthGroup HealthGroup, PhysicalEducationGroup PhysicalGroup, CertificateType Type,
        bool? HasStamp, bool? HasSignature, string? CertNumber, string? Organization, string? Restrictions);

    private static Recognized ParseRecognized(string? rawJson)
    {
        static string? Str(JsonElement r, string k) =>
            r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool? Bln(JsonElement r, string k) =>
            r.TryGetProperty(k, out var v) ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is "да" or "true" or "yes" or "есть",
                _ => (bool?)null
            } : null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
            var r = doc.RootElement;
            return new Recognized(
                Str(r, "full_name"), ParseDate(Str(r, "start_date")), ParseDate(Str(r, "end_date")), ParseDate(Str(r, "issue_date")),
                MapHealth(Str(r, "health_group")), MapPhysical(Str(r, "physical_group")), MapType(Str(r, "document_type")),
                Bln(r, "has_stamp"), Bln(r, "has_signature"), Str(r, "certificate_number"), Str(r, "medical_organization"), Str(r, "restrictions"));
        }
        catch
        {
            return new Recognized(null, null, null, null, HealthGroup.Unknown, PhysicalEducationGroup.None, CertificateType.Other, null, null, null, null, null);
        }
    }

    private static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] fmts = { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy" };
        return DateOnly.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static HealthGroup MapHealth(string? s) => (s?.Trim().ToUpperInvariant()) switch
    {
        "I" or "1" => HealthGroup.I,
        "II" or "2" => HealthGroup.II,
        "III" or "3" => HealthGroup.III,
        "IV" or "4" => HealthGroup.IV,
        "V" or "5" => HealthGroup.V,
        _ => HealthGroup.Unknown
    };

    private static PhysicalEducationGroup MapPhysical(string? s)
    {
        var v = s?.Trim().ToLowerInvariant() ?? "";
        if (v.Contains("основ")) return PhysicalEducationGroup.Basic;
        if (v.Contains("подгот")) return PhysicalEducationGroup.Preparatory;
        if (v.Contains("освобожд")) return PhysicalEducationGroup.Exempt;
        if (v.Contains("специальн")) return v.Contains("б") ? PhysicalEducationGroup.SpecialB : PhysicalEducationGroup.SpecialA;
        return PhysicalEducationGroup.None;
    }

    private static CertificateType MapType(string? s)
    {
        var v = s?.Trim().ToLowerInvariant() ?? "";
        if (v.Length == 0) return CertificateType.Standard086;
        if (v.Contains("086")) return CertificateType.Standard086;
        if (v.Contains("бассейн") || v.Contains("плав") || v.Contains("вод")) return CertificateType.Pool;
        if (v.Contains("освобожд")) return CertificateType.Exemption;
        return CertificateType.Other;
    }

    private static bool NameMatches(string? recognized, string? profile)
    {
        if (string.IsNullOrWhiteSpace(recognized) || string.IsNullOrWhiteSpace(profile)) return false;
        static HashSet<string> Toks(string s) => s.ToLowerInvariant().Replace('ё', 'е')
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2).ToHashSet();
        var rec = Toks(recognized);
        var prof = Toks(profile);
        if (rec.Count == 0 || prof.Count == 0) return false;
        // Совпадение, если меньшее множество токенов целиком входит в большее
        // («Иванов Иван» ⊆ «Иванов Иван Сергеевич»).
        return rec.Count <= prof.Count ? rec.IsSubsetOf(prof) : prof.IsSubsetOf(rec);
    }
}
