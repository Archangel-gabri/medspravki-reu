using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Локальный офлайн-провайдер распознавания: PDF/изображение → vision-LLM через Ollama
/// (в периметре РЭУ / на ПК с GPU). Никаких внешних облаков для медданных (152-ФЗ).
/// Двухэтапно: стадия 1 — общий проход по всей справке; стадия 2 — модель сама находит ключевые
/// поля (дата/номер/группа), мы вырезаем их строку с увеличением и перечитываем (точнее на рукописи).
/// </summary>
public sealed class LocalOllamaRecognitionProvider : IDocumentRecognitionService
{
    private readonly HttpClient _http;
    private readonly RecognitionOptions _options;
    private readonly ILogger<LocalOllamaRecognitionProvider> _logger;

    // Поля, которые модель чаще всего портит на рукописи и которые мы уточняем зумом.
    private static readonly string[] ZoomFields = { "issue_date", "number", "health_group" };

    private static readonly (string Key, string Label)[] FieldMap =
    {
        ("full_name", "ФИО"),
        ("birth_date", "Дата рождения"),
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
        ("electronic_signature", "Электронная подпись"),
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
        // Стадия 1: общий проход по ВСЕЙ справке (все страницы одним запросом) → черновик полей.
        var pages = IsPdf(scan.ContentType)
            ? await RenderPdfAllPagesAsync(scan.Content, 1000, cancellationToken)
            : new List<byte[]> { scan.Content };
        pages = await CleanPagesAsync(pages, cancellationToken);   // предобработка (контраст/резкость)

        var stage1Json = await CallOllamaAsync(BuildPrompt(), pages, json: true, numCtx: 8192, temperature: 0, cancellationToken);

        var finalJson = stage1Json;
        IReadOnlyList<string> lowConfidence = Array.Empty<string>();
        if (_options.TwoStage)
        {
            try
            {
                (finalJson, lowConfidence) = await RefineByZoomAsync(stage1Json, scan, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Двухэтапное уточнение (зум) не удалось — оставляю результат стадии 1");
            }
        }

        var fields = ParseFields(finalJson);
        _logger.LogInformation("ИИ-распознавание ({Model}): полей {Count}, two-stage={TwoStage}, неуверенных {Low}",
            _options.VisionModel, fields.Count, _options.TwoStage, lowConfidence.Count);

        return new RecognitionResult(RequiresManualReview: true, fields, finalJson, lowConfidence);
    }

    public async Task<string?> RecognizeFieldAsync(byte[] imageBytes, string fieldPrompt, CancellationToken cancellationToken = default)
    {
        var raw = await CallOllamaAsync(fieldPrompt, new[] { imageBytes }, json: false, numCtx: 4096, temperature: 0, cancellationToken);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    // ---------- Стадия 2: авто-зум по ключевым полям ----------
    // Зум = модель ФОКУСИРУЕТСЯ на одном поле за раз (надёжнее, чем пиксельный кроп, который теряет
    // контекст и портит чтение). По каждому ключевому полю — отдельный точечный запрос по каждой странице.
    private sealed record Candidate(int Page, int Y, string Value);

    private async Task<(string Json, IReadOnlyList<string> LowConfidence)> RefineByZoomAsync(string stage1Json, ScanInput scan, CancellationToken ct)
    {
        var root = JsonNode.Parse(SafeJson(stage1Json)) as JsonObject ?? new JsonObject();
        var uncertain = new List<string>();

        var pages = IsPdf(scan.ContentType)
            ? await RenderPdfAllPagesAsync(scan.Content, 1600, ct)
            : new List<byte[]> { scan.Content };
        pages = await CleanPagesAsync(pages, ct);

        foreach (var field in ZoomFields)
        {
            // 1) Локализуем поле по страницам (по одному чтению), выбираем целевую страницу.
            var candidates = new List<Candidate>();
            for (var pi = 0; pi < pages.Count; pi++)
            {
                var (val, y) = await ReadFieldAsync(pages[pi], field, temperature: 0, ct);
                if (val is not null) candidates.Add(new Candidate(pi, y, val));
            }
            if (candidates.Count == 0) continue;

            // Дата выдачи — внизу/на обороте у подписи врача (не лицензия из шапки): кандидат с самой
            // поздней страницы и ниже по листу. Остальные поля — первый валидный.
            var best = field == "issue_date"
                ? candidates.OrderByDescending(c => c.Page).ThenByDescending(c => c.Y).First()
                : candidates[0];

            var chosen = best.Value;
            var confident = true;

            if (field == "issue_date" && _options.VoteCount > 1)
            {
                // 2) Голосование на целевой странице: ещё несколько чтений (temp>0) + первое.
                var votes = new List<string> { best.Value };
                for (var k = 1; k < _options.VoteCount; k++)
                {
                    var (v, _) = await ReadFieldAsync(pages[best.Page], field, temperature: 0.45, ct);
                    if (v is not null) votes.Add(v);
                }
                var (top, count) = TopVote(votes);
                chosen = top ?? best.Value;
                confident = count * 2 > votes.Count;   // строгое большинство голосов
            }
            else
            {
                // Не уверены, если стадия 1 и стадия 2 разошлись.
                var stage1Val = root[MapKey(field)]?.ToString();
                confident = string.IsNullOrWhiteSpace(stage1Val) || SameValue(field, stage1Val, chosen);
            }

            root[MapKey(field)] = chosen;
            if (!confident) uncertain.Add(FieldLabel(field));
            _logger.LogInformation("Зум-уточнение {Field} → {Value} (уверенно={Conf})", field, chosen, confident);
        }

        return (root.ToJsonString(), uncertain);
    }

    private async Task<(string? Value, int Y)> ReadFieldAsync(byte[] pageBytes, string field, double temperature, CancellationToken ct)
    {
        var json = await CallOllamaAsync(LocatePromptFor(field), new[] { pageBytes }, json: true, numCtx: 8192, temperature, ct);
        if (JsonNode.Parse(SafeJson(json)) is not JsonObject o) return (null, 0);
        return (Validate(field, o["value"]?.ToString()), ExtractBbox(o)?.y1 ?? 0);
    }

    private static (string? Top, int Count) TopVote(IReadOnlyList<string> votes)
    {
        if (votes.Count == 0) return (null, 0);
        var g = votes.GroupBy(v => v).OrderByDescending(x => x.Count()).First();
        return (g.Key, g.Count());
    }

    private static bool SameValue(string field, string a, string b)
    {
        if (field == "health_group") return RomanHealth(a) == RomanHealth(b);
        if (field == "number")
        {
            var da = new string(a.Where(char.IsDigit).ToArray());
            var db = new string(b.Where(char.IsDigit).ToArray());
            return da.Length > 0 && (da == db || da.EndsWith(db) || db.EndsWith(da));
        }
        var na = Validate(field, a);
        var nb = Validate(field, b);
        return na is not null && nb is not null && na.Equals(nb, StringComparison.OrdinalIgnoreCase);
    }

    // Группа здоровья к канону (римская): «первый/1/I» → "I" и т.д. (длинные раньше коротких).
    private static string RomanHealth(string v)
    {
        var u = (v ?? "").ToUpperInvariant().Replace('Ё', 'Е');
        if (u.Contains("ПЯТ") || u.Contains("5")) return "V";
        if (u.Contains("ЧЕТВ") || u.Contains("4") || u.Contains("IV")) return "IV";
        if (u.Contains("ТРЕТ") || u.Contains("3") || u.Contains("III")) return "III";
        if (u.Contains("ВТОР") || u.Contains("2") || u.Contains("II")) return "II";
        if (u.Contains("ПЕРВ") || u.Contains("1") || u.Contains("I")) return "I";
        if (u.Contains("V")) return "V";
        return u;
    }

    private static string FieldLabel(string field) => field switch
    {
        "issue_date" => "дата выдачи",
        "number" => "номер справки",
        "health_group" => "группа здоровья",
        _ => field
    };

    // Предобработка изображений: нормализация контраста + резкость (без агрессивного бинаризации/дескью — рискованно на фото).
    private async Task<List<byte[]>> CleanPagesAsync(List<byte[]> pages, CancellationToken ct)
    {
        if (!_options.Preprocess) return pages;
        var result = new List<byte[]>(pages.Count);
        foreach (var p in pages) result.Add(await CleanImageAsync(p, ct) ?? p);
        return result;
    }

    private async Task<byte[]?> CleanImageAsync(byte[] img, CancellationToken ct)
    {
        var dir = NewTempDir();
        try
        {
            var inp = Path.Combine(dir, "in");
            var outp = Path.Combine(dir, "out.jpg");
            await File.WriteAllBytesAsync(inp, img, ct);
            var args = $"\"{inp}\" -auto-orient -normalize -brightness-contrast 0x8 -sharpen 0x0.7 \"{outp}\"";
            if (!await RunAsync("magick", args, ct) && !await RunAsync("convert", args, ct)) return null;
            return File.Exists(outp) ? await File.ReadAllBytesAsync(outp, ct) : null;
        }
        finally { TryDelete(dir); }
    }

    private static string LocatePromptFor(string field) => field switch
    {
        "issue_date" =>
            "Это страница российской медсправки. Найди дату ВЫДАЧИ справки (внизу/на обороте у строки " +
            "«Дата выдачи справки» и подписи врача; это НЕ дата лицензии медорганизации в шапке, " +
            "НЕ даты анализов/осмотров, НЕ дата рождения). Верни JSON {\"value\":\"ДД.ММ.ГГГГ\",\"bbox\":[x1,y1,x2,y2]}. " +
            "Если на этой странице её нет — {\"value\":null}. bbox — пиксели этого изображения. Только JSON.",
        "number" =>
            "Это страница российской медсправки. Найди номер справки (после «Справка №»; НЕ лицензия, НЕ ОГРН, НЕ ИНН). " +
            "Верни JSON {\"value\":\"...\",\"bbox\":[x1,y1,x2,y2]}. Если на странице нет — {\"value\":null}. Только JSON.",
        "health_group" =>
            "Это страница российской медсправки. Найди группу здоровья (римская цифра I/II/III/IV/V, " +
            "может быть словом «первая/вторая…»). Верни JSON {\"value\":\"I\",\"bbox\":[x1,y1,x2,y2]}. " +
            "Если на этой странице нет — {\"value\":null}. Только JSON.",
        _ => "Верни JSON {\"value\":null}."
    };

    private static string FieldPrompt(string field) => field switch
    {
        "issue_date" => "Это увеличенная строка «Дата выдачи справки» из медсправки. Прочитай дату выдачи и верни строго в формате ДД.ММ.ГГГГ, только дату, без слов.",
        "number" => "Это увеличенный фрагмент медсправки с номером справки. Верни только номер, без слов.",
        "health_group" => "Это увеличенный фрагмент медсправки с группой здоровья. Верни только римскую цифру: I, II, III, IV или V.",
        _ => "Прочитай текст на фрагменте и верни как есть, без комментариев."
    };

    private static string MapKey(string field) => field == "number" ? "certificate_number" : field;

    private static (int x1, int y1, int x2, int y2)? ExtractBbox(JsonObject fo)
    {
        var node = fo["bbox"] ?? fo["bbox_2d"] ?? fo["box"];
        if (node is null) return null;
        var nums = Regex.Matches(node.ToJsonString(), @"-?\d+").Select(m => int.Parse(m.Value)).ToList();
        if (nums.Count < 4) return null;
        var (x1, y1, x2, y2) = (nums[0], nums[1], nums[2], nums[3]);
        if (x2 < x1) (x1, x2) = (x2, x1);
        if (y2 < y1) (y1, y2) = (y2, y1);
        return (x1, y1, x2, y2);
    }

    private static string? Validate(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Trim('"', '«', '»', '.', ' ', '\n', '\r');
        if (v.Length == 0 || v.Equals("нет", StringComparison.OrdinalIgnoreCase)) return null;

        switch (field)
        {
            case "issue_date":
                var m = Regex.Match(v, @"(\d{1,2})\s*[.,/\-]\s*(\d{1,2})\s*[.,/\-]\s*(\d{2,4})");
                if (!m.Success) return null;
                int d = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value), y = int.Parse(m.Groups[3].Value);
                if (y < 100) y += 2000;
                if (d < 1 || d > 31 || mo < 1 || mo > 12 || y < 2000 || y > 2100) return null;
                return $"{d:00}.{mo:00}.{y:0000}";
            case "number":
                return Regex.IsMatch(v, @"\d") ? v : null;
            case "health_group":
                var u = v.ToUpperInvariant().Replace('Ё', 'Е');
                return Regex.IsMatch(u, @"\b(IV|III|II|I|V)\b") || Regex.IsMatch(u, @"[1-5]")
                       || u.Contains("ПЕРВ") || u.Contains("ВТОР") || u.Contains("ТРЕТ") || u.Contains("ЧЕТВ") || u.Contains("ПЯТ")
                    ? v : null;
            default:
                return v;
        }
    }

    // Вырезает строку поля широкой полосой (запас по горизонтали — чтобы не обрезать день/подпись) и увеличивает.
    private async Task<byte[]?> CropBandAsync(byte[] pageBytes, (int x1, int y1, int x2, int y2) b, CancellationToken ct)
    {
        var dir = NewTempDir();
        try
        {
            var src = Path.Combine(dir, "page.jpg");
            await File.WriteAllBytesAsync(src, pageBytes, ct);

            var px = Math.Max(0, b.x1 - 400);
            var pw = (b.x2 - b.x1) + 800;
            var py = Math.Max(0, b.y1 - 30);
            var ph = (b.y2 - b.y1) + 60;
            if (pw < 24 || ph < 12) return null;

            var outp = Path.Combine(dir, "crop.jpg");
            var geom = string.Format(CultureInfo.InvariantCulture, "{0}x{1}+{2}+{3}", pw, ph, px, py);
            var args = $"\"{src}\" -crop {geom} +repage -resize \"1300x>\" -sharpen 0x1 \"{outp}\"";
            if (!await RunAsync("magick", args, ct) && !await RunAsync("convert", args, ct)) return null;
            return File.Exists(outp) ? await File.ReadAllBytesAsync(outp, ct) : null;
        }
        finally { TryDelete(dir); }
    }

    // ---------- Общий вызов Ollama ----------
    private async Task<string> CallOllamaAsync(string prompt, IReadOnlyList<byte[]> images, bool json, int numCtx, double temperature, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.VisionModel,
            ["prompt"] = prompt,
            ["images"] = images.Select(Convert.ToBase64String).ToArray(),
            ["stream"] = false,
            ["options"] = new Dictionary<string, object?> { ["temperature"] = temperature, ["num_ctx"] = numCtx }
        };
        if (json) body["format"] = "json";

        // Буферизованный StringContent (Content-Length), без Expect:100-continue — иначе Ollama 400 на multi-image.
        var payload = JsonSerializer.Serialize(body);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.OllamaUrl.TrimEnd('/')}/api/generate")
        {
            Content = content
        };
        request.Headers.ExpectContinue = false;

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode}: {errBody}");
        }

        var ollama = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
        return ollama?.Response ?? (json ? "{}" : string.Empty);
    }

    private static string BuildPrompt() =>
        """
        Перед тобой РОССИЙСКАЯ медицинская справка (часто форма 086/у). Она может быть на НЕСКОЛЬКИХ
        изображениях — лицевая и ОБОРОТНАЯ стороны. Собери данные со ВСЕХ изображений и верни ОДИН JSON:
        full_name: ФИО студента (поле «Фамилия, имя, отчество» / «Выдана гр. …»). Рукопись читай внимательно.
        birth_date: ДАТА РОЖДЕНИЯ студента в ДД.ММ.ГГГГ (п.2 «Дата рождения», либо дата сразу после ФИО
          в строке «Выдана гр. ФИО, ДД.ММ.ГГГГ»). Это НЕ дата выдачи справки — извлекай её ОТДЕЛЬНО.
        document_type: одно из "086/у", "бассейн", "освобождение", иначе краткое описание.
        place_of_study: место учёбы/работы (п.4), напр. «РЭУ им. Г.В. Плеханова».
        past_illnesses: перенесённые заболевания (п.5), иначе null.
        issue_date: «Дата выдачи справки» в ДД.ММ.ГГГГ (ВНИЗУ справки/на обороте, рядом с подписью врача).
          Она ПОЗЖЕ дат осмотров врачей и обычно совпадает с ними по году. НЕ дата рождения (birth_date),
          НЕ дата лицензии из шапки. Сомневаешься — null.
        validity_months: число месяцев из «действительна N месяцев», иначе null.
        start_date, end_date: явный срок «действует с … по …» в ДД.ММ.ГГГГ, иначе null.
        certificate_number: номер справки (после «СПРАВКА №»). НЕ лицензия, НЕ ОГРН, НЕ ИНН.
        medical_organization: название клиники.
        health_group: ГЛАВНОЕ поле — группа здоровья РИМСКОЙ цифрой (I/II/III/IV/V). На справках её и пишут
          («I группа», «первая группа здоровья»). Если указано «основная группа … I группа» — бери "I".
        physical_group: физкультурная группа (Основная/Подготовительная/Специальная А/Специальная Б/Освобождение)
          ТОЛЬКО если отдельно явно указана словом; иначе null.
        fit_for_pe: true если к физкультуре допущен / противопоказаний нет; false если не допущен/освобождён; иначе null.
        restrictions: заключение/ограничения кратко, без диагноза.
        has_stamp: true/false (есть ли физические печати), has_signature: true/false (есть ли подписи врачей).
        electronic_signature: true, если на документе есть «Документ подписан электронной подписью» /
          «Сертификат» / «электронной подписью». У такой справки ФИЗИЧЕСКОЙ печати может НЕ быть — это нормально.
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

    // Рендерит ВСЕ страницы PDF в JPEG заданной ширины (px). Низкая ширина для стадии 1 (лимит multi-image),
    // повыше — для кропа стадии 2.
    private async Task<List<byte[]>> RenderPdfAllPagesAsync(byte[] pdf, int width, CancellationToken cancellationToken)
    {
        var dir = NewTempDir();
        var pdfPath = Path.Combine(dir, "in.pdf");
        await File.WriteAllBytesAsync(pdfPath, pdf, cancellationToken);

        try
        {
            var psi = new ProcessStartInfo("pdftoppm",
                $"-jpeg -scale-to-x {width} -scale-to-y -1 \"{pdfPath}\" \"{Path.Combine(dir, "p")}\"")
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
        finally { TryDelete(dir); }
    }

    private static bool IsPdf(string contentType) => contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);
    private static string SafeJson(string s) => string.IsNullOrWhiteSpace(s) ? "{}" : s;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reu-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private async Task<bool> RunAsync(string tool, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(tool, args) { RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Команда {Tool} не выполнена", tool);
            return false;
        }
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }
}
