using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Lookups;
using ReuMedCertificates.Application.Registry;
using ReuMedCertificates.Web.Helpers;

namespace ReuMedCertificates.Web.Pages.BeforeClass;

public class IndexModel : PageModel
{
    private readonly IRegistryQueryService _registry;
    private readonly ILookupService _lookups;

    public IndexModel(IRegistryQueryService registry, ILookupService lookups)
    {
        _registry = registry;
        _lookups = lookups;
    }

    [BindProperty(SupportsGet = true, Name = "group")] public Guid? StudyGroupId { get; set; }

    public IReadOnlyList<GroupLookup> Groups { get; private set; } = Array.Empty<GroupLookup>();
    public IReadOnlyList<RegistryRow> Rows { get; private set; } = Array.Empty<RegistryRow>();
    public string? GroupName { get; private set; }

    public int CountNoClearance { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Groups = await _lookups.GetGroupsAsync(null, cancellationToken);

        if (StudyGroupId is { } groupId)
        {
            GroupName = Groups.FirstOrDefault(g => g.Id == groupId)?.Name;

            var page = await _registry.SearchAsync(
                new RegistryFilter(StudyGroupId: groupId, Page: 1, PageSize: 200), cancellationToken);

            Rows = page.Rows
                .OrderBy(r => StatusDisplay.Severity(r.Status, r.PhysicalGroup))
                .ThenBy(r => r.FullName)
                .ToList();

            CountNoClearance = Rows.Count(r => StatusDisplay.Severity(r.Status, r.PhysicalGroup) <= 2);
        }
    }
}
