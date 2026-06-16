using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Lookups;
using ReuMedCertificates.Application.Students;

namespace ReuMedCertificates.Web.Pages.Students;

public class CreateModel : PageModel
{
    private readonly IStudentService _students;
    private readonly ILookupService _lookups;

    public CreateModel(IStudentService students, ILookupService lookups)
    {
        _students = students;
        _lookups = lookups;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<DepartmentLookup> Departments { get; private set; } = Array.Empty<DepartmentLookup>();
    public IReadOnlyList<GroupLookup> Groups { get; private set; } = Array.Empty<GroupLookup>();
    public IReadOnlyList<TeacherLookup> Teachers { get; private set; } = Array.Empty<TeacherLookup>();

    public Guid? DuplicateStudentId { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Укажите ФИО")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Выберите подразделение")]
        public Guid DepartmentId { get; set; }

        [Range(1, 6, ErrorMessage = "Курс 1–6")]
        public short Course { get; set; } = 1;

        [Required(ErrorMessage = "Выберите группу")]
        public Guid StudyGroupId { get; set; }

        public Guid? TeacherId { get; set; }

        public bool ConfirmDuplicate { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var request = new CreateStudentRequest(
            Input.FullName, Input.DepartmentId, Input.Course, Input.StudyGroupId, Input.TeacherId);

        var result = await _students.CreateAsync(request, Input.ConfirmDuplicate, cancellationToken);

        if (result.DuplicateWarning)
        {
            DuplicateStudentId = result.ExistingStudentId;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return Redirect($"/students/{result.StudentId}");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Departments = await _lookups.GetDepartmentsAsync(cancellationToken);
        Groups = await _lookups.GetGroupsAsync(null, cancellationToken);
        Teachers = await _lookups.GetTeachersAsync(cancellationToken);
    }
}
