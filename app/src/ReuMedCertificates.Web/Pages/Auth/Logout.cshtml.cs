using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Web.Pages.Auth;

public class LogoutModel : PageModel
{
    private readonly SignInManager<AppUser> _signInManager;

    public LogoutModel(SignInManager<AppUser> signInManager) => _signInManager = signInManager;

    public IActionResult OnGet() => RedirectToPage("/Auth/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        await _signInManager.SignOutAsync();
        return LocalRedirect("/Auth/Login");
    }
}
