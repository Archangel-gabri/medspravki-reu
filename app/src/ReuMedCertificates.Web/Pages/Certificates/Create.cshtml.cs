using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Web.Pages.Certificates;

public class CreateModel : PageModel
{
    private readonly ICertificateService _certificates;
    private readonly IStudentService _students;

    public CreateModel(ICertificateService certificates, IStudentService students)
    {
        _certificates = certificates;
        _students = students;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public Guid StudentId { get; private set; }
    public string StudentName { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Укажите дату начала")]
        [DataType(DataType.Date)]
        public DateOnly StartDate { get; set; }

        [Required(ErrorMessage = "Укажите дату окончания")]
        [DataType(DataType.Date)]
        public DateOnly EndDate { get; set; }

        [DataType(DataType.Date)]
        public DateOnly? IssueDate { get; set; }

        public PhysicalEducationGroup PhysicalGroup { get; set; } = PhysicalEducationGroup.Basic;
        public HealthGroup HealthGroup { get; set; } = HealthGroup.Unknown;
        public string? CertificateNumber { get; set; }
        public string? MedicalOrganization { get; set; }
        public string? Restrictions { get; set; }
        public string? Comment { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null)
            return NotFound();

        StudentId = student.Id;
        StudentName = student.FullName;
        Input.StartDate = DateOnly.FromDateTime(DateTime.UtcNow);
        Input.EndDate = Input.StartDate.AddYears(1);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null)
            return NotFound();

        StudentId = student.Id;
        StudentName = student.FullName;

        if (!ModelState.IsValid)
            return Page();

        try
        {
            var request = new AddCertificateRequest(
                id, Input.StartDate, Input.EndDate, Input.IssueDate,
                Input.HealthGroup, Input.PhysicalGroup,
                string.IsNullOrWhiteSpace(Input.Restrictions) ? null : Input.Restrictions.Trim(),
                string.IsNullOrWhiteSpace(Input.Comment) ? null : Input.Comment.Trim(),
                string.IsNullOrWhiteSpace(Input.CertificateNumber) ? null : Input.CertificateNumber.Trim(),
                string.IsNullOrWhiteSpace(Input.MedicalOrganization) ? null : Input.MedicalOrganization.Trim());

            await _certificates.AddAsync(request, cancellationToken);
            return Redirect($"/students/{id}");
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
