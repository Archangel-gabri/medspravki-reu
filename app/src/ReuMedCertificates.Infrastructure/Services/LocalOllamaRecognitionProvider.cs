using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Локальный офлайн-провайдер распознавания: PDF/изображение → vision-LLM через Ollama
/// (в периметре РЭУ / на ПК с GPU). Никаких внешних облаков для медданных (152-ФЗ).
/// ИИ только ПРЕДЗАПОЛНЯЕТ черновик — RequiresManualReview всегда true.
/// </summary>
public sealed class LocalOllamaRecognitionProvider : IDocumentRecognitionService
{
    private readonly HttpClient _http;
    private readonly RecognitionOptions _options;
    private readonly ILogger<LocalOllamaRecognitionProvider> _logger;

    private static readonly (string Key, string Label)[] FieldMap =
    {
        ("full_name", "ФИО"),
        ("document_type", "Тип документа"),
        ("issue_date", "Дата выдачи"),
        ("start_date", "Дата начала"),
        ("end_date", "Дата окончания"),
        ("validity_months", "Действует, мес."),
        ("certificate_number", "Номер справки"),
        ("medical_organization", "Мед. организация"),
        ("physical_group", "Физкультурная группа"),
        ("health_group", "Группа здоровья"),
        ("admitted", "Допуск (да/нет)"),
        ("restrictions", "Заключение/ограничения"),
        ("has_stamp", "Печать обнаружена"),
        ("has_signature", "Подпись обнаружена"),
    };

    public LocalOllamaRecognitionProvider(HttpClient http, RecognitionOptions options, ILogger<LocalOllamaRecognitionProvider> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public async Task<RecognitionResult> RecognizeAsync(ScanInput scan, CancellationToken cancellationToken = default)
    {
        var imageBytes = scan.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            ? await RenderPdfFirstPageAsync(scan.Content, cancellationToken)
            : scan.Content;

        var requestBody = new
        {
            model = _options.VisionModel,
            prompt = BuildPrompt(),
            images = new[] { Convert.ToBase64String(imageBytes) },
            stream = false,
            format = "json",
            options = new { temperature = 0 }
        };

        using var response = await _http.PostAsJsonAsync(
            $"{_options.OllamaUrl.TrimEnd('/')}/api/generate", requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollama = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
        var rawJson = ollama?.Response ?? "{}";

        var fields = ParseFields(rawJson);
        _logger.LogInformation("ИИ-распознавание ({Model}): извлечено полей {Count}", _options.VisionModel, fields.Count);

        return new RecognitionResult(RequiresManualReview: true, fields, rawJson);
    }

    private static string BuildPrompt() =>
        """
        Ты распознаёшь российскую медицинскую справку для допуска к физкультуре. Верни ТОЛЬКО JSON с ключами:
        full_name: ФИО студента (поле «Фамилия, имя, отчество» / «Выдана гр. …»). Рукопись читай максимально внимательно.
        document_type: одно из "086/у", "бассейн", "освобождение", иначе краткое описание.
        issue_date: дата ВЫДАЧИ справки в ДД.ММ.ГГГГ, ТОЛЬКО если явно есть «выдана»/«дата выдачи».
          НЕ бери дату рождения (после ФИО, «г.р.», «дата рождения») и НЕ дату лицензии/ОГРН/«от …». Сомневаешься — null.
        validity_months: число месяцев, если написано «действительна N месяцев», иначе null.
        start_date, end_date: явный срок «действует с … по …» в ДД.ММ.ГГГГ, иначе null.
        certificate_number: номер справки (после «СПРАВКА №» / «МЕДИЦИНСКАЯ СПРАВКА №»). НЕ лицензия, НЕ ОГРН, НЕ ИНН.
        medical_organization: название клиники.
        physical_group: ТОЛЬКО если явно «физкультурная группа: …» или «основная/подготовительная/специальная медицинская группа».
          Фраза «по группе А/Б» про бассейн — это НЕ физкультурная группа → null.
        health_group: "I"/"II"/"III"/"IV"/"V" или null.
        admitted: true если «допущен», false если «не допущен», иначе null.
        restrictions: заключение/ограничения кратко, без диагноза.
        has_stamp: true/false (есть ли печать), has_signature: true/false (есть ли подпись/росчерк врача).
        Используй настоящий JSON null (без кавычек), не строку "null". Никакого текста кроме JSON.
        """;

    private static IReadOnlyList<RecognizedField> ParseFields(string json)
    {
        var result = new List<RecognizedField>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var (key, label) in FieldMap)
            {
                if (!root.TryGetProperty(key, out var prop)) continue;
                var value = prop.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => "да",
                    JsonValueKind.False => "нет",
                    JsonValueKind.String => prop.GetString(),
                    _ => prop.ToString()
                };
                result.Add(new RecognizedField(label, value, 0.0));
            }
        }
        catch (JsonException)
        {
            // модель вернула невалидный JSON — оставляем сырой текст в RawJson, полей нет
        }
        return result;
    }

    private async Task<byte[]> RenderPdfFirstPageAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        var baseName = Path.Combine(Path.GetTempPath(), "reu-ocr-" + Guid.NewGuid().ToString("N"));
        var pdfPath = baseName + ".pdf";
        var pngPath = baseName + ".png";
        await File.WriteAllBytesAsync(pdfPath, pdf, cancellationToken);

        try
        {
            var psi = new ProcessStartInfo("pdftoppm",
                $"-png -singlefile -r {_options.PdfRenderDpi} -f 1 -l 1 \"{pdfPath}\" \"{baseName}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить pdftoppm (poppler не установлен?).");
            await process.WaitForExitAsync(cancellationToken);

            if (!File.Exists(pngPath))
                throw new InvalidOperationException("pdftoppm не отрендерил PDF в изображение.");

            return await File.ReadAllBytesAsync(pngPath, cancellationToken);
        }
        finally
        {
            TryDelete(pdfPath);
            TryDelete(pngPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }
}
