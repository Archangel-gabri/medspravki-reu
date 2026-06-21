using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>Склейка изображений в PDF через ImageMagick (magick/convert). Сохраняет порядок страниц.</summary>
public sealed class ImageMagickDocumentAssembler : IDocumentAssembler
{
    private readonly ILogger<ImageMagickDocumentAssembler> _logger;

    public ImageMagickDocumentAssembler(ILogger<ImageMagickDocumentAssembler> logger) => _logger = logger;

    public async Task<byte[]> ImagesToPdfAsync(IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default)
    {
        if (images is null || images.Count == 0)
            throw new InvalidOperationException("Нет изображений для склейки.");

        var dir = Path.Combine(Path.GetTempPath(), "reu-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var inputs = new List<string>();
            for (var i = 0; i < images.Count; i++)
            {
                var p = Path.Combine(dir, $"p{i:D2}.img");
                await File.WriteAllBytesAsync(p, images[i], cancellationToken);
                inputs.Add(p);
            }
            var outPdf = Path.Combine(dir, "out.pdf");
            // Ширину страниц ограничиваем (-resize 1000x) — потом всё равно так рендерим для ИИ; экономит размер.
            var argList = string.Join(" ", inputs.Select(p => $"\"{p}\"")) + $" -resize \"1000x>\" \"{outPdf}\"";

            if (!await RunAsync("magick", argList, cancellationToken) &&
                !await RunAsync("convert", argList, cancellationToken))
                throw new InvalidOperationException("ImageMagick (magick/convert) недоступен для склейки PDF.");

            if (!File.Exists(outPdf))
                throw new InvalidOperationException("Не удалось собрать PDF из изображений.");

            return await File.ReadAllBytesAsync(outPdf, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
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
            _logger.LogWarning(ex, "Склейка через {Tool} не удалась", tool);
            return false;
        }
    }
}
