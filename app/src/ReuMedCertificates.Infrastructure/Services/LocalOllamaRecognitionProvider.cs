using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
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
        ("place_of_study", "Место учёбы"),
        ("past_illnesses", "Перенесённые заболевания"),
        ("issue_date", "Дата выдачи"),
        ("start_date", "Дата начала"),
        ("end_date", "Дата окончания"),
        ("validity_months", "Действует, мес."),
        ("certificate_number", "Номер справки"),
        ("medical_organization", "Мед. организация"),
        ("physical_group", "Физкультурная группа"),
        ("health_group", "Группа здоровья"),
        ("fit_for_pe", "Годен к физкультуре"),
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
        // PDF → все страницы (двухсторонняя справка: лицо + оборот), фото → одно изображение.
        var pages = scan.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            ? await RenderPdfAllPagesAsync(scan.Content, cancellationToken)
            : new List<byte[]> { scan.Content };

        var requestBody = new
        {
            model = _options.VisionModel,
            prompt = BuildPrompt(),
            images = pages.Select(Convert.ToBase64String).ToArray(),
            stream = false,
            format = "json",
            // num_ctx поднят: двухстраничная справка (2 изображения) + промпт не влезают в дефолтные 4096.
            options = new { temperature = 0, num_ctx = 8192 }
        };

        // Важно: отправляем буферизованным StringContent (с Content-Length), а не PostAsJsonAsync
        // (тот шлёт chunked-потоком — Ollama отвечает 400 на multi-image запрос). Без Expect:100-continue.
        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.OllamaUrl.TrimEnd('/')}/api/generate")
        {
            Content = content
        };
        request.Headers.ExpectContinue = false;

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode}: {errBody}");
        }

        var ollama = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
        var rawJson = ollama?.Response ?? "{}";

        var fields = ParseFields(rawJson);
        _logger.LogInformation("ИИ-распознавание ({Model}): извлечено полей {Count}", _options.VisionModel, fields.Count);

        return new RecognitionResult(RequiresManualReview: true, fields, rawJson);
    }

    private static string BuildPrompt() =>
        """
        Перед тобой РОССИЙСКАЯ медицинская справка (часто форма 086/у). Она может быть на НЕСКОЛЬКИХ
        изображениях — лицевая и ОБОРОТНАЯ стороны. Собери данные со ВСЕХ изображений и верни ОДИН JSON:
        full_name: ФИО студента (поле «Фамилия, имя, отчество» / «Выдана гр. …»). Рукопись читай внимательно.
        document_type: одно из "086/у", "бассейн", "освобождение", иначе краткое описание.
        place_of_study: место учёбы/работы (п.4), напр. «РЭУ им. Г.В. Плеханова».
        past_illnesses: перенесённые заболевания (п.5), иначе null.
        issue_date: «Дата выдачи справки» в ДД.ММ.ГГГГ (обычно на обороте). НЕ дата рождения, НЕ дата лицензии. Сомневаешься — null.
        validity_months: число месяцев из «действительна N месяцев», иначе null.
        start_date, end_date: явный срок «действует с … по …» в ДД.ММ.ГГГГ, иначе null.
        certificate_number: номер справки (после «СПРАВКА №»). НЕ лицензия, НЕ ОГРН, НЕ ИНН.
        medical_organization: название клиники.
        physical_group: физкультурная группа из ЗАКЛЮЧЕНИЯ (Основная/Подготовительная/Специальная А/Специальная Б/Освобождение) или null.
        health_group: "I"/"II"/"III"/"IV"/"V" или null.
        fit_for_pe: true если к физкультуре допущен / противопоказаний нет; false если не допущен/освобождён; иначе null.
        restrictions: заключение/ограничения кратко, без диагноза.
        has_stamp: true/false (есть ли печати), has_signature: true/false (есть ли подписи врачей).
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

    // Рендерит ВСЕ страницы PDF (двухсторонняя справка) в JPEG. Низкий dpi (≈120) — иначе
    // суммарный запрос к Ollama с несколькими изображениями превышает лимит (HTTP 400).
    private async Task<List<byte[]>> RenderPdfAllPagesAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), "reu-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pdfPath = Path.Combine(dir, "in.pdf");
        await File.WriteAllBytesAsync(pdfPath, pdf, cancellationToken);

        try
        {
            // Ширина страниц фиксируется ~1000px (-scale-to-x): иначе суммарный multi-image
            // запрос к Ollama превышает лимит и возвращает HTTP 400.
            var psi = new ProcessStartInfo("pdftoppm",
                $"-jpeg -scale-to-x 1000 -scale-to-y -1 \"{pdfPath}\" \"{Path.Combine(dir, "p")}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить pdftoppm (poppler не установлен?).");
            await process.WaitForExitAsync(cancellationToken);

            var files = Directory.GetFiles(dir, "p-*.jpg").OrderBy(f => f).ToList();
            if (files.Count == 0)
                throw new InvalidOperationException("pdftoppm не отрендерил PDF в изображения.");

            var result = new List<byte[]>();
            foreach (var f in files.Take(4))   // страховка: не больше 4 страниц
                result.Add(await File.ReadAllBytesAsync(f, cancellationToken));
            return result;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }
}
