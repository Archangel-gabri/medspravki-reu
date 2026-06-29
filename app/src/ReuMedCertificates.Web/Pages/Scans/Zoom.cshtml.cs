using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Scans;
using ReuMedCertificates.Application.Students;

namespace ReuMedCertificates.Web.Pages.Scans;

public class ZoomModel : PageModel
{
    private readonly IScanService _scans;
    private readonly IStudentService _students;
    private readonly IRegionRecognizer _regions;

    public ZoomModel(IScanService scans, IStudentService students, IRegionRecognizer regions)
    {
        _scans = scans;
        _students = students;
        _regions = regions;
    }

    public Guid StudentId { get; private set; }
    public Guid ScanId { get; private set; }
    public string StudentName { get; private set; } = string.Empty;
    public int PageCount { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid scanId, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null) return NotFound();

        var detail = await _scans.GetAsync(scanId, cancellationToken);
        if (detail is null || detail.StudentId != id) return NotFound();

        StudentId = id;
        ScanId = scanId;
        StudentName = student.FullName;

        var content = await _scans.OpenAsync(scanId, cancellationToken);
        if (content is not null)
        {
            await using var stream = content.Stream;
            var bytes = await ReadAllAsync(stream, cancellationToken);
            PageCount = await _regions.GetPageCountAsync(bytes, content.ContentType, cancellationToken);
        }
        return Page();
    }

    // Картинка отрендеренной страницы скана (для показа/выделения в браузере).
    public async Task<IActionResult> OnGetPageAsync(Guid id, Guid scanId, int n, CancellationToken cancellationToken)
    {
        var detail = await _scans.GetAsync(scanId, cancellationToken);
        if (detail is null || detail.StudentId != id) return NotFound();

        var content = await _scans.OpenAsync(scanId, cancellationToken);
        if (content is null) return NotFound();

        await using var stream = content.Stream;
        var bytes = await ReadAllAsync(stream, cancellationToken);
        var rendered = await _regions.RenderPageAsync(bytes, content.ContentType, n <= 0 ? 1 : n, cancellationToken);
        return File(rendered.Jpeg, "image/jpeg");
    }

    public record RegionRequest(int Page, double X, double Y, double W, double H, string Field);

    // Распознать выделенную область (двухэтапное распознавание).
    public async Task<IActionResult> OnPostRecognizeAsync(Guid id, Guid scanId, [FromForm] RegionRequest req, CancellationToken cancellationToken)
    {
        var detail = await _scans.GetAsync(scanId, cancellationToken);
        if (detail is null || detail.StudentId != id) return NotFound();

        var content = await _scans.OpenAsync(scanId, cancellationToken);
        if (content is null) return NotFound();

        try
        {
            await using var stream = content.Stream;
            var bytes = await ReadAllAsync(stream, cancellationToken);
            var value = await _regions.RecognizeRegionAsync(
                bytes, content.ContentType, req.Page,
                new RegionRect(req.X, req.Y, req.W, req.H), req.Field ?? "text", cancellationToken);
            return new JsonResult(new { ok = true, value });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
