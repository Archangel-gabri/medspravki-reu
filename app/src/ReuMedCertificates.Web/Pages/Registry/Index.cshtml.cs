using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Lookups;
using ReuMedCertificates.Application.Registry;

namespace ReuMedCertificates.Web.Pages.Registry;

public class IndexModel : PageModel
{
    private const int PageSize = 25;

    private readonly IRegistryQueryService _registry;
    private readonly ILookupService _lookups;

    public IndexModel(IRegistryQueryService registry, ILookupService lookups)
    {
        _registry = registry;
        _lookups = lookups;
    }

    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }
    [BindProperty(SupportsGet = true, Name = "dept")] public Guid? DepartmentId { get; set; }
    [BindProperty(SupportsGet = true, Name = "course")] public short? Course { get; set; }
    [BindProperty(SupportsGet = true, Name = "group")] public Guid? StudyGroupId { get; set; }
    [BindProperty(SupportsGet = true, Name = "teacher")] public Guid? TeacherId { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public RegistryPage Result { get; private set; } = new(Array.Empty<RegistryRow>(), 0, 1, PageSize);
    public IReadOnlyList<DepartmentLookup> Departments { get; private set; } = Array.Empty<DepartmentLookup>();
    public IReadOnlyList<GroupLookup> Groups { get; private set; } = Array.Empty<GroupLookup>();
    public IReadOnlyList<TeacherLookup> Teachers { get; private set; } = Array.Empty<TeacherLookup>();

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Result.Total / (double)PageSize));

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Departments = await _lookups.GetDepartmentsAsync(cancellationToken);
        Teachers = await _lookups.GetTeachersAsync(cancellationToken);
        Groups = await _lookups.GetGroupsAsync(DepartmentId, cancellationToken);

        var filter = new RegistryFilter(
            Query, DepartmentId, Course, StudyGroupId, TeacherId,
            Math.Max(1, PageNumber), PageSize);

        Result = await _registry.SearchAsync(filter, cancellationToken);
    }
}
