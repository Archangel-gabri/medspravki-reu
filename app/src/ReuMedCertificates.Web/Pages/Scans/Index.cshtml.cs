using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Scans;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Web.Pages.Scans;

public class IndexModel : PageModel
{
    private readonly IScanService _scans;
    private readonly IStudentService _students;
    private readonly ScanStorageOptions _scanOptions;
    private readonly RecognitionOptions _recognitionOptions;

    public IndexModel(IScanService scans, IStudentService students,
        ScanStorageOptions scanOptions, RecognitionOptions recognitionOptions)
    {
        _scans = scans;
        _students = students;
        _scanOptions = scanOptions;
        _recognitionOptions = recognitionOptions;
    }

    public Guid StudentId { get; private set; }
    public string StudentName { get; private set; } = string.Empty;
    public IReadOnlyList<ScanRow> Scans { get; private set; } = Array.Empty<ScanRow>();
    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }

    public long MaxUploadBytes => _scanOptions.MaxUploadBytes;
    public string MaxUploadMb => (_scanOptions.MaxUploadBytes / 1024.0 / 1024.0).ToString("0.#");
    public bool AiEnabled => !string.Equals(_recognitionOptions.Provider, "Manual", StringComparison.OrdinalIgnoreCase);
    public string AiModel => _recognitionOptions.VisionModel;

    public record ScanRow(ScanListItem Item, IReadOnlyList<RecognizedField> Fields);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(Guid id, IFormFile? file, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null) return NotFound();

        if (file is null || file.Length == 0)
            ErrorMessage = "Файл не выбран.";
        else if (file.Length > _scanOptions.MaxUploadBytes)
            ErrorMessage = $"Файл больше {MaxUploadMb} МБ.";
        else if (!_scanOptions.AllowedContentTypes.Contains(file.ContentType))
            ErrorMessage = $"Недопустимый тип файла ({file.ContentType}). Разрешено: PDF, JPG, PNG.";
        else if (!await HasAllowedSignatureAsync(file, cancellationToken))
            ErrorMessage = "Файл не прошёл проверку сигнатуры — это не PDF/JPG/PNG.";
        else
        {
            await using var stream = file.OpenReadStream();
            await _scans.UploadAsync(
                new ScanUploadRequest(id, stream, Path.GetFileName(file.FileName), file.ContentType),
                cancellationToken);
            Message = "Скан загружен.";
        }

        await LoadAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRecognizeAsync(Guid id, Guid scanId, CancellationToken cancellationToken)
    {
        var result = await _scans.RecognizeAsync(scanId, cancellationToken);
        Message = result is null
            ? "Распознавание недоступно или не удалось (см. журнал)."
            : $"Распознано полей: {result.Fields.Count}. Проверьте и подтвердите вручную.";

        await LoadAsync(id, cancellationToken);
        return Page();
    }

    // Медработник подтверждает поля (human-in-the-loop, ФЗ-273) → создаётся справка из скана и связывается с ним.
    public async Task<IActionResult> OnPostCreateCertAsync(
        Guid id, Guid scanId, DateOnly startDate, DateOnly endDate, DateOnly? issueDate,
        PhysicalEducationGroup physicalGroup, HealthGroup healthGroup,
        string? certificateNumber, string? medicalOrganization, string? restrictions,
        CancellationToken cancellationToken)
    {
        try
        {
            await _scans.CreateCertificateFromScanAsync(scanId,
                new AddCertificateRequest(id, startDate, endDate, issueDate, healthGroup, physicalGroup,
                    restrictions, null, certificateNumber, medicalOrganization),
                cancellationToken);
            Message = "Справка создана из скана и подтверждена. Допуск студента обновлён.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync(id, cancellationToken);
        return Page();
    }

    // Медработник отклоняет заявку-скан с причиной → студент увидит её в кабинете.
    public async Task<IActionResult> OnPostRejectScanAsync(Guid id, Guid scanId, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await _scans.RejectScanAsync(scanId, reason ?? string.Empty, cancellationToken);
            Message = "Заявка отклонена. Студент увидит причину в личном кабинете.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync(id, cancellationToken);
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null) return false;

        StudentId = student.Id;
        StudentName = student.FullName;

        var items = await _scans.ListForStudentAsync(id, cancellationToken);
        var rows = new List<ScanRow>();
        foreach (var item in items)
        {
            IReadOnlyList<RecognizedField> fields = Array.Empty<RecognizedField>();
            if (item.RecognitionStatus is "Done")
            {
                var detail = await _scans.GetAsync(item.Id, cancellationToken);
                fields = ParseFields(detail?.RecognitionJson);
            }
            rows.Add(new ScanRow(item, fields));
        }

        Scans = rows;
        return true;
    }

    // Проверка реальной сигнатуры (magic bytes), а не клиентского MIME — анти-XSS на загрузке.
    private static async Task<bool> HasAllowedSignatureAsync(IFormFile file, CancellationToken ct)
    {
        var buf = new byte[8];
        await using var s = file.OpenReadStream();
        var n = await s.ReadAsync(buf.AsMemory(0, 8), ct);
        bool pdf = n >= 4 && buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46; // %PDF
        bool png = n >= 8 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47; // PNG
        bool jpg = n >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF;                    // JPEG
        return pdf || png || jpg;
    }

    private static IReadOnlyList<RecognizedField> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<RecognizedField>();
        try
        {
            var result = JsonSerializer.Deserialize<RecognitionResult>(json);
            return result?.Fields ?? Array.Empty<RecognizedField>();
        }
        catch
        {
            return Array.Empty<RecognizedField>();
        }
    }
}
