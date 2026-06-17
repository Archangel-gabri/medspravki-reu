using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Web.Pages.Me;

/// <summary>Личный кабинет студента: показывает ТОЛЬКО свой допуск к физкультуре.
/// Аккаунт привязан к карточке студента через AppUser.StudentId.</summary>
public class IndexModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly IStudentService _students;

    public IndexModel(UserManager<AppUser> users, IStudentService students)
    {
        _users = users;
        _students = students;
    }

    public StudentDetail? Student { get; private set; }

    /// <summary>Аккаунт студента не привязан к карточке (StudentId == null) — показать подсказку.</summary>
    public bool NotLinked { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _users.GetUserAsync(User);
        if (user?.StudentId is not { } studentId)
        {
            NotLinked = true;
            return Page();
        }

        Student = await _students.GetDetailAsync(studentId, cancellationToken);
        if (Student is null)
            NotLinked = true;

        return Page();
    }
}
