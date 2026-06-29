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
            // У бассейновой справки группа здоровья (I–V) не применяется — там группы А/Б.
            HealthGroup = request.Type == CertificateType.Pool ? HealthGroup.Unknown : request.HealthGroup,
            PhysicalGroup = request.PhysicalGroup,
            Type = request.Type,
            Admitted = request.Admitted,
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
            scan.AiNotes = "ОШИБКА: " + ex.Message;   // диагностика (видно в БД)
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
            scan.AiNotes ??= "ИИ не распознал документ.";   // не затираем диагностику из RecognizeAsync
            scan.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var p = ParseRecognized(rec.RawJson);
        var today = DateOnly.FromDateTime(now);

        // Шаг человека №1: «дата выдачи» не может быть датой рождения (в 086/у она стоит сразу за ФИО).
        var issueDate = RecognitionRules.ResolveIssueDate(p.IssueDate, p.BirthDate);

        // «Не допущен» — это НЕ брак, а валидный медицинский вердикт (Admitted=false).
        var admitted = p.FitForPe != false;

        // Срок: явный «с–по», иначе «дата выдачи + N месяцев» (для 086/у обычно 6).
        var start = p.StartDate ?? issueDate;
        var end = p.EndDate ?? (start.HasValue ? start.Value.AddMonths(p.ValidityMonths ?? 6) : (DateOnly?)null);

        // Брак скана (нужна пересдача/ручной ввод): ФИО / печать-или-ЭЦП / срок.
        var flags = new List<string>();
        if (!NameMatches(p.FullName, scan.Student?.FullName))
            flags.Add($"ФИО на справке не совпадает с вашим (распознано: «{p.FullName ?? "—"}»)");
        // Шаг человека №2: достаточно печати ИЛИ электронной подписи (е-справки физпечати не имеют).
        if (!RecognitionRules.StampOrSignaturePresent(p.HasStamp, p.ElectronicSignature))
            flags.Add("не видно ни печати, ни электронной подписи");
        // Шаг человека №3: срок нужен только для справки-ДОПУСКА; «не допущен» действует без срока.
        if (RecognitionRules.ValidityRequired(admitted) && (start is null || end is null))
            flags.Add("не распознан срок действия");
        else if (start is not null && end is not null && end < start)
            flags.Add("срок указан неверно");

        // У справки для бассейна нет «группы здоровья» (там группы А/Б по плаванию) — не подставляем её.
        var healthGroup = p.Type == CertificateType.Pool ? HealthGroup.Unknown : p.HealthGroup;

        if (flags.Count == 0)
        {
            // Для «не допущен» без срока — нейтральные даты (в реестре срок у недопуска скрыт).
            var effStart = start ?? issueDate ?? today;
            var effEnd = end ?? effStart;
            await CreateCertificateFromScanAsync(scanId, new AddCertificateRequest(
                scan.StudentId, effStart, effEnd, issueDate,
                healthGroup, p.PhysicalGroup, p.Restrictions, "Автопроверка ИИ",
                p.CertNumber, p.Organization, p.Type, admitted), cancellationToken);
            scan.RecognitionStatus = "AutoApproved";
            scan.AiNotes = admitted
                ? $"ИИ: ФИО совпало, допуск подтверждён; срок {effStart:dd.MM.yyyy}–{effEnd:dd.MM.yyyy}, группа {p.PhysicalGroup}."
                : "ИИ: ФИО совпало, по справке НЕ допущен.";
            // Шаг человека №4: мягкие сигналы «проверьте» — неуверенность модели + подозрительная дата (год?).
            var warns = new List<string>();
            if (rec.LowConfidenceFields is { Count: > 0 } low) warns.AddRange(low);
            if (admitted && !RecognitionRules.IssueDatePlausible(issueDate, today))
                warns.Add("дата выдачи (проверьте год)");
            if (warns.Count > 0)
                scan.AiNotes += " ⚠ Проверьте (распознано неуверенно): " + string.Join(", ", warns) + ".";
        }
        else
        {
            // Один из статусов — «Отклонено» с причиной (никакой ручной очереди).
            scan.RecognitionStatus = "Rejected";
            scan.RejectionReason = string.Join("; ", flags) + ". Пришлите корректную справку.";
            scan.RejectedAt = now;
            scan.AiNotes = "Отклонено ИИ: " + string.Join("; ", flags);
        }
        scan.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private sealed record Recognized(
        string? FullName, DateOnly? StartDate, DateOnly? EndDate, DateOnly? IssueDate, int? ValidityMonths,
        HealthGroup HealthGroup, PhysicalEducationGroup PhysicalGroup, CertificateType Type,
        bool? HasStamp, bool? HasSignature, bool? FitForPe, string? PlaceOfStudy,
        string? CertNumber, string? Organization, string? Restrictions,
        DateOnly? BirthDate, bool? ElectronicSignature);

    private static Recognized ParseRecognized(string? rawJson)
    {
        static string? Str(JsonElement r, string k) =>
            r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool? Bln(JsonElement r, string k) =>
            r.TryGetProperty(k, out var v) ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is "да" or "true" or "yes" or "есть" or "годен" or "допущен",
                _ => (bool?)null
            } : null;
        static int? Intg(JsonElement r, string k)
        {
            if (!r.TryGetProperty(k, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(new string((v.GetString() ?? "").Where(char.IsDigit).ToArray()), out var m)) return m;
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
            var r = doc.RootElement;
            return new Recognized(
                Str(r, "full_name"), ParseDate(Str(r, "start_date")), ParseDate(Str(r, "end_date")), ParseDate(Str(r, "issue_date")), Intg(r, "validity_months"),
                MapHealth(Str(r, "health_group")), MapPhysical(Str(r, "physical_group")), MapType(Str(r, "document_type")),
                Bln(r, "has_stamp"), Bln(r, "has_signature"), Bln(r, "fit_for_pe"), Str(r, "place_of_study"),
                Str(r, "certificate_number"), Str(r, "medical_organization"), Str(r, "restrictions"),
                ParseDate(Str(r, "birth_date")), Bln(r, "electronic_signature"));
        }
        catch
        {
            return new Recognized(null, null, null, null, null, HealthGroup.Unknown, PhysicalEducationGroup.None, CertificateType.Other, null, null, null, null, null, null, null, null, null);
        }
    }

    private static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] fmts = { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd.MM.yy", "d.M.yy", "dd.M.yy", "d.MM.yy" };
        return DateOnly.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static HealthGroup MapHealth(string? s)
    {
        var v = (s ?? "").Trim().ToUpperInvariant().Replace('Ё', 'Е');
        if (v.Length == 0) return HealthGroup.Unknown;
        // словами и арабскими цифрами
        if (v.Contains("ПЯТ") || v.Contains("5")) return HealthGroup.V;
        if (v.Contains("ЧЕТВ") || v.Contains("4")) return HealthGroup.IV;
        if (v.Contains("ТРЕТ") || v.Contains("3")) return HealthGroup.III;
        if (v.Contains("ВТОР") || v.Contains("2")) return HealthGroup.II;
        if (v.Contains("ПЕРВ") || v.Contains("1")) return HealthGroup.I;
        // римскими (длинные раньше коротких)
        if (v.Contains("IV")) return HealthGroup.IV;
        if (v.Contains("III")) return HealthGroup.III;
        if (v.Contains("II")) return HealthGroup.II;
        if (v.Contains("V")) return HealthGroup.V;
        if (v.Contains("I")) return HealthGroup.I;
        return HealthGroup.Unknown;
    }

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

    private static List<string> Toks(string? s) => (s ?? "").ToLowerInvariant().Replace('ё', 'е')
        .Split(new[] { ' ', '\t', '\n', '\r', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length >= 2).ToList();

    // Нечёткая сверка ФИО: рукопись ИИ читает приблизительно, поэтому сравниваем токены
    // по похожести (Левенштейн). Нужно совпадение минимум двух токенов (фамилия+имя).
    private static bool NameMatches(string? recognized, string? profile)
    {
        var rec = Toks(recognized);
        var prof = Toks(profile);
        if (rec.Count == 0 || prof.Count == 0) return false;
        var matched = prof.Count(pt => rec.Any(rt => Similar(pt, rt) >= 0.6));
        return matched >= Math.Min(2, prof.Count);
    }

    private static double Similar(string a, string b)
    {
        var m = Math.Max(a.Length, b.Length);
        return m == 0 ? 1.0 : 1.0 - (double)Levenshtein(a, b) / m;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }
}
