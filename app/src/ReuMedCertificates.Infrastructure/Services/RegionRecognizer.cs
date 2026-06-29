using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Двухэтапное распознавание: рендерит страницу скана, вырезает выделенную область С УВЕЛИЧЕНИЕМ
/// и шлёт vision-модели узкий запрос по одному полю. Поппинг через poppler (pdftoppm/pdfinfo) и ImageMagick.
/// </summary>
public sealed class RegionRecognizer : IRegionRecognizer
{
    private readonly IDocumentRecognitionService _recognition;
    private readonly ILogger<RegionRecognizer> _logger;

    public RegionRecognizer(IDocumentRecognitionService recognition, ILogger<RegionRecognizer> logger)
    {
        _recognition = recognition;
        _logger = logger;
    }

    public async Task<RenderedPage> RenderPageAsync(byte[] file, string contentType, int page, CancellationToken cancellationToken = default)
    {
        var jpeg = await RenderPageJpegAsync(file, contentType, Math.Max(1, page), 1500, cancellationToken);
        var count = await GetPageCountAsync(file, contentType, cancellationToken);
        return new RenderedPage(jpeg, count);
    }

    public async Task<int> GetPageCountAsync(byte[] file, string contentType, CancellationToken cancellationToken = default)
    {
        if (!IsPdf(contentType)) return 1;

        var dir = NewTempDir();
        try
        {
            var pdf = Path.Combine(dir, "in.pdf");
            await File.WriteAllBytesAsync(pdf, file, cancellationToken);
            var (ok, stdout) = await RunCaptureAsync("pdfinfo", $"\"{pdf}\"", cancellationToken);
            if (ok)
            {
                foreach (var line in stdout.Split('\n'))
                    if (line.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line[6..].Trim(), out var n) && n > 0)
                        return n;
            }
            return 1;
        }
        finally { TryDelete(dir); }
    }

    public async Task<string?> RecognizeRegionAsync(byte[] file, string contentType, int page, RegionRect rect, string fieldKey, CancellationToken cancellationToken = default)
    {
        // Рендерим страницу крупнее (для качества кропа), затем вырезаем область и увеличиваем.
        var pageJpeg = await RenderPageJpegAsync(file, contentType, Math.Max(1, page), 2000, cancellationToken);
        var crop = await CropAsync(pageJpeg, rect, cancellationToken);
        if (crop is null) return null;

        var prompt = PromptFor(fieldKey);
        var raw = await _recognition.RecognizeFieldAsync(crop, prompt, cancellationToken);
        return CleanAnswer(raw);
    }

    // --- Рендер страницы в JPEG ---
    private async Task<byte[]> RenderPageJpegAsync(byte[] file, string contentType, int page, int width, CancellationToken ct)
    {
        var dir = NewTempDir();
        try
        {
            if (IsPdf(contentType))
            {
                var pdf = Path.Combine(dir, "in.pdf");
                await File.WriteAllBytesAsync(pdf, file, ct);
                var prefix = Path.Combine(dir, "pg");
                var args = $"-jpeg -scale-to-x {width} -scale-to-y -1 -f {page} -l {page} \"{pdf}\" \"{prefix}\"";
                if (!await RunAsync("pdftoppm", args, ct))
                    throw new InvalidOperationException("pdftoppm недоступен (poppler не установлен?).");
                var jpg = Directory.GetFiles(dir, "pg*.jpg").OrderBy(f => f).FirstOrDefault()
                    ?? throw new InvalidOperationException("pdftoppm не отрендерил страницу.");
                return await File.ReadAllBytesAsync(jpg, ct);
            }

            // Изображение: нормализуем ориентацию и ширину через ImageMagick.
            var inImg = Path.Combine(dir, "in.img");
            var outImg = Path.Combine(dir, "out.jpg");
            await File.WriteAllBytesAsync(inImg, file, ct);
            var mArgs = $"\"{inImg}\" -auto-orient -resize \"{width}x>\" \"{outImg}\"";
            if (!await RunAsync("magick", mArgs, ct) && !await RunAsync("convert", mArgs, ct))
                throw new InvalidOperationException("ImageMagick недоступен для рендера изображения.");
            return await File.ReadAllBytesAsync(outImg, ct);
        }
        finally { TryDelete(dir); }
    }

    // --- Кроп выделенной области (доли 0..1) + увеличение ---
    private async Task<byte[]?> CropAsync(byte[] pageJpeg, RegionRect rect, CancellationToken ct)
    {
        var dir = NewTempDir();
        try
        {
            var src = Path.Combine(dir, "page.jpg");
            await File.WriteAllBytesAsync(src, pageJpeg, ct);

            var (okId, dims) = await RunCaptureAsync("magick", $"identify -format \"%w %h\" \"{src}\"", ct);
            if (!okId) (okId, dims) = await RunCaptureAsync("identify", $"-format \"%w %h\" \"{src}\"", ct);
            if (!okId) return null;

            var parts = dims.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
                return null;

            // Доли → пиксели, с защитой границ и минимальным размером.
            var px = (int)Math.Round(Clamp01(rect.X) * w);
            var py = (int)Math.Round(Clamp01(rect.Y) * h);
            var pw = (int)Math.Round(Clamp01(rect.W) * w);
            var ph = (int)Math.Round(Clamp01(rect.H) * h);
            pw = Math.Max(16, Math.Min(pw, w - px));
            ph = Math.Max(16, Math.Min(ph, h - py));

            var outImg = Path.Combine(dir, "crop.jpg");
            // Вырезаем и увеличиваем до ~1200px по ширине (но не уменьшаем) + лёгкая резкость.
            var cropGeom = string.Format(CultureInfo.InvariantCulture, "{0}x{1}+{2}+{3}", pw, ph, px, py);
            var args = $"\"{src}\" -crop {cropGeom} +repage -resize \"1200x>\" -sharpen 0x1 \"{outImg}\"";
            if (!await RunAsync("magick", args, ct) && !await RunAsync("convert", args, ct))
                return null;

            return File.Exists(outImg) ? await File.ReadAllBytesAsync(outImg, ct) : null;
        }
        finally { TryDelete(dir); }
    }

    private static string PromptFor(string fieldKey) => fieldKey switch
    {
        "issue_date" =>
            "Это увеличенный фрагмент российской медицинской справки. Прочитай дату на нём и верни СТРОГО " +
            "в формате ДД.ММ.ГГГГ, только дату, без слов. Если даты не видно — верни «нет».",
        "number" =>
            "Это фрагмент медицинской справки. Прочитай номер справки (число после «Справка №»). " +
            "Верни только номер, без слов.",
        "health_group" =>
            "Это фрагмент медицинской справки. Прочитай группу здоровья (римская цифра I, II, III, IV или V; " +
            "может быть словом «первая/вторая/третья…»). Верни только римскую цифру.",
        "name" =>
            "Это фрагмент медицинской справки. Прочитай ФИО (фамилия имя отчество). Верни только ФИО.",
        _ =>
            "Это увеличенный фрагмент медицинской справки. Прочитай весь текст на нём максимально точно " +
            "и верни как есть, без комментариев."
    };

    private static string? CleanAnswer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"', '«', '»', '.', ' ');
        return s.Length == 0 ? null : s;
    }

    private static bool IsPdf(string contentType) =>
        contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reu-zoom-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(bool ok, string stdout)> RunCaptureAsync(string tool, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(tool, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, string.Empty);
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode == 0, stdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Команда {Tool} не выполнена", tool);
            return (false, string.Empty);
        }
    }
}
