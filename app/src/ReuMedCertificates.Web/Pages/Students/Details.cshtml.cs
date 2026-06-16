using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Students;

namespace ReuMedCertificates.Web.Pages.Students;

public class DetailsModel : PageModel
{
    private readonly IStudentService _students;

    public DetailsModel(IStudentService students) => _students = students;

    public StudentDetail Student { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _students.GetDetailAsync(id, cancellationToken);
        if (detail is null)
            return NotFound();

        Student = detail;
        return Page();
    }
}
