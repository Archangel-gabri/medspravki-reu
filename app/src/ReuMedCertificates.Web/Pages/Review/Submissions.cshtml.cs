using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Scans;

namespace ReuMedCertificates.Web.Pages.Review;

/// <summary>Очередь медработника: сканы, загруженные студентами, по которым ещё не оформлена справка.</summary>
public class SubmissionsModel : PageModel
{
    private readonly IScanService _scans;

    public SubmissionsModel(IScanService scans) => _scans = scans;

    public IReadOnlyList<PendingScanItem> Items { get; private set; } = Array.Empty<PendingScanItem>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => Items = await _scans.ListPendingStudentScansAsync(cancellationToken);
}
