using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Audit;

namespace ReuMedCertificates.Web.Pages.Journal;

public class IndexModel : PageModel
{
    private readonly IAuditQueryService _audit;

    public IndexModel(IAuditQueryService audit) => _audit = audit;

    public IReadOnlyList<AuditEntryView> Items { get; private set; } = Array.Empty<AuditEntryView>();

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => Items = await _audit.GetRecentAsync(200, string.IsNullOrWhiteSpace(Action) ? null : Action, cancellationToken);
}
