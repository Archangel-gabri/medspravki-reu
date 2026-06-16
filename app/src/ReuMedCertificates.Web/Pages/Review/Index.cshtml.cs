using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Certificates;

namespace ReuMedCertificates.Web.Pages.Review;

public class IndexModel : PageModel
{
    private readonly ICertificateService _certificates;

    public IndexModel(ICertificateService certificates) => _certificates = certificates;

    public IReadOnlyList<ReviewItem> Items { get; private set; } = Array.Empty<ReviewItem>();

    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => Items = await _certificates.GetReviewQueueAsync(cancellationToken);

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await _certificates.ApproveAsync(id, cancellationToken); }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        try { await _certificates.RejectAsync(id, reason ?? string.Empty, cancellationToken); }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return RedirectToPage();
    }
}
