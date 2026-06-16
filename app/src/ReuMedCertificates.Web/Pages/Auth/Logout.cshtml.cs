using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Web.Pages.Auth;

public class LogoutModel : PageModel
{
    private readonly SignInManager<AppUser> _signInManager;
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;

    public LogoutModel(SignInManager<AppUser> signInManager, IApplicationDbContext db, IDateTimeProvider clock)
    {
        _signInManager = signInManager;
        _db = db;
        _clock = clock;
    }

    public IActionResult OnGet() => RedirectToPage("/Auth/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        var name = User.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                EntityType = "AppUser",
                ActionType = "Logout",
                UserNameSnapshot = name,
                OccurredAt = _clock.UtcNow,
                Description = $"Выход: {name}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }

        await _signInManager.SignOutAsync();
        return LocalRedirect("/Auth/Login");
    }
}
