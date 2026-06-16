using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Scans;
using ReuMedCertificates.Application.Students;

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
