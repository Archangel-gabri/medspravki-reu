using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Students;

namespace ReuMedCertificates.Web.Pages.Students;

public class DetailsModel : PageModel
{
    private readonly IStudentService _students;
    private readonly ICertificateService _certificates;

    public DetailsModel(IStudentService students, ICertificateService certificates)
    {
        _students = students;
        _certificates = certificates;
    }

    public StudentDetail Student { get; private set; } = default!;
    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _students.GetDetailAsync(id, cancellationToken);
        if (detail is null)
            return NotFound();

        Student = detail;
        return Page();
    }

    // Физрук отзывает действующую справку (снимает допуск) с причиной.
    public async Task<IActionResult> OnPostRevokeAsync(Guid id, Guid certificateId, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await _certificates.RevokeAsync(certificateId, reason ?? string.Empty, cancellationToken);
            Message = "Справка отозвана, допуск снят.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        var detail = await _students.GetDetailAsync(id, cancellationToken);
        if (detail is null) return NotFound();
        Student = detail;
        return Page();
    }
}
