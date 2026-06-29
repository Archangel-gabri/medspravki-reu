using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Web.Pages.Certificates;

public class EditModel : PageModel
{
    private readonly ICertificateService _certificates;
    private readonly IStudentService _students;

    public EditModel(ICertificateService certificates, IStudentService students)
    {
        _certificates = certificates;
        _students = students;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public Guid StudentId { get; private set; }
    public Guid CertificateId { get; private set; }
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

        public CertificateType Type { get; set; } = CertificateType.Standard086;
        public bool Admitted { get; set; } = true;
        public PhysicalEducationGroup PhysicalGroup { get; set; } = PhysicalEducationGroup.None;
        public HealthGroup HealthGroup { get; set; } = HealthGroup.Unknown;
        public string? CertificateNumber { get; set; }
        public string? MedicalOrganization { get; set; }
        public string? Restrictions { get; set; }
        public string? Comment { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid certId, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null) return NotFound();

        var cert = student.Current.Concat(student.History).FirstOrDefault(c => c.Id == certId);
        if (cert is null) return NotFound();

        StudentId = student.Id;
        CertificateId = certId;
        StudentName = student.FullName;

        Input = new InputModel
        {
            StartDate = cert.StartDate,
            EndDate = cert.EndDate,
            IssueDate = cert.IssueDate,
            Type = cert.Type,
            Admitted = cert.Admitted,
            PhysicalGroup = cert.PhysicalGroup,
            HealthGroup = cert.HealthGroup,
            CertificateNumber = cert.CertificateNumber,
            MedicalOrganization = cert.MedicalOrganization,
            Restrictions = cert.Restrictions,
            Comment = cert.Comment
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, Guid certId, CancellationToken cancellationToken)
    {
        var student = await _students.GetDetailAsync(id, cancellationToken);
        if (student is null) return NotFound();

        StudentId = student.Id;
        CertificateId = certId;
        StudentName = student.FullName;

        if (!ModelState.IsValid) return Page();

        try
        {
            await _certificates.UpdateAsync(new EditCertificateRequest(
                certId, Input.StartDate, Input.EndDate, Input.IssueDate,
                Input.HealthGroup, Input.PhysicalGroup, Input.Type, Input.Admitted,
                Input.CertificateNumber, Input.MedicalOrganization, Input.Restrictions, Input.Comment),
                cancellationToken);
            return Redirect($"/students/{id}");
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
